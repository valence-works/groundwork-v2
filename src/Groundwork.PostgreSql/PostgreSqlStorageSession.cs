using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Store;
using Groundwork.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace Groundwork.PostgreSql;

internal sealed class PostgreSqlStorageSession : IStorageSession, IExactAppendStorageSession, IConcurrencyStorageSession, ICompareAndDeleteStorageSession, IBatchedStorageSession, IRetentionStorageSession, IStorageInspectionSession, IExactRetentionStorageSession, IPrivilegedCrossScopeQuerySession
{
    private readonly PostgreSqlProviderConnection owner;
    private readonly NpgsqlConnection connection;
    private readonly NpgsqlTransaction? transaction;
    private NpgsqlTransaction? activeTransaction;
    private bool closed;

    internal PostgreSqlStorageSession(
        PostgreSqlProviderConnection owner,
        StorageUnit unit,
        StorageAccess access,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        IProviderCommandObserver? observer = null)
    {
        this.owner = owner;
        Unit = unit;
        Access = access;
        this.connection = connection;
        this.transaction = transaction;
        commandObserver = observer;
    }

    /// <summary>
    /// Counts every provider command this session issues. It lives on the session because the session is what
    /// issues commands; it used to be dug out of an individual write's options, which meant a batch observed
    /// only whatever was staged first.
    /// </summary>
    private readonly IProviderCommandObserver? commandObserver;

    private void Observe(string operation, string? commandText, ProviderCommandKind kind, bool isProbe = false) =>
        commandObserver?.Observe(new ProviderCommandEvent(operation, commandText, kind, isProbe));

    public StorageUnit Unit { get; }

    public StorageAccess Access { get; }

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) => Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        StorageAccessValidation.EnsureOrdinaryQuery(Access);
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException($"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.", nameof(request));
        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var executionSource = WithScopePredicate(request);
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(Unit.Key.Columns.Where(name => name != PostgreSqlSchemaCoordinator.ScopeColumn).Select(QueryColumn).Where(column => column is not null)!.Select(column => column!)) with
        {
            Indexes = SearchKeyQueryMappings.RetargetIndexes(Unit, suppliedOptions.Indexes)
                .Select(index => index.WithColumnTypes(Unit.Columns.ToDictionary(column => column.Name, column => QueryTypeOf(column.Type), StringComparer.Ordinal))).ToImmutableArray(),
            PhysicalIndexNames = Unit.Indexes.ToDictionary(
                index => index.Name,
                index => PostgreSqlDialect.PhysicalIndexName(Unit.Name, index.Name),
                StringComparer.Ordinal),
            SearchKeyColumns = SearchKeyQueryMappings.For(Unit)
        };
        var executionRequest = QueryRequestExecution.ForPage(executionSource, renderOptions);
        var command = new PostgreSqlQueryRenderer().Render(executionRequest, renderOptions);
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.query", command.CommandText, ProviderCommandKind.Read, IsProbe: false));
        var rows = RelationalQueryResultReader.Read(connection, command, (name, value) =>
        {
            if (name == "__groundwork_total_count") return value;
            var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
            return column is null ? value : FromDatabase(value ?? DBNull.Value, column);
        }, activeTransaction ?? transaction);
        AssertExplainPlan(command, renderOptions);
        return QueryResultMaterializer.Materialize(executionSource, renderOptions, rows, command.SelectedIndex, command.IndexHintApplied,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true);
    });

    public CrossScopeQueryResult QueryAcrossScopes(
        QueryRequest request,
        QueryRenderOptions? options = null) => Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Access.IsPrivilegedAcrossScopes)
            throw new InvalidOperationException(
                "GW-ACCESS-001: cross-scope queries require explicit privileged across-scope access.");
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.",
                nameof(request));
        StorageAccessValidation.ObservePrivilegedQuery(Access, Unit);

        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var scopeToken = new ColumnRef(
            new TableId(Unit.Name),
            CrossScopeQueryMaterializer.ScopeTokenColumn,
            QueryType.String,
            isNullable: false);
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(
            new[] { scopeToken }
                .Concat(Unit.Key.Columns
                    .Where(name => name != PostgreSqlSchemaCoordinator.ScopeColumn)
                    .Select(QueryColumn)
                    .Where(column => column is not null)
                    .Select(column => column!))) with
        {
            Indexes = SearchKeyQueryMappings.RetargetIndexes(Unit, suppliedOptions.Indexes)
                .Select(index => index.WithColumnTypes(Unit.Columns.ToDictionary(
                    column => column.Name,
                    column => QueryTypeOf(column.Type),
                    StringComparer.Ordinal)))
                .ToImmutableArray(),
            PhysicalIndexNames = Unit.Indexes.ToDictionary(
                index => index.Name,
                index => PostgreSqlDialect.PhysicalIndexName(Unit.Name, index.Name),
                StringComparer.Ordinal),
            SearchKeyColumns = SearchKeyQueryMappings.For(Unit),
            LatestPartitionColumns = [scopeToken]
        };
        var executionSource = QueryRequestExecution.WithProviderPredicate(
            request,
            request.Where,
            CrossScopeQueryMaterializer.BindingDiscriminator(Access));
        var executionRequest = EnsureScopeProjection(
            QueryRequestExecution.ForPage(executionSource, renderOptions));
        var command = new PostgreSqlQueryRenderer().Render(executionRequest, renderOptions);
        var rows = RelationalQueryResultReader.Read(connection, command, (name, value) =>
        {
            if (name == "__groundwork_total_count") return value;
            var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
            return column is null ? value : FromDatabase(value ?? DBNull.Value, column);
        });
        AssertExplainPlan(command, renderOptions);
        var materialized = QueryResultMaterializer.Materialize(
            executionSource,
            renderOptions,
            rows,
            command.SelectedIndex,
            command.IndexHintApplied,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true);
        return CrossScopeQueryMaterializer.FromNativePage(
            materialized,
            rows,
            PostgreSqlSchemaCoordinator.ScopeColumn);
    });

    public AggregationResult Aggregate(AggregationQuery query) => Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(query);
        StorageAccessValidation.EnsurePointOperation(Access, "aggregate");
        var profile = AggregationProfileValidator.ResolveOrThrow(Unit, query.ProfileName);
        var decode = (string name, object? value) =>
        {
            var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
            return column is null ? value : FromDatabase(value ?? DBNull.Value, column);
        };
        return Unit.Scope == ScopePolicy.Scoped
            ? RelationalAggregationExecutor.ExecuteScoped(
                connection,
                activeTransaction ?? transaction,
                new PostgreSqlDialect(),
                Unit,
                profile,
                query,
                decode,
                PostgreSqlSchemaCoordinator.ScopeColumn,
                Access.Scope!,
                commandObserver,
                "postgresql.aggregate")
            : RelationalAggregationExecutor.Execute(
            connection,
            activeTransaction ?? transaction,
            new PostgreSqlDialect(),
            Unit,
            profile,
            query,
            decode,
            commandObserver,
            "postgresql.aggregate");
    });

    private void AssertExplainPlan(RelationalQueryCommand query, QueryRenderOptions options)
    {
        if (query.IsMatchNone || !ExplainAssertionMode.ShouldAssert(query.SelectedIndex)) return;
        var logicalIndex = query.SelectedIndex!;
        var physicalIndex = options.ResolvePhysicalIndexName(logicalIndex);
        using var explain = Command("EXPLAIN (FORMAT JSON) " + query.CommandText.TrimEnd().TrimEnd(';'));
        RelationalQueryResultReader.AddParameters(explain, query);
        var rawPlan = Convert.ToString(explain.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
        ExplainAssertionMode.AssertChosenIndex(
            "PostgreSQL", logicalIndex, physicalIndex, query.IndexHintApplied, rawPlan,
            PostgreSqlExplainPlanInspector.ChoseIndex(rawPlan, physicalIndex));
    }

    private QueryRequest WithScopePredicate(QueryRequest request) => Unit.Scope != ScopePolicy.Scoped
        ? request
        : QueryRequestExecution.WithProviderPredicate(request, new Predicate.And([
            request.Where,
            new Predicate.Equal(new ColumnRef(new TableId(Unit.Name), PostgreSqlSchemaCoordinator.ScopeColumn, QueryType.String),
                QueryConstant.Of(new ColumnRef(new TableId(Unit.Name), PostgreSqlSchemaCoordinator.ScopeColumn, QueryType.String), Access.Scope!.Value))]),
            QueryRequestExecution.ScopeBindingDiscriminator(Access.Scope!.Value));

    public StoredEntry? Read(StorageKey key)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "read");
        return Execute(() => PublicEntry(ReadCore(key, observerOperation: "postgresql.read", isProbe: false)));
    }

    private QueryRequest EnsureScopeProjection(QueryRequest request)
    {
        if (request.Projection.AllColumns || request.Projection.Columns.Any(column =>
                string.Equals(column.Name, PostgreSqlSchemaCoordinator.ScopeColumn, StringComparison.Ordinal)))
            return request;
        var scope = new ColumnRef(
            new TableId(Unit.Name),
            PostgreSqlSchemaCoordinator.ScopeColumn,
            QueryType.String,
            isNullable: false);
        return QueryRequestExecution.WithProjection(
            request,
            Projection.ColumnsOnly([.. request.Projection.Columns, scope]));
    }

    private static StoredEntry? PublicEntry(StoredEntry? entry) => entry is null
        ? null
        : new StoredEntry(new StorageValues(SearchKeyProjection.PublicValues(entry.Values.Values)), entry.Version);

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Insert, options);
        return Mutate(values, options, Mutation.Insert);
    }

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Update, options);
        return Mutate(values, options, Mutation.Update);
    }

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Upsert, options);
        return Mutate(values, options, Mutation.Upsert);
    }

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, options);
        var outcome = Execute(() => ConditionalUpsertCore(values, options));
        if (outcome.Status == WriteOutcomeStatus.Inserted && Unit.Retention?.Trigger == RetentionTrigger.OnAppend)
            ApplyOnAppendRetention();
        return outcome;
    }

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes)
    {
        var nativeOnAppend = IsNativeAppendBatch(writes);
        var outcomes = ExecuteWrite(() => ApplyBatchCore(writes));
        if (nativeOnAppend && OnAppendRetentionCoordinator.ContainsAppend(outcomes))
            ApplyOnAppendRetention();
        return outcomes;
    }

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null)
    {
        WritePreconditionValidator.Validate(Unit, WriteOperation.Delete, options);
        return ExecuteWrite(() =>
        {
            if (Unit.Concurrency.IsNone)
            {
                var (noneWhere, noneParameters) = KeyPredicate(key.Values);
                using var noneCommand = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {noneWhere};");
                AddParameters(noneCommand, noneParameters);
                commandObserver?.Observe(new ProviderCommandEvent("postgresql.delete", noneCommand.CommandText, ProviderCommandKind.Write, IsProbe: false));
                return noneCommand.ExecuteNonQuery() == 0
                    ? new WriteOutcome(WriteOutcomeStatus.NotFound)
                    : new WriteOutcome(WriteOutcomeStatus.Deleted);
            }

            var existing = ReadCore(key);
            ValidateExpected(options, existing, Mutation.Delete);
            if (existing is null)
                return new WriteOutcome(WriteOutcomeStatus.NotFound);
            var (where, parameters) = KeyPredicate(key.Values);
            if (VersionColumn is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumn.Name)}=@expected";
                parameters["@expected"] = options.Precondition.Version!.Value;
            }
            using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
            AddParameters(command, parameters);
            commandObserver?.Observe(new ProviderCommandEvent("postgresql.delete", command.CommandText, ProviderCommandKind.Write, IsProbe: false));
            var affected = command.ExecuteNonQuery();
            return affected == 0
                ? new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version)
                : new WriteOutcome(WriteOutcomeStatus.Deleted, existing.Version);
        });
    }

    public WriteOutcome CompareAndDelete(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null)
    {
        var canonicalKey = CompareAndDeleteValidation.CanonicalizeKey(Unit, key);
        var expected = CompareAndDeleteValidation.Validate(Unit, canonicalKey, expectedValues, options);
        return ExecuteWrite(() =>
        {
            // Lock the identity row in the current transaction before classifying a
            // zero-row delete. This keeps absence and comparison mismatch tied to one
            // serializable provider decision even when the surrounding UOW uses
            // PostgreSQL's default ReadCommitted isolation.
            var existing = ReadCore(canonicalKey, forUpdate: true, observerOperation: "postgresql.compare-and-delete-read");
            if (existing is null)
                return new WriteOutcome(WriteOutcomeStatus.NotFound);
            if (options?.Precondition.Kind == WritePreconditionKind.IfVersion &&
                options.Precondition.Version != existing.Version)
                return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            if (!MatchesExpected(existing, expected))
                return new WriteOutcome(WriteOutcomeStatus.ComparisonMismatch, existing.Version);

            var (where, parameters) = KeyPredicate(canonicalKey.Values);
            foreach (var pair in expected)
            {
                if (pair.Value is null)
                {
                    where += $" AND {Quote(pair.Key)} IS NULL";
                }
                else
                {
                    var parameter = "@compare_" + pair.Key;
                    where += $" AND {Quote(pair.Key)}={parameter}";
                    parameters[parameter] = ConvertValue(pair.Value, Column(pair.Key));
                }
            }
            if (VersionColumn is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumn.Name)}=@expected";
                parameters["@expected"] = options.Precondition.Version!.Value;
            }

            using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
            AddParameters(command, parameters);
            commandObserver?.Observe(new ProviderCommandEvent("postgresql.compare-and-delete", command.CommandText, ProviderCommandKind.Write, IsProbe: false));
            return command.ExecuteNonQuery() != 0
                ? new WriteOutcome(WriteOutcomeStatus.Deleted, existing.Version)
                : new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
        });
    }

    private bool MatchesExpected(StoredEntry existing, IReadOnlyDictionary<string, object?> expected) =>
        expected.All(pair =>
        {
            var definition = Column(pair.Key);
            return existing.Values.Values.TryGetValue(pair.Key, out var actual) &&
                CompareAndDeleteValidation.ValuesEqual(actual, pair.Value, definition.Type);
        });

    public RetentionResult ApplyRetention(RetentionExecutionOptions? options = null) =>
        ExecuteWrite(() => ApplyRetentionCore(options ?? new RetentionExecutionOptions()));

    private RetentionResult ApplyRetentionCore(RetentionExecutionOptions options)
    {
        if (options.MaxRowsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxRowsPerBatch));
        var declaration = Unit.Retention ??
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare retention.");
        var keepNewest = RetentionSessionExtensions.EffectiveKeepNewest(Unit, options);
        var keyColumns = Unit.Key.Columns;
        var partition = declaration.PartitionColumns.Count == 0
            ? string.Empty
            : $"PARTITION BY {string.Join(", ", declaration.PartitionColumns.Select(Quote))} ";
        var scope = Unit.Columns.Any(column => column.Name == PostgreSqlSchemaCoordinator.ScopeColumn)
            ? $" WHERE {Quote(PostgreSqlSchemaCoordinator.ScopeColumn)}=@__groundwork_scope"
            : string.Empty;
        var keys = string.Join(", ", keyColumns.Select(Quote));
        var ordering = string.Join(", ", [
            $"{Quote(declaration.OrderColumn)} DESC",
            .. keyColumns.Select(column => $"{Quote(column)} ASC")]);
        var equality = string.Join(" AND ", keyColumns.Select(column =>
            $"target.{Quote(column)}=victim.{Quote(column)}"));
        var deleted = 0;
        var batches = 0;
        while (true)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            using var command = Command($"WITH ranked AS (" +
                $"SELECT {keys}, ROW_NUMBER() OVER ({partition}ORDER BY {ordering}) AS __groundwork_retention_rank " +
                $"FROM {Quote(Unit.Name)}{scope}), victims AS (" +
                $"SELECT {keys} FROM ranked WHERE __groundwork_retention_rank > @keep LIMIT @limit) " +
                $"DELETE FROM {Quote(Unit.Name)} AS target USING victims AS victim WHERE {equality};");
            Add(command, "keep", keepNewest);
            Add(command, "limit", options.MaxRowsPerBatch);
            if (Unit.Columns.Any(column => column.Name == PostgreSqlSchemaCoordinator.ScopeColumn))
                Add(command, "__groundwork_scope", Access.Scope!.Value);
            var affected = command.ExecuteNonQuery();
            commandObserver?.Observe(new ProviderCommandEvent("postgresql.retention-delete", command.CommandText, ProviderCommandKind.Write, IsProbe: false));
            if (affected == 0)
                break;
            deleted += affected;
            batches++;
            if (affected < options.MaxRowsPerBatch)
                break;
        }
        return new RetentionResult(deleted, batches);
    }

    public StorageInspection Inspect() => Execute(() =>
    {
        StorageAccessValidation.EnsurePointOperation(Access, "inspect");
        StorageInspectionSessionExtensions.EnsureProviderSequence(Unit);
        EnsureHighWaterTable();
        using var command = Command($"SELECT {Quote(HighWaterValue)} FROM {Quote(HighWaterTable)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope;");
        Add(command, "unit", Unit.Id.Value);
        Add(command, "scope", Access.Scope?.Value ?? string.Empty);
        var value = command.ExecuteScalar();
        return value is null or DBNull
            ? new StorageInspection(null)
            : new StorageInspection(Convert.ToInt64(value, CultureInfo.InvariantCulture));
    });

    public RetentionOperationResult ApplyRetention(OperationId operationId, RetentionExecutionOptions? options = null)
    {
        var declaration = Unit.RetentionIdempotency ?? throw new InvalidOperationException(
            $"Storage unit '{Unit.Name}' does not declare retention idempotency; declare RetentionIdempotency before using operation-identified retention.");
        declaration.Validate(Unit);
        options ??= new RetentionExecutionOptions();
        RetentionSessionExtensions.ValidateExecutionOptions(options);
        RetentionOperationCodec.ValidateOperation(operationId);
        return ExecuteWrite(() => ApplyExactRetentionCore(operationId, declaration, options));
    }

    private RetentionOperationResult ApplyExactRetentionCore(
        OperationId operationId,
        RetentionIdempotencyDeclaration declaration,
        RetentionExecutionOptions options)
    {
        EnsureLedgerTable(declaration.LedgerName);
        var providerNow = ProviderNow();
        var scope = Access.Scope?.Value ?? string.Empty;
        var fingerprint = RetentionOperationCodec.Fingerprint(Unit, options);
        var cutoff = IdempotencyRules.ReclamationCutoff(providerNow, declaration.Window);
        using (var reclaim = Command($"DELETE FROM {Quote(declaration.LedgerName)} WHERE ctid IN (SELECT ctid FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@reclaim_unit AND {Quote(LedgerCommittedAt)} <= @cutoff LIMIT 128);"))
        {
            Add(reclaim, "reclaim_unit", Unit.Id.Value);
            Add(reclaim, "cutoff", FormatLedgerTime(cutoff));
            reclaim.ExecuteNonQuery();
        }

        var existing = ReadRetentionLedger(declaration.LedgerName, operationId, scope);
        if (existing is not null)
        {
            var (committedAt, storedFingerprint, storedResult) = existing.Value;
            if (IdempotencyRules.IsWithinWindow(committedAt, providerNow, declaration.Window))
            {
                if (string.IsNullOrEmpty(storedFingerprint) || string.IsNullOrEmpty(storedResult))
                    throw new InvalidOperationException("GW-RETENTION-002: an existing exact retention ledger entry has no exact result; use a new operation nonce.");
                if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new RetentionIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, storedFingerprint, fingerprint);
                return RetentionOperationCodec.DeserializeResult(storedResult) with { Status = RetentionOperationStatus.Replayed };
            }

            using var deleteExpired = Command($"DELETE FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(deleteExpired, Unit.Id.Value, scope, operationId.Nonce);
            deleteExpired.ExecuteNonQuery();
        }

        using (var insertLedger = Command($"INSERT INTO {Quote(declaration.LedgerName)} ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}, {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)}) VALUES (@unit, @scope, @nonce, @committed_at, @fingerprint, @result) ON CONFLICT ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}) DO NOTHING;"))
        {
            AddLedgerParameters(insertLedger, Unit.Id.Value, scope, operationId.Nonce);
            Add(insertLedger, "committed_at", FormatLedgerTime(providerNow));
            Add(insertLedger, "fingerprint", fingerprint);
            Add(insertLedger, "result", string.Empty);
            if (insertLedger.ExecuteNonQuery() == 0)
            {
                var raced = ReadRetentionLedger(declaration.LedgerName, operationId, scope);
                if (raced is null || string.IsNullOrEmpty(raced.Value.storedFingerprint) || string.IsNullOrEmpty(raced.Value.storedResult))
                    throw new InvalidOperationException("GW-RETENTION-002: an existing exact retention ledger entry has no exact result; use a new operation nonce.");
                if (!string.Equals(raced.Value.storedFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new RetentionIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, raced.Value.storedFingerprint, fingerprint);
                return RetentionOperationCodec.DeserializeResult(raced.Value.storedResult) with { Status = RetentionOperationStatus.Replayed };
            }
        }

        var retention = ApplyRetentionCore(options);
        var result = new RetentionOperationResult(RetentionOperationStatus.Executed, retention.DeletedRows, retention.Batches, retention.Completed);
        using var complete = Command($"UPDATE {Quote(declaration.LedgerName)} SET {Quote(LedgerResult)}=@result WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
        AddLedgerParameters(complete, Unit.Id.Value, scope, operationId.Nonce);
        Add(complete, "result", RetentionOperationCodec.SerializeResult(result));
        complete.ExecuteNonQuery();
        return result;
    }

    private (DateTimeOffset committedAt, string? storedFingerprint, string? storedResult)? ReadRetentionLedger(string table, OperationId operationId, string scope)
    {
        using var command = Command($"SELECT {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} FROM {Quote(table)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
        AddLedgerParameters(command, Unit.Id.Value, scope, operationId.Nonce);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return (
            DateTimeOffset.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture));
    }

    public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        var declaration = IdempotencyRules.RequireDeclaration(Unit);
        IdempotencyRules.ValidateOperation(Unit, operationId, values);
        foreach (var value in values)
            WritePreconditionValidator.ValidateSystemOwnedValues(Unit, value.Values);
        var execution = ExecuteWrite(() => AppendCore(operationId, values, declaration, exactOutcomes: false));
        if (Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            execution.Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed)
            ApplyOnAppendRetention();
        return new WriteOutcome(execution.Status);
    }

    public AppendOutcomeReport AppendWithOutcomes(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        var declaration = IdempotencyRules.RequireDeclaration(Unit);
        IdempotencyRules.ValidateOperation(Unit, operationId, values);
        foreach (var value in values)
            WritePreconditionValidator.ValidateSystemOwnedValues(Unit, value.Values);
        var outcome = ExecuteWrite(() => AppendCore(operationId, values, declaration, exactOutcomes: true).ToReport());
        if (Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            outcome.Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed)
            ApplyOnAppendRetention();
        return outcome;
    }

    private AppendExecution AppendCore(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        AppendIdempotencyDeclaration declaration,
        bool exactOutcomes)
    {
        EnsureLedgerTable(declaration.LedgerName);
        var providerNow = ProviderNow();
        var scope = Access.Scope?.Value ?? string.Empty;
        var fingerprint = ExactAppendCodec.Fingerprint(Unit, values);
        var cutoff = IdempotencyRules.ReclamationCutoff(providerNow, declaration.Window);
        using (var reclaim = Command($"DELETE FROM {Quote(declaration.LedgerName)} WHERE ctid IN (SELECT ctid FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@reclaim_unit AND {Quote(LedgerCommittedAt)} <= @cutoff LIMIT 128);"))
        {
            Add(reclaim, "reclaim_unit", Unit.Id.Value);
            Add(reclaim, "cutoff", FormatLedgerTime(cutoff));
            reclaim.ExecuteNonQuery();
        }

        var expiredExisting = false;
        using (var existing = Command($"SELECT {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;"))
        {
            AddLedgerParameters(existing, Unit.Id.Value, scope, operationId.Nonce);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var committedAt = DateTimeOffset.Parse(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (IdempotencyRules.IsWithinWindow(committedAt, providerNow, declaration.Window))
                {
                    var storedFingerprint = reader.IsDBNull(1) ? null : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture);
                    var storedResult = reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(storedFingerprint) || string.IsNullOrEmpty(storedResult))
                    {
                        if (!exactOutcomes)
                            return new AppendExecution(WriteOutcomeStatus.Replayed, null);
                        throw new InvalidOperationException("GW-APPEND-002: an existing append ledger entry has no exact result; use a new operation nonce.");
                    }
                    if (!exactOutcomes)
                        return new AppendExecution(WriteOutcomeStatus.Replayed, ExactAppendCodec.DeserializeOutcomes(storedResult));
                    if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
                        throw new AppendIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, storedFingerprint, fingerprint);
                    return new AppendExecution(WriteOutcomeStatus.Replayed, ExactAppendCodec.DeserializeOutcomes(storedResult));
                }
                expiredExisting = true;
            }
        }
        if (expiredExisting)
        {
            using var deleteExpired = Command($"DELETE FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
            AddLedgerParameters(deleteExpired, Unit.Id.Value, scope, operationId.Nonce);
            deleteExpired.ExecuteNonQuery();
        }

        using (var insertLedger = Command($"INSERT INTO {Quote(declaration.LedgerName)} ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}, {Quote(LedgerCommittedAt)}, {Quote(LedgerFingerprint)}, {Quote(LedgerResult)}) VALUES (@unit, @scope, @nonce, @committed_at, @fingerprint, @result) ON CONFLICT ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}) DO NOTHING;"))
        {
            AddLedgerParameters(insertLedger, Unit.Id.Value, scope, operationId.Nonce);
            Add(insertLedger, "committed_at", FormatLedgerTime(providerNow));
            Add(insertLedger, "fingerprint", fingerprint);
            Add(insertLedger, "result", string.Empty);
            if (insertLedger.ExecuteNonQuery() == 0)
            {
                using var replay = Command($"SELECT {Quote(LedgerFingerprint)}, {Quote(LedgerResult)} FROM {Quote(declaration.LedgerName)} WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;");
                AddLedgerParameters(replay, Unit.Id.Value, scope, operationId.Nonce);
                using var reader = replay.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1) || string.IsNullOrEmpty(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)))
                {
                    if (!exactOutcomes)
                        return new AppendExecution(WriteOutcomeStatus.Replayed, null);
                    throw new InvalidOperationException("GW-APPEND-002: an existing append ledger entry has no exact result; use a new operation nonce.");
                }
                var storedFingerprint = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
                if (!exactOutcomes)
                    return new AppendExecution(WriteOutcomeStatus.Replayed, ExactAppendCodec.DeserializeOutcomes(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!));
                if (!string.Equals(storedFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new AppendIdempotencyConflictException(Unit.Id.Value, scope, operationId.Nonce, storedFingerprint, fingerprint);
                return new AppendExecution(WriteOutcomeStatus.Replayed, ExactAppendCodec.DeserializeOutcomes(Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture)!));
            }
        }

        var logicalUnit = IdempotencyRules.LogicalUnit(Unit, PostgreSqlSchemaCoordinator.ScopeColumn);
        var writes = values
            .Select(value => RowWrite.Insert(logicalUnit, value))
            .ToArray();
        var outcomes = SequenceColumn is not null
            ? writes.Select(InsertAppendSequence).ToArray()
            : ApplyBatchCore(writes);
        if (outcomes.Any(outcome => !outcome.Outcome.Succeeded))
            throw new InvalidOperationException("An idempotent append payload row was not accepted; the ledger and payload were rolled back.");
        var report = new AppendExecution(WriteOutcomeStatus.Inserted, outcomes.Select(outcome => outcome.Outcome).ToArray());
        using (var complete = Command($"UPDATE {Quote(declaration.LedgerName)} SET {Quote(LedgerResult)}=@result WHERE {Quote(LedgerUnit)}=@unit AND {Quote(LedgerScope)}=@scope AND {Quote(LedgerNonce)}=@nonce;"))
        {
            AddLedgerParameters(complete, Unit.Id.Value, scope, operationId.Nonce);
            Add(complete, "result", ExactAppendCodec.SerializeOutcomes(report.Outcomes!));
            complete.ExecuteNonQuery();
        }
        return report;
    }

    private RowWriteOutcome InsertAppendSequence(RowWrite write)
    {
        var values = new StorageValues(SearchKeyProjection.Populate(Unit, write.Values!.Values));
        ValidateValues(values.Values, requireAllNonNullable: true);
        return new RowWriteOutcome(write, InsertCore(values));
    }

    private void EnsureLedgerTable(string table)
    {
        using var command = Command($"CREATE TABLE IF NOT EXISTS {Quote(table)} (" +
            $"{Quote(LedgerUnit)} text NOT NULL, " +
            $"{Quote(LedgerScope)} text NOT NULL, " +
            $"{Quote(LedgerNonce)} text NOT NULL, " +
            $"{Quote(LedgerCommittedAt)} text NOT NULL, " +
            $"{Quote(LedgerFingerprint)} text NULL, " +
            $"{Quote(LedgerResult)} text NULL, " +
            $"PRIMARY KEY ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(LedgerNonce)}));");
        command.ExecuteNonQuery();

        EnsureLedgerColumn(table, LedgerFingerprint);
        EnsureLedgerColumn(table, LedgerResult);

        using var cleanupIndex = Command($"CREATE INDEX IF NOT EXISTS {Quote(IdempotencyRules.CleanupIndexName(table))} " +
            $"ON {Quote(table)} ({Quote(LedgerUnit)}, {Quote(LedgerCommittedAt)});");
        cleanupIndex.ExecuteNonQuery();
    }

    private void EnsureLedgerColumn(string table, string column)
    {
        using var alter = Command($"ALTER TABLE {Quote(table)} ADD COLUMN IF NOT EXISTS {Quote(column)} text NULL;");
        alter.ExecuteNonQuery();
    }

    private void EnsureHighWaterTable()
    {
        using var command = Command($"CREATE TABLE IF NOT EXISTS {Quote(HighWaterTable)} (" +
            $"{Quote(LedgerUnit)} text NOT NULL, " +
            $"{Quote(LedgerScope)} text NOT NULL, " +
            $"{Quote(HighWaterValue)} bigint NOT NULL, " +
            $"PRIMARY KEY ({Quote(LedgerUnit)}, {Quote(LedgerScope)}));");
        command.ExecuteNonQuery();
    }

    private void RecordHighWater(object? generatedValue)
    {
        if (SequenceColumn is null || generatedValue is null)
            return;
        EnsureHighWaterTable();
        using var command = Command($"INSERT INTO {Quote(HighWaterTable)} ({Quote(LedgerUnit)}, {Quote(LedgerScope)}, {Quote(HighWaterValue)}) VALUES (@unit, @scope, @value) ON CONFLICT ({Quote(LedgerUnit)}, {Quote(LedgerScope)}) DO UPDATE SET {Quote(HighWaterValue)}=GREATEST({Quote(HighWaterTable)}.{Quote(HighWaterValue)}, EXCLUDED.{Quote(HighWaterValue)});");
        Add(command, "unit", Unit.Id.Value);
        Add(command, "scope", Access.Scope?.Value ?? string.Empty);
        Add(command, "value", Convert.ToInt64(generatedValue, CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private void AddLedgerParameters(NpgsqlCommand command, string unit, string scope, string nonce)
    {
        Add(command, "unit", unit);
        Add(command, "scope", scope);
        Add(command, "nonce", nonce);
    }

    private DateTimeOffset ProviderNow()
    {
        using var command = Command("SELECT clock_timestamp();");
        var value = command.ExecuteScalar();
        return value switch
        {
            DateTimeOffset timestamp => timestamp.ToUniversalTime(),
            DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
        };
    }

    private static string FormatLedgerTime(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    internal void Close() => closed = true;

    private IReadOnlyList<RowWriteOutcome> ApplyBatchCore(IReadOnlyList<RowWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return [];
        if (SequenceColumn is not null)
            return ApplyBatchFallback(writes);
        if (writes.Any(write => write.Options.Precondition.Kind != WritePreconditionKind.Unconditional))
            return ApplyBatchFallback(writes);
        if (HasSecondaryUniqueIndex(writes[0].Unit))
            return ApplyBatchFallback(writes);
        var physicalWrites = writes.Select(write => write.PopulateSearchKeyValues()).ToArray();
        return physicalWrites[0].Mode switch
        {
            RowWriteMode.Insert => ApplyInsertBatch(physicalWrites),
            RowWriteMode.Upsert => ApplyUpsertBatch(physicalWrites),
            _ => ApplyBatchFallback(writes)
        };
    }

    private bool IsNativeAppendBatch(IReadOnlyList<RowWrite> writes) =>
        Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
        writes.Count != 0 &&
        SequenceColumn is null &&
        writes.All(write => write.Options.Precondition.Kind == WritePreconditionKind.Unconditional) &&
        !HasSecondaryUniqueIndex(writes[0].Unit) &&
        writes.Select(write => write.ColumnSet).Distinct(StringComparer.Ordinal).Count() == 1 &&
        writes[0].Mode is RowWriteMode.Insert or RowWriteMode.Upsert;

    private IReadOnlyList<RowWriteOutcome> ApplyInsertBatch(IReadOnlyList<RowWrite> writes)
    {
        var columns = PhysicalValues(writes[0].Values!.Values, includeVersion: VersionColumn is not null).Keys.ToArray();
        foreach (var write in writes)
        {
            ValidateValues(write.Values!.Values, requireAllNonNullable: true);
            if (!PhysicalValues(write.Values.Values, includeVersion: VersionColumn is not null).Keys.SequenceEqual(columns, StringComparer.Ordinal))
                return ApplyBatchFallback(writes);
        }
        var maxRows = Math.Max(1, Math.Min(1_000, 32_000 / columns.Length));
        if (writes.Count > maxRows)
            return writes.Chunk(maxRows).SelectMany(ApplyInsertBatch).ToArray();
        using var command = Command(string.Empty);
        var rows = AddBatchValues(command, writes, columns);
        var returning = string.Join(", ", Unit.Key.Columns.Select(Quote).Concat(
            VersionColumn is null ? [] : [Quote(VersionColumn.Name)]));
        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) VALUES {string.Join(", ", rows)} ON CONFLICT DO NOTHING RETURNING {returning};";
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.batch-insert", "PostgreSQL multi-row INSERT", ProviderCommandKind.Write, IsProbe: false));
        try
        {
            var returned = ReadReturnedRows(command, writes[0].Unit);
            return writes.Select(write => new RowWriteOutcome(write,
                returned.TryGetValue(write.Identity, out var version)
                    ? new WriteOutcome(WriteOutcomeStatus.Inserted, version)
                    : new WriteOutcome(WriteOutcomeStatus.UniqueViolation))).ToArray();
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private IReadOnlyList<RowWriteOutcome> ApplyUpsertBatch(IReadOnlyList<RowWrite> writes)
    {
        var columns = PhysicalValues(writes[0].Values!.Values, includeVersion: VersionColumn is not null).Keys.ToArray();
        foreach (var write in writes)
        {
            ValidateValues(write.Values!.Values, requireAllNonNullable: false);
            if (!PhysicalValues(write.Values.Values, includeVersion: VersionColumn is not null).Keys.SequenceEqual(columns, StringComparer.Ordinal))
                return ApplyBatchFallback(writes);
        }
        var maxRows = Math.Max(1, Math.Min(1_000, 32_000 / columns.Length));
        if (writes.Count > maxRows)
            return writes.Chunk(maxRows).SelectMany(ApplyUpsertBatch).ToArray();
        using var command = Command(string.Empty);
        var rows = AddBatchValues(command, writes, columns);
        var conflictPredicate = PartialKeyPredicate();
        var conflict = $"({string.Join(", ", Unit.Key.Columns.Select(Quote))})" +
            (conflictPredicate is null ? string.Empty : $" WHERE {conflictPredicate}");
        var updates = columns
            .Where(column => !Unit.Key.Columns.Contains(column, StringComparer.Ordinal) &&
                             column != PostgreSqlSchemaCoordinator.ScopeColumn &&
                             column != "createdAt" &&
                             column != PostgreSqlSchemaCoordinator.VersionColumn)
            .Select(column => $"{Quote(column)}=EXCLUDED.{Quote(column)}")
            .ToList();
        if (VersionColumn is not null)
            updates.Add($"{Quote(VersionColumn.Name)}={Quote(Unit.Name)}.{Quote(VersionColumn.Name)}+1");
        if (updates.Count == 0)
            updates.Add($"{Quote(Unit.Key.Columns[0])}={Quote(Unit.Name)}.{Quote(Unit.Key.Columns[0])}");
        var returning = string.Join(", ", Unit.Key.Columns.Select(Quote).Concat(
            VersionColumn is null ? [] : [Quote(VersionColumn.Name)]));
        command.CommandText = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) VALUES {string.Join(", ", rows)} ON CONFLICT {conflict} DO UPDATE SET {string.Join(", ", updates)} RETURNING {returning};";
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.batch-upsert", "PostgreSQL multi-row INSERT ON CONFLICT", ProviderCommandKind.Write, IsProbe: false));
        try
        {
            var returned = ReadReturnedRows(command, writes[0].Unit);
            return writes.Select(write => new RowWriteOutcome(write,
                returned.TryGetValue(write.Identity, out var version)
                    ? new WriteOutcome(WriteOutcomeStatus.Upserted, version)
                    : new WriteOutcome(WriteOutcomeStatus.UniqueViolation))).ToArray();
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private List<string> AddBatchValues(NpgsqlCommand command, IReadOnlyList<RowWrite> writes, IReadOnlyList<string> columns)
    {
        var rows = new List<string>(writes.Count);
        for (var row = 0; row < writes.Count; row++)
        {
            var physical = PhysicalValues(writes[row].Values!.Values, includeVersion: VersionColumn is not null);
            var parameters = new List<string>(columns.Count);
            foreach (var column in columns)
            {
                var name = $"r{row}_{column}";
                parameters.Add("@" + name);
                Add(command, name, physical[column], column);
            }
            rows.Add($"({string.Join(", ", parameters)})");
        }
        return rows;
    }

    private Dictionary<string, long?> ReadReturnedRows(NpgsqlCommand command, StorageUnit logicalUnit)
    {
        var returned = new Dictionary<string, long?>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < Unit.Key.Columns.Count; index++)
            {
                var column = Unit.Key.Columns[index];
                if (column == PostgreSqlSchemaCoordinator.ScopeColumn)
                    values[column] = Access.Scope!.Value;
                else
                    values[column] = FromDatabase(reader.GetValue(index), Column(column));
            }
            var versionOrdinal = Unit.Key.Columns.Count;
            var version = VersionColumn is null || reader.IsDBNull(versionOrdinal)
                ? (long?)null
                : Convert.ToInt64(reader.GetValue(versionOrdinal), CultureInfo.InvariantCulture);
            returned[RowWrite.IdentityFor(logicalUnit, values)] = version;
        }
        return returned;
    }


    private IReadOnlyList<RowWriteOutcome> ApplyBatchFallback(IReadOnlyList<RowWrite> writes) =>
        writes.Select(write => new RowWriteOutcome(write, write.Mode switch
        {
            RowWriteMode.Insert => Insert(write.Values!, write.Options),
            RowWriteMode.Update => Update(write.Values!, write.Options),
            RowWriteMode.Upsert when write.Options.Precondition.Kind == WritePreconditionKind.IfVersion => ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.Upsert => Upsert(write.Values!, write.Options),
            RowWriteMode.ConditionalUpsert => ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.Delete => Delete(write.Key!, write.Options),
            RowWriteMode.CompareAndDelete => CompareAndDelete(write.Key!, write.ExpectedValues, write.Options),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        })).ToArray();

    private static bool HasSecondaryUniqueIndex(StorageUnit logicalUnit) =>
        logicalUnit.Indexes.Any(index => index.IsUnique &&
            !index.Columns.Select(column => column.Column)
                .SequenceEqual(logicalUnit.Key.Columns, StringComparer.Ordinal));
    private ColumnRef? QueryColumn(string name)
    {
        var column = Unit.Columns.Single(item => item.Name == name);
        return column.Type switch
        {
            PortableType.Boolean => new ColumnRef(new TableId(Unit.Name), name, QueryType.Boolean, column.IsNullable),
            PortableType.Int32 => new ColumnRef(new TableId(Unit.Name), name, QueryType.Int32, column.IsNullable),
            PortableType.Int64 => new ColumnRef(new TableId(Unit.Name), name, QueryType.Int64, column.IsNullable),
            PortableType.Decimal => new ColumnRef(new TableId(Unit.Name), name, QueryType.Decimal, column.IsNullable, null,
                column.Precision is int precision ? checked((byte)precision) : null,
                column.Scale is int scale ? checked((byte)scale) : null),
            PortableType.String => new ColumnRef(new TableId(Unit.Name), name, QueryType.String, column.IsNullable, column.MaxLength),
            PortableType.DateTimeOffset => new ColumnRef(new TableId(Unit.Name), name, QueryType.DateTimeOffset, column.IsNullable),
            PortableType.Guid => new ColumnRef(new TableId(Unit.Name), name, QueryType.Guid, column.IsNullable),
            PortableType.Binary => new ColumnRef(new TableId(Unit.Name), name, QueryType.Binary, column.IsNullable, column.MaxLength),
            _ => null
        };
    }

    private static QueryType? QueryTypeOf(PortableType type) => type switch
    {
        PortableType.Boolean => QueryType.Boolean,
        PortableType.Int32 => QueryType.Int32,
        PortableType.Int64 => QueryType.Int64,
        PortableType.Decimal => QueryType.Decimal,
        PortableType.String => QueryType.String,
        PortableType.DateTimeOffset => QueryType.DateTimeOffset,
        PortableType.Guid => QueryType.Guid,
        PortableType.Binary => QueryType.Binary,
        _ => null
    };

    private WriteOutcome Mutate(StorageValues values, WriteOptions? options, Mutation mutation)
    {
        var outcome = MutateCore(values, options, mutation);
        if (outcome.Succeeded && Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            mutation is Mutation.Insert or Mutation.Upsert)
            ApplyOnAppendRetention();
        return outcome;
    }

    private void ApplyOnAppendRetention()
    {
        void Cleanup() => ApplyRetention(new RetentionExecutionOptions());
        if (transaction is null)
            OnAppendRetentionCoordinator.Run(owner, Unit, Access.Scope?.Value, Cleanup);
        else
            Cleanup();
    }

    private WriteOutcome MutateCore(StorageValues values, WriteOptions? options, Mutation mutation) => ExecuteWrite(() =>
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        ValidateValues(values.Values, mutation == Mutation.Insert,
            allowGeneratedLocator: mutation is Mutation.Update or Mutation.Upsert);

        // A provider sequence has no caller-visible key until the insert commits. Treat an
        // upsert without a generated key as an insert; accepting a synthetic read here would
        // both defeat the native identity and make the returned generated value ambiguous.
        if (SequenceColumn is not null &&
            (mutation is Mutation.Insert or Mutation.Upsert) &&
            !values.Values.ContainsKey(SequenceColumn.Name))
        {
            ValidateExpected(options, null, mutation);
            return InsertCore(values, mutation == Mutation.Upsert ? WriteOutcomeStatus.Upserted : WriteOutcomeStatus.Inserted);
        }

        var key = KeyFromValues(values.Values);
        // None mode has no token to inspect. Keep direct writes single-statement and let the
        // database report uniqueness/not-found from the write itself.
        var existing = Unit.Concurrency.IsNone ? null : ReadCore(key);
        if (mutation == Mutation.Insert && existing is not null)
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing.Version);
        if (mutation == Mutation.Update && existing is null && Unit.Concurrency.IsOptimistic)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        if (mutation == Mutation.Upsert && SequenceColumn is not null &&
            values.Values.ContainsKey(SequenceColumn.Name) && existing is null && Unit.Concurrency.IsOptimistic)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        ValidateExpected(options, existing, mutation);
        return mutation switch
        {
            Mutation.Insert => InsertCore(values),
            Mutation.Update => UpdateCore(values, key, existing!, options),
            Mutation.Upsert when SequenceColumn is not null && values.Values.ContainsKey(SequenceColumn.Name) =>
                UpdateCore(values, key, existing!, options),
            Mutation.Upsert => UpsertCore(values, key, existing, options, exactOutcome: false),
            Mutation.ConditionalUpsert => UpsertCore(values, key, existing, options, exactOutcome: true),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };
    });

    private WriteOutcome ConditionalUpsertCore(StorageValues values, WriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        ValidateValues(values.Values, requireAllNonNullable: false);
        if (options?.Precondition.Kind == WritePreconditionKind.IfVersion && VersionColumn is null)
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare version machinery.");

        var key = KeyFromValues(values.Values);
        var physical = PhysicalValues(values.Values, includeVersion: VersionColumn is not null);
        var (keyPredicate, keyParameters) = KeyPredicate(key.Values);
        var columns = physical.Keys.ToArray();
        var updateColumns = columns.Where(column =>
            !Unit.Key.Columns.Contains(column, StringComparer.Ordinal) &&
            column != PostgreSqlSchemaCoordinator.ScopeColumn &&
            column != "createdAt" &&
            column != PostgreSqlSchemaCoordinator.VersionColumn).ToArray();
        var updates = updateColumns.Select(column =>
            $"{Quote(column)}=EXCLUDED.{Quote(column)}").ToList();
        if (VersionColumn is not null)
            updates.Add($"{Quote(VersionColumn.Name)}={Quote(Unit.Name)}.{Quote(VersionColumn.Name)}+1");
        if (updates.Count == 0)
        {
            var noOpColumn = Unit.Key.Columns[0];
            updates.Add($"{Quote(noOpColumn)}={Quote(Unit.Name)}.{Quote(noOpColumn)}");
        }

        var conflictPredicate = PartialKeyPredicate();
        var conflict = $"({string.Join(", ", Unit.Key.Columns.Select(Quote))})" +
            (conflictPredicate is null ? string.Empty : $" WHERE {conflictPredicate}");
        var actionPredicate = VersionColumn is null || options?.Precondition.Kind == WritePreconditionKind.Unconditional
            ? string.Empty
            : options?.Precondition.Kind == WritePreconditionKind.CreateOnly
                ? " WHERE FALSE"
                : $" WHERE {Quote(Unit.Name)}.{Quote(VersionColumn.Name)}=@expected::bigint";
        var source = VersionColumn is null || options?.Precondition.Version is null
            ? $"VALUES ({string.Join(", ", columns.Select(column => "@" + column))})"
            : $"SELECT {string.Join(", ", columns.Select(column => "@" + column))} WHERE EXISTS (SELECT 1 FROM {Quote(Unit.Name)} WHERE {keyPredicate} AND {Quote(VersionColumn.Name)}=@expected::bigint)";
        var returning = VersionColumn is null
            ? "(xmax = 0) AS \"__groundwork_inserted\", NULL::bigint AS \"__groundwork_version\""
            : $"(xmax = 0) AS \"__groundwork_inserted\", {Quote(VersionColumn.Name)} AS \"__groundwork_version\"";
        var sql = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) " +
                  $"{source} ON CONFLICT {conflict} DO UPDATE SET {string.Join(", ", updates)}{actionPredicate} " +
                  $"RETURNING {returning};";
        using var command = Command(sql);
        AddParameters(command, physical);
        foreach (var pair in keyParameters)
            if (!physical.ContainsKey(pair.Key.TrimStart('@')))
                Add(command, pair.Key.TrimStart('@'), pair.Value);
        if (VersionColumn is not null)
            Add(command, "expected", options?.Precondition.Version);
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.conditional-upsert", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return DeferredConflict(key);

            var inserted = reader.GetBoolean(0);
            var versionOrdinal = 1;
            var version = reader.IsDBNull(versionOrdinal) ? (long?)null : reader.GetInt64(versionOrdinal);
            return new WriteOutcome(inserted ? WriteOutcomeStatus.Inserted : WriteOutcomeStatus.Updated, version);
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out var indexName))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName));
        }
    }

    private WriteOutcome DeferredConflict(StorageKey key) =>
        WriteOutcome.Deferred(
            WriteOutcomeStatus.ConcurrencyConflict,
            null,
            () =>
            {
                var existing = ReadCore(key);
                return existing is null
                    ? new WriteOutcomeDetail(WriteOutcomeStatus.NotFound)
                    : new WriteOutcomeDetail(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            });

    private string? LogicalIndexName(string? physicalName)
    {
        if (string.IsNullOrWhiteSpace(physicalName))
            return physicalName;

        return Unit.Indexes.FirstOrDefault(index =>
            string.Equals(
                PostgreSqlDialect.PhysicalIndexName(Unit.Name, index.Name),
                physicalName,
                StringComparison.Ordinal))?.Name ?? physicalName;
    }

    private WriteOutcome InsertCore(StorageValues values, WriteOutcomeStatus status = WriteOutcomeStatus.Inserted)
    {
        var physical = PhysicalValues(values.Values, includeVersion: VersionColumn is not null);
        var columns = physical.Keys.ToArray();
        var returning = SequenceColumn is null ? string.Empty : $" RETURNING {Quote(SequenceColumn.Name)};";
        var sql = columns.Length == 0
            ? $"INSERT INTO {Quote(Unit.Name)} DEFAULT VALUES{returning}"
            : $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column))}){returning}";
        using var command = Command(sql);
        AddParameters(command, physical);
        commandObserver?.Observe(new ProviderCommandEvent("postgresql.insert", sql, ProviderCommandKind.Write, IsProbe: false));
        try
        {
            if (SequenceColumn is null)
            {
                command.ExecuteNonQuery();
                return new WriteOutcome(status, VersionColumn is null ? null : 1);
            }

            object? generatedValue;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                    return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
                generatedValue = FromDatabase(reader.GetValue(0), SequenceColumn);
            }
            RecordHighWater(generatedValue);
            return new WriteOutcome(
                status,
                VersionColumn is null ? null : 1,
                generatedValues: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [SequenceColumn.Name] = generatedValue
                });
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
        }
    }

    private WriteOutcome UpdateCore(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options)
    {
        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name) &&
            !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal)).ToArray();
        var sets = supplied.Select(column => $"{Quote(column.Name)}=@{column.Name}").ToList();
        var parameters = PhysicalValues(values.Values, includeVersion: false);
        var (where, keyParameters) = KeyPredicate(key.Values);
        foreach (var pair in keyParameters) parameters[pair.Key] = pair.Value;
        if (VersionColumn is not null)
        {
            sets.Add($"{Quote(VersionColumn.Name)}={Quote(VersionColumn.Name)}+1");
            if (options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumn.Name)}=@expected";
                parameters["@expected"] = options.Precondition.Version!.Value;
            }
        }
        if (sets.Count == 0)
        {
            var noOpColumn = Unit.Key.Columns.First(column =>
                column != PostgreSqlSchemaCoordinator.ScopeColumn);
            sets.Add($"{Quote(noOpColumn)}={Quote(noOpColumn)}");
        }
        var sql = $"UPDATE {Quote(Unit.Name)} SET {string.Join(", ", sets)} WHERE {where};";
        using var command = Command(sql);
        AddParameters(command, parameters);
        if (Unit.Concurrency.IsNone)
            commandObserver?.Observe(new ProviderCommandEvent("postgresql.update", sql, ProviderCommandKind.Write, IsProbe: false));
        if (command.ExecuteNonQuery() == 0)
            return new WriteOutcome(Unit.Concurrency.IsNone
                ? WriteOutcomeStatus.NotFound
                : WriteOutcomeStatus.ConcurrencyConflict, existing?.Version);
        return new WriteOutcome(WriteOutcomeStatus.Updated,
            VersionColumn is null ? null : existing!.Version + 1);
    }

    private WriteOutcome UpsertCore(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        bool exactOutcome)
    {
        var physical = PhysicalValues(values.Values, includeVersion: VersionColumn is not null);
        var columns = physical.Keys.ToArray();
        var updateColumns = columns.Where(column =>
            !Unit.Key.Columns.Contains(column, StringComparer.Ordinal) &&
            column != PostgreSqlSchemaCoordinator.ScopeColumn &&
            column != "createdAt" &&
            column != PostgreSqlSchemaCoordinator.VersionColumn).ToArray();
        var conflictPredicate = PartialKeyPredicate();
        var conflict = $"({string.Join(", ", Unit.Key.Columns.Select(Quote))})" +
            (conflictPredicate is null ? string.Empty : $" WHERE {conflictPredicate}");
        var updates = new List<string>(updateColumns.Select(column =>
            $"{Quote(column)}=EXCLUDED.{Quote(column)}"));
        if (VersionColumn is not null)
            updates.Add($"{Quote(VersionColumn.Name)}={Quote(Unit.Name)}.{Quote(VersionColumn.Name)}+1");
        var action = updates.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", updates) +
              (exactOutcome && VersionColumn is not null ? $" WHERE {Quote(Unit.Name)}.{Quote(VersionColumn.Name)}=@expected" : string.Empty);
        var returning = exactOutcome
            ? " RETURNING (xmax = 0) AS \"__groundwork_inserted\", " +
              (VersionColumn is null ? "NULL::bigint" : Quote(VersionColumn.Name)) + " AS \"__groundwork_version\""
            : string.Empty;
        var sql = $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(Quote))}) VALUES ({string.Join(", ", columns.Select(column => "@" + column))}) ON CONFLICT {conflict} {action}{returning};";
        using var command = Command(sql);
        AddParameters(command, physical);
        if (exactOutcome && VersionColumn is not null)
            Add(command, "expected", options?.Precondition.Version);
        commandObserver?.Observe(new ProviderCommandEvent(
            exactOutcome ? "postgresql.conditional-upsert" : "postgresql.upsert",
            sql,
            ProviderCommandKind.Write,
            IsProbe: false));
        try
        {
            if (!exactOutcome)
            {
                command.ExecuteNonQuery();
                return new WriteOutcome(WriteOutcomeStatus.Upserted,
                    VersionColumn is null ? null : existing is null ? 1 : existing.Version + 1);
            }

            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing?.Version);
            var inserted = reader.GetBoolean(0);
            var version = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            return new WriteOutcome(inserted ? WriteOutcomeStatus.Inserted : WriteOutcomeStatus.Updated, version);
        }
        catch (DbException exception) when (new PostgreSqlDialect().TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing?.Version);
        }
    }

    private StoredEntry? ReadCore(
        StorageKey key,
        bool forUpdate = false,
        string? observerOperation = null,
        bool isProbe = true)
    {
        var (where, parameters) = KeyPredicate(key.Values);
        var columns = UserColumns.Concat(VersionColumn is null ? [] : [VersionColumn]).ToArray();
        var locking = forUpdate ? " FOR UPDATE" : string.Empty;
        using var command = Command($"SELECT {string.Join(", ", columns.Select(column => Quote(column.Name)))} FROM {Quote(Unit.Name)} WHERE {where}{locking};");
        AddParameters(command, parameters);
        commandObserver?.Observe(new ProviderCommandEvent(observerOperation ?? "postgresql.write-probe", command.CommandText, ProviderCommandKind.Read, IsProbe: isProbe));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < UserColumns.Count; index++)
            values[UserColumns[index].Name] = FromDatabase(reader.GetValue(index), UserColumns[index]);
        var version = VersionColumn is null ? (long?)null : reader.GetInt64(UserColumns.Count);
        return new StoredEntry(new StorageValues(values), version);
    }

    private StorageKey KeyFromValues(IReadOnlyDictionary<string, object?> values) =>
        new(Unit.Key.Columns.Where(column => column != PostgreSqlSchemaCoordinator.ScopeColumn)
            .ToDictionary(column => column, column => values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)), StringComparer.Ordinal));

    private (string Predicate, Dictionary<string, object?> Parameters) KeyPredicate(IReadOnlyDictionary<string, object?> values)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in Unit.Key.Columns)
        {
            object? value;
            string parameter;
            if (column == PostgreSqlSchemaCoordinator.ScopeColumn)
            {
                value = Access.Scope!.Value;
                parameter = "@__groundwork_scope";
            }
            else
            {
                value = values.TryGetValue(column, out var supplied)
                    ? supplied
                    : throw new ArgumentException($"Key column '{column}' is required.", nameof(values));
                parameter = "@key_" + column;
            }
            clauses.Add($"{Quote(column)}={parameter}");
            parameters[parameter] = ConvertValue(value, Column(column));
        }
        return (string.Join(" AND ", clauses), parameters);
    }

    private Dictionary<string, object?> PhysicalValues(
        IReadOnlyDictionary<string, object?> values,
        bool includeVersion)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in UserColumns)
            if (values.TryGetValue(column.Name, out var value))
                result[column.Name] = ConvertValue(value, column);
        if (Unit.Scope == ScopePolicy.Scoped)
            result[PostgreSqlSchemaCoordinator.ScopeColumn] = Access.Scope!.Value;
        if (includeVersion)
            result[PostgreSqlSchemaCoordinator.VersionColumn] = 1L;
        return result;
    }

    private string? PartialKeyPredicate()
    {
        var index = Unit.Indexes.FirstOrDefault(index => index.IsUnique &&
            index.MissingValues == MissingValueBehavior.Excluded &&
            index.Columns.Select(column => column.Column).SequenceEqual(Unit.Key.Columns, StringComparer.Ordinal));
        return index is null ? null : new PostgreSqlDialect().IndexFilter(index);
    }

    private void ValidateValues(
        IReadOnlyDictionary<string, object?> values,
        bool requireAllNonNullable,
        bool allowGeneratedLocator = false)
    {
        var known = UserColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null)
            throw new ArgumentException($"Column '{unknown}' is not declared by '{Unit.Name}'.", nameof(values));
        foreach (var generated in UserColumns.Where(column => column.Generation == ColumnGeneration.ProviderSequence))
            if (values.ContainsKey(generated.Name) && !allowGeneratedLocator)
                throw new ArgumentException($"ProviderSequence column '{generated.Name}' is assigned by PostgreSQL; it may only be supplied as the locator for Update or Upsert.", nameof(values));
        if (!requireAllNonNullable)
            return;
        foreach (var column in UserColumns.Where(column => !column.IsNullable && column.Default is null))
        {
            if (column.Generation == ColumnGeneration.ProviderSequence)
                continue;
            if (!values.TryGetValue(column.Name, out var value) || value is null)
                throw new ArgumentException($"Non-nullable column '{column.Name}' is required.", nameof(values));
        }
    }

    private void ValidateExpected(WriteOptions? options, StoredEntry? existing, Mutation mutation)
    {
        if (VersionColumn is null)
            return;
        var precondition = options?.Precondition ?? WritePrecondition.Unconditional;
        if (mutation == Mutation.Insert)
            return;

        if (mutation == Mutation.Upsert)
        {
            if (precondition.Kind == WritePreconditionKind.CreateOnly && existing is not null)
                throw new ConcurrencyConflictException(existing.Version);
            if (precondition.Kind == WritePreconditionKind.IfVersion &&
                (existing is null || precondition.Version != existing.Version))
                throw new ConcurrencyConflictException(existing?.Version);
            return;
        }
        if (precondition.Kind == WritePreconditionKind.IfVersion &&
            (existing is null || precondition.Version != existing.Version))
            throw new ConcurrencyConflictException(existing?.Version);
    }

    private T Execute<T>(Func<T> operation)
    {
        try
        {
            ThrowIfClosed();
            return operation();
        }
        catch (ConcurrencyConflictException exception) when (typeof(T) == typeof(WriteOutcome))
        {
            return (T)(object)new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, exception.Version);
        }
    }

    private T ExecuteWrite<T>(Func<T> operation)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        ThrowIfClosed();
        if (transaction is not null)
            return Translate(operation);
        using var write = connection.BeginTransaction(IsolationLevel.ReadCommitted);
        activeTransaction = write;
        try
        {
            var result = Translate(operation);
            write.Commit();
            return result;
        }
        catch
        {
            write.Rollback();
            throw;
        }
        finally
        {
            activeTransaction = null;
        }
    }

    private static T Translate<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (ConcurrencyConflictException exception) when (typeof(T) == typeof(WriteOutcome))
        {
            return (T)(object)new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, exception.Version);
        }
    }

    private NpgsqlCommand Command(string sql)
    {
        ThrowIfClosed();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = activeTransaction ?? transaction;
        return command;
    }

    private void AddParameters(NpgsqlCommand command, IReadOnlyDictionary<string, object?> parameters)
    {
        foreach (var pair in parameters)
            Add(command, pair.Key.TrimStart('@'), pair.Value);
    }

    /// <summary>
    /// Adds one parameter, typed from the column it carries.
    /// <para>
    /// <paramref name="column"/> is separate from <paramref name="name"/> because a batched statement
    /// cannot name its parameters after their columns — it holds one row per write, so the names are
    /// prefixed to keep them unique. Deriving the type from the placeholder instead would silently miss
    /// for every batched write, and PostgreSQL rejects an untyped JSON value with
    /// <c>42804: column "…" is of type jsonb but expression is of type text</c>.
    /// </para>
    /// </summary>
    private void Add(NpgsqlCommand command, string name, object? value, string? column = null)
    {
        var parameter = command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var typedAs = column ?? name;
        if (typedAs == "expected")
            parameter.NpgsqlDbType = NpgsqlDbType.Bigint;
        if (Unit.Columns.FirstOrDefault(candidate => candidate.Name == typedAs)?.Type == PortableType.Json)
            parameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
    }

    private static object? ConvertValue(object? value, ColumnDefinition definition) =>
        new PostgreSqlDialect().ConvertValue(value, definition);

    private static object? FromDatabase(object value, ColumnDefinition definition)
    {
        if (value is DBNull)
            return null;
        return definition.Type switch
        {
            PortableType.DateTimeOffset => new DateTimeOffset(Convert.ToInt64(value, CultureInfo.InvariantCulture), TimeSpan.Zero),
            PortableType.Json when value is string json => JsonDocument.Parse(json).RootElement.Clone(),
            PortableType.Json when value is JsonDocument document => document.RootElement.Clone(),
            PortableType.Json when value is JsonElement element => element.Clone(),
            PortableType.Binary when value is byte[] bytes => bytes.ToArray(),
            _ => value
        };
    }

    private ColumnDefinition Column(string name) => Unit.Columns.First(column => column.Name == name);

    private IReadOnlyList<ColumnDefinition> UserColumns =>
        Unit.Columns.Where(column => column.Name is not PostgreSqlSchemaCoordinator.ScopeColumn and not PostgreSqlSchemaCoordinator.VersionColumn).ToArray();

    private ColumnDefinition? SequenceColumn => UserColumns.FirstOrDefault(column =>
        column.Generation == ColumnGeneration.ProviderSequence);

    private ColumnDefinition? VersionColumn => Unit.Columns.FirstOrDefault(column => column.Name == PostgreSqlSchemaCoordinator.VersionColumn);

    private const string LedgerUnit = "unit";
    private const string LedgerScope = "scope";
    private const string LedgerNonce = "nonce";
    private const string LedgerCommittedAt = "committed_at";
    private const string LedgerFingerprint = "input_fingerprint";
    private const string LedgerResult = "exact_result";
    private const string HighWaterTable = "__groundwork_sequence_high_waters";
    private const string HighWaterValue = "high_water";

    private static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private void ThrowIfClosed()
    {
        owner.ThrowIfDisposed();
        if (closed)
            throw new ObjectDisposedException(nameof(PostgreSqlStorageSession));
    }

    private enum Mutation
    {
        Insert,
        Update,
        Upsert,
        ConditionalUpsert,
        Delete
    }

    private sealed record AppendExecution(WriteOutcomeStatus Status, IReadOnlyList<WriteOutcome>? Outcomes)
    {
        internal AppendOutcomeReport ToReport() =>
            new(Status, Outcomes ?? throw new InvalidOperationException("GW-APPEND-002: an exact append result was not recorded."));
    }

    private sealed class ConcurrencyConflictException(long? version = null) : Exception
    {
        public long? Version { get; } = version;
    }
}
