using System.Data;
using System.Data.SqlTypes;
using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;
using Groundwork.Testing;

namespace Groundwork.SqlServer;

internal sealed class SqlServerStorageSession : IStorageSession, IConcurrencyStorageSession, IBatchedStorageSession
{
    private readonly SqlServerProviderConnection owner;
    private readonly SqlConnection connection;
    private readonly SqlTransaction? transaction;
    private readonly SqlServerDialect dialect = new();
    private SqlTransaction? activeTransaction;
    private bool closed;

    internal SqlServerStorageSession(SqlServerProviderConnection owner, StorageUnit unit, StorageAccess access,
        SqlConnection connection, SqlTransaction? transaction)
    {
        this.owner = owner;
        Unit = unit;
        Access = access;
        this.connection = connection;
        this.transaction = transaction;
    }

    public StorageUnit Unit { get; }
    public StorageAccess Access { get; }

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) => Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException($"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.", nameof(request));
        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var executionSource = WithScopePredicate(request);
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(Unit.Key.Columns.Where(name => name != SqlServerSchemaCoordinator.ScopeColumn).Select(QueryColumn).Where(column => column is not null)!.Select(column => column!)) with
        {
            Indexes = SearchKeyQueryMappings.RetargetIndexes(Unit, suppliedOptions.Indexes)
                .Select(index => index.WithColumnTypes(Unit.Columns.ToDictionary(column => column.Name, column => QueryTypeOf(column.Type), StringComparer.Ordinal))).ToImmutableArray(),
            PhysicalIndexNames = Unit.Indexes.ToDictionary(
                index => index.Name,
                index => SqlServerDialect.PhysicalIndexName(Unit.Name, index.Name),
                StringComparer.Ordinal),
            SearchKeyColumns = SearchKeyQueryMappings.For(Unit)
        };
        var executionRequest = QueryRequestExecution.ForPage(executionSource, renderOptions);
        var command = new SqlServerQueryRenderer().Render(executionRequest, renderOptions);
        var rows = RelationalQueryResultReader.Read(connection, command, (name, value) =>
        {
            if (name == "__groundwork_total_count") return value;
            var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
            return column is null ? value : FromSqlServer(value ?? DBNull.Value, column);
        });
        AssertExplainPlan(command, renderOptions);
        return QueryResultMaterializer.Materialize(executionSource, renderOptions, rows, command.SelectedIndex, command.IndexHintApplied,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true);
    });

    public AggregationResult Aggregate(AggregationQuery query) => Execute(() =>
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.PostPredicate is not null || Unit.Scope != ScopePolicy.Global)
            return AggregationSessionExecutor.Execute(this, query);
        return RelationalAggregationExecutor.Execute(
            connection,
            activeTransaction ?? transaction,
            dialect,
            Unit,
            AggregationProfileValidator.ResolveOrThrow(Unit, query.ProfileName),
            query,
            (name, value) =>
            {
                var column = Unit.Columns.FirstOrDefault(item => item.Name == name);
                return column is null ? value : FromSqlServer(value ?? DBNull.Value, column);
            });
    });

    private void AssertExplainPlan(RelationalQueryCommand query, QueryRenderOptions options)
    {
        if (query.IsMatchNone || !ExplainAssertTestMode.ShouldAssert(query.SelectedIndex)) return;
        var logicalIndex = query.SelectedIndex!;
        var physicalIndex = options.ResolvePhysicalIndexName(logicalIndex);
        using (var enable = Command("SET STATISTICS XML ON")) enable.ExecuteNonQuery();
        string rawPlan;
        try
        {
            using var explain = Command(query.CommandText);
            RelationalQueryResultReader.AddParameters(explain, query);
            using var reader = explain.ExecuteReader();
            var plans = new List<string>();
            do
            {
                while (reader.Read())
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    if (!reader.GetName(ordinal).Contains("XML Showplan", StringComparison.OrdinalIgnoreCase) &&
                        reader.GetFieldType(ordinal) != typeof(SqlXml))
                        continue;
                    var content = reader.GetValue(ordinal) switch
                    {
                        SqlXml xml when !xml.IsNull => xml.Value,
                        SqlString text when !text.IsNull => text.Value,
                        string text => text,
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(content)) plans.Add(content);
                }
            } while (reader.NextResult());
            rawPlan = string.Join(Environment.NewLine, plans);
        }
        finally
        {
            using var disable = Command("SET STATISTICS XML OFF");
            disable.ExecuteNonQuery();
        }
        ExplainAssertTestMode.AssertChosenIndex(
            "SQL Server", logicalIndex, physicalIndex, query.IndexHintApplied, rawPlan,
            SqlServerExplainPlanInspector.ChoseIndex(rawPlan, physicalIndex));
    }

    private QueryRequest WithScopePredicate(QueryRequest request) => Unit.Scope != ScopePolicy.Scoped
        ? request
        : QueryRequestExecution.WithProviderPredicate(request, new Predicate.And([
            request.Where,
            new Predicate.Equal(new ColumnRef(new TableId(Unit.Name), SqlServerSchemaCoordinator.ScopeColumn, QueryType.String),
                QueryConstant.Of(new ColumnRef(new TableId(Unit.Name), SqlServerSchemaCoordinator.ScopeColumn, QueryType.String), Access.Scope!.Value))]),
            QueryRequestExecution.ScopeBindingDiscriminator(Access.Scope!.Value));

    public StoredEntry? Read(StorageKey key) => Execute(() => PublicEntry(ReadCore(key)));

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
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, options);
        return Execute(() => ConditionalUpsertCore(values, options));
    }

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes) =>
        ExecuteWrite(() => ApplyBatchCore(writes));

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
                return noneCommand.ExecuteNonQuery() == 0
                    ? new WriteOutcome(WriteOutcomeStatus.NotFound)
                    : new WriteOutcome(WriteOutcomeStatus.Deleted);
            }

            var existing = ReadCore(key);
            ValidateExpected(options, existing, Mutation.Delete);
            if (existing is null)
                return new WriteOutcome(WriteOutcomeStatus.NotFound);
            var (where, parameters) = KeyPredicate(key.Values);
            if (VersionColumnDefinition is not null && options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
                parameters["@expected"] = (options.Precondition.Version!.Value, VersionColumnDefinition);
            }
            using var command = Command($"DELETE FROM {Quote(Unit.Name)} WHERE {where};");
            AddParameters(command, parameters);
            if (command.ExecuteNonQuery() == 0) return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            return new WriteOutcome(WriteOutcomeStatus.Deleted, existing.Version);
        });
    }

    internal void Close() => closed = true;

    private IReadOnlyList<RowWriteOutcome> ApplyBatchCore(IReadOnlyList<RowWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return [];
        if (SequenceColumnDefinition is not null)
            return ApplyBatchFallback(writes);
        if (writes.Any(write => write.Options.Precondition.Kind != WritePreconditionKind.Unconditional))
            return ApplyBatchFallback(writes);
        if (HasSecondaryUniqueIndex(writes[0].Unit))
            return ApplyBatchFallback(writes);
        if (writes[0].Mode is not (RowWriteMode.Insert or RowWriteMode.Upsert))
            return ApplyBatchFallback(writes);

        var physicalWrites = writes.Select(write => write.PopulateSearchKeyValues()).ToArray();

        var columns = PhysicalBatchColumns(physicalWrites[0]);
        foreach (var write in physicalWrites)
        {
            ValidateValues(write.Values!.Values, requireAllNonNullable: write.Mode == RowWriteMode.Insert);
            if (!PhysicalBatchColumns(write).Select(column => column.Name).SequenceEqual(columns.Select(column => column.Name), StringComparer.Ordinal))
                return ApplyBatchFallback(writes);
        }

        return ApplyMergeBatch(physicalWrites, columns);
    }

    private IReadOnlyList<ColumnDefinition> PhysicalBatchColumns(RowWrite write)
    {
        var columns = UserColumns.Where(column => write.Values!.Values.ContainsKey(column.Name)).ToList();
        if (VersionColumnDefinition is not null)
            columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null)
            columns.Add(ScopeColumnDefinition);
        return columns;
    }

    private IReadOnlyList<RowWriteOutcome> ApplyMergeBatch(
        IReadOnlyList<RowWrite> writes,
        IReadOnlyList<ColumnDefinition> columns)
    {
        try
        {
            return ApplyMergeBatchTableValued(writes, columns);
        }
        catch (SqlException exception) when (exception.Message.Contains("table type", StringComparison.OrdinalIgnoreCase) ||
                                              exception.Message.Contains("type name", StringComparison.OrdinalIgnoreCase))
        {
            // Existing installations can be upgraded before the provider definition is
            // materialized. Preserve a VALUES fallback while the durable TVP catches up.
            return ApplyMergeBatchValues(writes, columns);
        }
    }

    private IReadOnlyList<RowWriteOutcome> ApplyMergeBatchTableValued(
        IReadOnlyList<RowWrite> writes,
        IReadOnlyList<ColumnDefinition> columns)
    {
        using var command = Command(string.Empty);
        var table = new DataTable();
        foreach (var column in Unit.Columns)
            table.Columns.Add(column.Name, ClrType(column.Type));
        foreach (var write in writes)
        {
            var row = table.NewRow();
            foreach (var column in Unit.Columns)
            {
                row[column.Name] = column.Name == SqlServerSchemaCoordinator.VersionColumn
                    ? 1L
                    : column.Name == SqlServerSchemaCoordinator.ScopeColumn
                        ? Access.Scope?.Value ?? (object)DBNull.Value
                        : write.Values!.Values.TryGetValue(column.Name, out var value)
                            ? SqlServerProviderConnection.ToSqlServerValue(value, column) ?? DBNull.Value
                            : DBNull.Value;
            }
            table.Rows.Add(row);
        }
        var parameter = command.Parameters.Add("@rows", SqlDbType.Structured);
        parameter.TypeName = $"dbo.{SqlServerSchemaCoordinator.BatchTypeName(writes[0].Unit)}";
        parameter.Value = table;

        var match = string.Join(" AND ", Unit.Key.Columns.Select(column =>
            $"target.{Quote(column)}=source.{Quote(column)}"));
        var sql = $"MERGE {Quote(Unit.Name)} WITH (HOLDLOCK) AS target USING @rows AS source ON {match} ";
        if (writes[0].Mode == RowWriteMode.Upsert)
        {
            var updates = columns
                .Where(column => !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
                                 column.Name != SqlServerSchemaCoordinator.ScopeColumn &&
                                 column.Name != "createdAt" &&
                                 column.Name != SqlServerSchemaCoordinator.VersionColumn)
                .Select(column => $"target.{Quote(column.Name)}=source.{Quote(column.Name)}")
                .ToList();
            if (VersionColumnDefinition is not null)
                updates.Add($"target.{Quote(VersionColumnDefinition.Name)}=target.{Quote(VersionColumnDefinition.Name)}+1");
            if (updates.Count == 0)
                updates.Add($"target.{Quote(Unit.Key.Columns[0])}=target.{Quote(Unit.Key.Columns[0])}");
            sql += $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updates)} ";
        }
        sql += $"WHEN NOT MATCHED BY TARGET THEN INSERT ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => $"source.{Quote(column.Name)}"))}) OUTPUT $action, {string.Join(", ", Unit.Key.Columns.Select(column => $"inserted.{Quote(column)}"))}{(VersionColumnDefinition is null ? string.Empty : $", inserted.{Quote(VersionColumnDefinition.Name)}")};";
        command.CommandText = sql;
        writes[0].Options.Observer?.Observe(new WritePathEvent("sqlserver.batch-merge-tvp", "SQL Server MERGE table-valued parameter", IsProbe: false));
        try
        {
            var returned = ReadMergeOutcomes(command, writes[0].Unit);
            return MapMergeOutcomes(writes, returned);
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private IReadOnlyList<RowWriteOutcome> ApplyMergeBatchValues(
        IReadOnlyList<RowWrite> writes,
        IReadOnlyList<ColumnDefinition> columns)
    {
        var maxRows = Math.Max(1, 2_000 / columns.Count);
        if (writes.Count > maxRows)
            return writes.Chunk(maxRows).SelectMany(chunk => ApplyMergeBatchValues(chunk, columns)).ToArray();

        using var command = Command(string.Empty);
        var rows = new List<string>(writes.Count);
        for (var row = 0; row < writes.Count; row++)
        {
            var values = writes[row].Values!.Values;
            var parameters = new List<string>(columns.Count);
            foreach (var column in columns)
            {
                var name = $"@r{row}_{column.Name}";
                parameters.Add(name);
                SqlServerProviderConnection.AddParameter(command, name,
                    column.Name == SqlServerSchemaCoordinator.VersionColumn
                        ? 1L
                        : column.Name == SqlServerSchemaCoordinator.ScopeColumn
                            ? Access.Scope!.Value
                            : values[column.Name], column);
            }
            rows.Add($"({string.Join(", ", parameters)})");
        }

        var sourceColumns = string.Join(", ", columns.Select(column => Quote(column.Name)));
        var match = string.Join(" AND ", Unit.Key.Columns.Select(column =>
            $"target.{Quote(column)}=source.{Quote(column)}"));
        var sql = $"MERGE {Quote(Unit.Name)} WITH (HOLDLOCK) AS target USING (VALUES {string.Join(", ", rows)}) AS source ({sourceColumns}) ON {match} ";
        if (writes[0].Mode == RowWriteMode.Upsert)
        {
            var updates = columns
                .Where(column => !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
                                 column.Name != SqlServerSchemaCoordinator.ScopeColumn &&
                                 column.Name != "createdAt" &&
                                 column.Name != SqlServerSchemaCoordinator.VersionColumn)
                .Select(column => $"target.{Quote(column.Name)}=source.{Quote(column.Name)}")
                .ToList();
            if (VersionColumnDefinition is not null)
                updates.Add($"target.{Quote(VersionColumnDefinition.Name)}=target.{Quote(VersionColumnDefinition.Name)}+1");
            if (updates.Count == 0)
                updates.Add($"target.{Quote(Unit.Key.Columns[0])}=target.{Quote(Unit.Key.Columns[0])}");
            sql += $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updates)} ";
        }
        sql += $"WHEN NOT MATCHED BY TARGET THEN INSERT ({string.Join(", ", columns.Select(column => Quote(column.Name)))}) VALUES ({string.Join(", ", columns.Select(column => $"source.{Quote(column.Name)}"))}) OUTPUT $action, {string.Join(", ", Unit.Key.Columns.Select(column => $"inserted.{Quote(column)}"))}{(VersionColumnDefinition is null ? string.Empty : $", inserted.{Quote(VersionColumnDefinition.Name)}")};";
        command.CommandText = sql;
        writes[0].Options.Observer?.Observe(new WritePathEvent("sqlserver.batch-merge", "SQL Server MERGE batch", IsProbe: false));
        try
        {
            var returned = ReadMergeOutcomes(command, writes[0].Unit);
            return writes.Select(write =>
            {
                if (!returned.TryGetValue(write.Identity, out var result))
                    return new RowWriteOutcome(write, new WriteOutcome(WriteOutcomeStatus.UniqueViolation));
                var status = string.Equals(result.Action, "INSERT", StringComparison.Ordinal)
                    ? WriteOutcomeStatus.Inserted
                    : WriteOutcomeStatus.Updated;
                return new RowWriteOutcome(write, new WriteOutcome(status, result.Version));
            }).ToArray();
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out var indexName))
        {
            return writes.Select(write => new RowWriteOutcome(write,
                new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName)))).ToArray();
        }
    }

    private Dictionary<string, (string Action, long? Version)> ReadMergeOutcomes(
        SqlCommand command,
        StorageUnit logicalUnit)
    {
        var returned = new Dictionary<string, (string, long?)>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < Unit.Key.Columns.Count; index++)
            {
                var column = Unit.Key.Columns[index];
                if (column != SqlServerSchemaCoordinator.ScopeColumn)
                    values[column] = FromSqlServer(reader.GetValue(index + 1), Column(column));
            }
            var versionOrdinal = Unit.Key.Columns.Count + 1;
            var version = VersionColumnDefinition is null || reader.IsDBNull(versionOrdinal)
                ? (long?)null
                : Convert.ToInt64(reader.GetValue(versionOrdinal), CultureInfo.InvariantCulture);
            returned[RowWrite.IdentityFor(logicalUnit, values)] = (reader.GetString(0), version);
        }
        return returned;
    }

    private IReadOnlyList<RowWriteOutcome> MapMergeOutcomes(
        IReadOnlyList<RowWrite> writes,
        IReadOnlyDictionary<string, (string Action, long? Version)> returned) =>
        writes.Select(write =>
        {
            if (!returned.TryGetValue(write.Identity, out var result))
                return new RowWriteOutcome(write, new WriteOutcome(WriteOutcomeStatus.UniqueViolation));
            var status = string.Equals(result.Action, "INSERT", StringComparison.Ordinal)
                ? WriteOutcomeStatus.Inserted
                : WriteOutcomeStatus.Updated;
            return new RowWriteOutcome(write, new WriteOutcome(status, result.Version));
        }).ToArray();

    private static Type ClrType(PortableType type) => type switch
    {
        PortableType.String or PortableType.Json => typeof(string),
        PortableType.Int32 => typeof(int),
        PortableType.Int64 => typeof(long),
        PortableType.Decimal => typeof(decimal),
        PortableType.Boolean => typeof(bool),
        PortableType.DateTimeOffset => typeof(DateTimeOffset),
        PortableType.Guid => typeof(Guid),
        PortableType.Binary => typeof(byte[]),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private IReadOnlyList<RowWriteOutcome> ApplyBatchFallback(IReadOnlyList<RowWrite> writes) =>
        writes.Select(write => new RowWriteOutcome(write, write.Mode switch
        {
            RowWriteMode.Insert => Insert(write.Values!, write.Options),
            RowWriteMode.Update => Update(write.Values!, write.Options),
            RowWriteMode.Upsert when write.Options.Precondition.Kind == WritePreconditionKind.IfVersion => ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.Upsert => Upsert(write.Values!, write.Options),
            RowWriteMode.ConditionalUpsert => ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.Delete => Delete(write.Key!, write.Options),
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

    private WriteOutcome Mutate(StorageValues values, WriteOptions? options, Mutation mutation) => ExecuteWrite(() =>
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        ValidateValues(values.Values, mutation == Mutation.Insert,
            allowGeneratedLocator: mutation is Mutation.Update or Mutation.Upsert);
        if (SequenceColumnDefinition is not null &&
            (mutation is Mutation.Insert or Mutation.Upsert) &&
            !values.Values.ContainsKey(SequenceColumnDefinition.Name))
        {
            ValidateExpected(options, null, mutation);
            return InsertCore(values.Values, mutation == Mutation.Upsert ? WriteOutcomeStatus.Upserted : WriteOutcomeStatus.Inserted);
        }
        var key = new StorageKey(LogicalKeyColumns.ToDictionary(
            column => column,
            column => values.Values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
            StringComparer.Ordinal));
        // None mode has no token to inspect. Keep direct writes single-statement and let the
        // database report uniqueness/not-found from the write itself.
        var existing = Unit.Concurrency.IsNone ? null : ReadCore(key);
        if (mutation == Mutation.Insert && existing is not null) return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing.Version);
        if (mutation == Mutation.Update && existing is null && Unit.Concurrency.IsOptimistic) return new WriteOutcome(WriteOutcomeStatus.NotFound);
        if (mutation == Mutation.Upsert && SequenceColumnDefinition is not null &&
            values.Values.ContainsKey(SequenceColumnDefinition.Name) && existing is null && Unit.Concurrency.IsOptimistic)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        ValidateExpected(options, existing, mutation);
        if (mutation == Mutation.Upsert)
        {
            if (SequenceColumnDefinition is not null && values.Values.ContainsKey(SequenceColumnDefinition.Name))
                return UpdateCore(values.Values, existing, options);
            if (Unit.Concurrency.IsNone) return UpsertNoneCore(values.Values, options);
            if (existing is null) return InsertCore(values.Values);
            return UpdateCore(values.Values, existing, options);
        }

        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        if (mutation == Mutation.Insert)
        {
            if (VersionColumnDefinition is not null) columns.Add(VersionColumnDefinition);
            if (ScopeColumnDefinition is not null) columns.Add(ScopeColumnDefinition);
            var parameters = BuildParameters(values.Values, supplied);
            if (VersionColumnDefinition is not null) parameters["@__groundwork_version"] = (1L, VersionColumnDefinition);
            if (ScopeColumnDefinition is not null) parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
            var output = SequenceColumnDefinition is null ? string.Empty : $" OUTPUT INSERTED.{Quote(SequenceColumnDefinition.Name)}";
            using var insert = Command($"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}){output} VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))});");
            AddParameters(insert, parameters);
            try
            {
                if (SequenceColumnDefinition is null)
                {
                    insert.ExecuteNonQuery();
                    return new WriteOutcome(WriteOutcomeStatus.Inserted, VersionColumnDefinition is null ? null : 1);
                }

                var generated = insert.ExecuteScalar();
                return new WriteOutcome(
                    WriteOutcomeStatus.Inserted,
                    VersionColumnDefinition is null ? null : 1,
                    generatedValues: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [SequenceColumnDefinition.Name] = FromSqlServer(generated!, SequenceColumnDefinition)
                    });
            }
            catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _))
            {
                return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
            }
        }
        return UpdateCore(values.Values, existing, options);
    });

    private WriteOutcome InsertCore(
        IReadOnlyDictionary<string, object?> values,
        WriteOutcomeStatus status = WriteOutcomeStatus.Upserted)
    {
        var supplied = UserColumns.Where(column => values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        if (VersionColumnDefinition is not null) columns.Add(VersionColumnDefinition);
        if (ScopeColumnDefinition is not null) columns.Add(ScopeColumnDefinition);
        var parameters = BuildParameters(values, supplied);
        if (VersionColumnDefinition is not null) parameters["@__groundwork_version"] = (1L, VersionColumnDefinition);
        if (ScopeColumnDefinition is not null) parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
        var output = SequenceColumnDefinition is null ? string.Empty : $" OUTPUT INSERTED.{Quote(SequenceColumnDefinition.Name)}";
        var sql = columns.Count == 0
            ? $"INSERT INTO {Quote(Unit.Name)}{output} DEFAULT VALUES;"
            : $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", columns.Select(column => Quote(column.Name)))}){output} VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))});";
        using var command = Command(sql);
        AddParameters(command, parameters);
        try
        {
            if (SequenceColumnDefinition is null)
            {
                command.ExecuteNonQuery();
                return new WriteOutcome(status, VersionColumnDefinition is null ? null : 1);
            }

            var generated = command.ExecuteScalar();
            return new WriteOutcome(
                status,
                VersionColumnDefinition is null ? null : 1,
                generatedValues: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [SequenceColumnDefinition.Name] = FromSqlServer(generated!, SequenceColumnDefinition)
                });
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _)) { return new WriteOutcome(WriteOutcomeStatus.UniqueViolation); }
    }

    private WriteOutcome UpsertNoneCore(IReadOnlyDictionary<string, object?> values, WriteOptions? options)
    {
        var supplied = UserColumns.Where(column => values.ContainsKey(column.Name)).ToArray();
        var columns = supplied.ToList();
        var parameters = BuildParameters(values, supplied);
        if (ScopeColumnDefinition is not null)
        {
            columns.Add(ScopeColumnDefinition);
            parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
        }

        var sql = RenderNoneUpsertSql(Unit, columns);
        using var command = Command(sql);
        AddParameters(command, parameters);
        options?.Observer?.Observe(new WritePathEvent("sqlserver.upsert", sql, IsProbe: false));
        try
        {
            command.ExecuteNonQuery();
            return new WriteOutcome(WriteOutcomeStatus.Upserted);
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation);
        }
    }

    internal static string RenderNoneUpsertSql(
        StorageUnit unit,
        IReadOnlyList<ColumnDefinition> columns)
    {
        var sourceColumns = string.Join(", ", columns.Select(column => Quote(column.Name)));
        var match = string.Join(" AND ", unit.Key.Columns.Select(column =>
            $"target.{Quote(column)}=source.{Quote(column)}"));
        var updates = columns
            .Where(column => !unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
                             column.Name != SqlServerSchemaCoordinator.ScopeColumn &&
                             column.Name != "createdAt" &&
                             column.Name != SqlServerSchemaCoordinator.VersionColumn)
            .Select(column => $"target.{Quote(column.Name)}=source.{Quote(column.Name)}")
            .ToList();
        if (updates.Count == 0)
            updates.Add($"target.{Quote(unit.Key.Columns[0])}=target.{Quote(unit.Key.Columns[0])}");

        return $"MERGE {Quote(unit.Name)} WITH (HOLDLOCK) AS target " +
               $"USING (VALUES ({string.Join(", ", columns.Select(column => "@" + column.Name))})) AS source ({sourceColumns}) ON {match} " +
               $"WHEN MATCHED THEN UPDATE SET {string.Join(", ", updates)} " +
               $"WHEN NOT MATCHED BY TARGET THEN INSERT ({sourceColumns}) VALUES ({string.Join(", ", columns.Select(column => $"source.{Quote(column.Name)}"))});";
    }

    private WriteOutcome UpdateCore(IReadOnlyDictionary<string, object?> values, StoredEntry? existing, WriteOptions? options)
    {
        var supplied = UserColumns.Where(column => values.ContainsKey(column.Name) && !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal)).ToArray();
        var sets = supplied.Select(column => $"{Quote(column.Name)}=@{column.Name}").ToList();
        var parameters = BuildParameters(values, supplied);
        var (where, keyParameters) = KeyPredicate(values);
        foreach (var pair in keyParameters) parameters[pair.Key] = pair.Value;
        if (VersionColumnDefinition is not null)
        {
            sets.Add($"{Quote(VersionColumnDefinition.Name)}={Quote(VersionColumnDefinition.Name)}+1");
            if (options?.Precondition.Kind == WritePreconditionKind.IfVersion)
            {
                where += $" AND {Quote(VersionColumnDefinition.Name)}=@expected";
                parameters["@expected"] = (options.Precondition.Version!.Value, VersionColumnDefinition);
            }
        }
        if (sets.Count == 0)
        {
            var noOpColumn = LogicalKeyColumns[0];
            sets.Add($"{Quote(noOpColumn)}={Quote(noOpColumn)}");
        }
        var sql = $"UPDATE {Quote(Unit.Name)} SET {string.Join(", ", sets)} WHERE {where};";
        using var command = Command(sql);
        AddParameters(command, parameters);
        if (Unit.Concurrency.IsNone)
            options?.Observer?.Observe(new WritePathEvent("sqlserver.update", sql, IsProbe: false));
        try
        {
            if (command.ExecuteNonQuery() == 0)
                return new WriteOutcome(Unit.Concurrency.IsNone
                    ? WriteOutcomeStatus.NotFound
                    : WriteOutcomeStatus.ConcurrencyConflict, existing?.Version);
            return new WriteOutcome(WriteOutcomeStatus.Updated, VersionColumnDefinition is null ? null : existing!.Version + 1);
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out _))
        {
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing?.Version);
        }
    }

    private WriteOutcome ConditionalUpsertCore(StorageValues values, WriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        ValidateValues(values.Values, requireAllNonNullable: false);
        if (options?.Precondition.Kind == WritePreconditionKind.IfVersion && VersionColumnDefinition is null)
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare version machinery.");

        var key = new StorageKey(LogicalKeyColumns.ToDictionary(
            column => column,
            column => values.Values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
            StringComparer.Ordinal));
        return ExecuteConditionalBatch(values, options, key);
    }

    private WriteOutcome ExecuteConditionalBatch(
        StorageValues values,
        WriteOptions? options,
        StorageKey key)
    {
        var supplied = UserColumns.Where(column => values.Values.ContainsKey(column.Name)).ToArray();
        var updateColumns = supplied.Where(column =>
            !Unit.Key.Columns.Contains(column.Name, StringComparer.Ordinal) &&
            column.Name != "createdAt" &&
            column.Name != SqlServerSchemaCoordinator.ScopeColumn).ToArray();
        var updates = updateColumns.Select(column =>
            $"{Quote(column.Name)}=@{column.Name}").ToList();
        if (updates.Count == 0)
        {
            var noOpColumn = LogicalKeyColumns[0];
            updates.Add($"{Quote(noOpColumn)}={Quote(noOpColumn)}");
        }
        if (VersionColumnDefinition is not null)
            updates.Add($"{Quote(VersionColumnDefinition.Name)}={Quote(VersionColumnDefinition.Name)}+1");

        var parameters = BuildParameters(values.Values, supplied);
        var (_, keyParameters) = KeyPredicate(key.Values);
        foreach (var pair in keyParameters)
            parameters[pair.Key] = pair.Value;
        if (VersionColumnDefinition is not null)
            parameters["@expected"] = (options?.Precondition.Version, VersionColumnDefinition);

        var where = string.Join(" AND ", LogicalKeyColumns.Select(column =>
            $"target.{Quote(column)}=@key_{column}"));
        if (ScopeColumnDefinition is not null)
            where += $" AND target.{Quote(ScopeColumnDefinition.Name)}=@__groundwork_scope";
        var updateCondition = VersionColumnDefinition is null || options?.Precondition.Kind == WritePreconditionKind.Unconditional
            ? "1=1"
            : options?.Precondition.Kind == WritePreconditionKind.CreateOnly
                ? "1=0"
                : $"target.{Quote(VersionColumnDefinition.Name)}=@expected";
        var insertCondition = VersionColumnDefinition is null || options?.Precondition.Kind != WritePreconditionKind.IfVersion ? "1=1" : "1=0";
        var insertColumns = supplied.ToList();
        if (VersionColumnDefinition is not null)
        {
            insertColumns.Add(VersionColumnDefinition);
            parameters["@__groundwork_version"] = (1L, VersionColumnDefinition);
        }
        if (ScopeColumnDefinition is not null)
        {
            insertColumns.Add(ScopeColumnDefinition);
            parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
        }

        var outputVersion = VersionColumnDefinition is null
            ? "CONVERT(bigint, NULL)"
            : $"inserted.{Quote(VersionColumnDefinition.Name)}";
        var operation = $"DECLARE @result TABLE ([operation] nvarchar(6) NOT NULL, [version] bigint NULL); " +
            $"UPDATE target WITH (UPDLOCK, SERIALIZABLE) SET {string.Join(", ", updates)} " +
            $"OUTPUT N'UPDATE', {outputVersion} INTO @result ([operation], [version]) " +
            $"FROM {Quote(Unit.Name)} AS target WHERE {where} AND ({updateCondition}); " +
            $"IF @@ROWCOUNT = 0 AND ({insertCondition}) BEGIN " +
            $"INSERT INTO {Quote(Unit.Name)} ({string.Join(", ", insertColumns.Select(column => Quote(column.Name)))}) " +
            $"OUTPUT N'INSERT', {(VersionColumnDefinition is null ? "CONVERT(bigint, NULL)" : "CONVERT(bigint, 1)")} " +
            $"INTO @result ([operation], [version]) VALUES ({string.Join(", ", insertColumns.Select(column => "@" + column.Name))}); END; " +
            "SELECT [operation], [version] FROM @result;";
        // A range lock must span the UPDATE and conditional INSERT, but opening and
        // committing a SqlTransaction from the client would add two network round
        // trips. When the caller did not supply a transaction, keep the transaction
        // boundary inside this one submitted batch. XACT_ABORT plus the catch block
        // guarantees that a failed insert does not strand an open transaction.
        var sql = transaction is not null
            ? operation
            : "SET XACT_ABORT ON; BEGIN TRANSACTION; BEGIN TRY " + operation +
              " COMMIT TRANSACTION; END TRY BEGIN CATCH IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION; THROW; END CATCH;";
        using var command = Command(sql);
        AddParameters(command, parameters);
        options?.Observer?.Observe(new WritePathEvent("sqlserver.conditional-upsert", sql, IsProbe: false));
        try
        {
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var status = string.Equals(reader.GetString(0), "INSERT", StringComparison.Ordinal)
                    ? WriteOutcomeStatus.Inserted
                    : WriteOutcomeStatus.Updated;
                var version = reader.IsDBNull(1)
                    ? (long?)null
                    : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                return new WriteOutcome(status, version);
            }
        }
        catch (SqlException exception) when (dialect.TryMapUniqueViolation(exception, out var indexName))
        {
            return IsPrimaryKeyViolation(indexName)
                ? DeferredConflict(key, options?.Observer)
                : new WriteOutcome(WriteOutcomeStatus.UniqueViolation, null, LogicalIndexName(indexName));
        }

        return DeferredConflict(key, options?.Observer);
    }

    private bool IsPrimaryKeyViolation(string indexName) =>
        indexName.Contains("__groundwork_pk_", StringComparison.OrdinalIgnoreCase) ||
        indexName.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);

    private string? LogicalIndexName(string? physicalName)
    {
        if (string.IsNullOrWhiteSpace(physicalName))
            return physicalName;

        return Unit.Indexes.FirstOrDefault(index =>
            string.Equals(
                SqlServerDialect.PhysicalIndexName(Unit.Name, index.Name),
                physicalName,
                StringComparison.OrdinalIgnoreCase))?.Name ?? physicalName;
    }

    private WriteOutcome DeferredConflict(StorageKey key, IWritePathObserver? observer) =>
        WriteOutcome.Deferred(
            WriteOutcomeStatus.ConcurrencyConflict,
            null,
            () =>
            {
                var existing = ReadCore(key, observer);
                return existing is null
                    ? new WriteOutcomeDetail(WriteOutcomeStatus.NotFound)
                    : new WriteOutcomeDetail(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            });

    private StoredEntry? ReadCore(StorageKey key, IWritePathObserver? observer = null)
    {
        var (where, parameters) = KeyPredicate(key.Values);
        var columns = UserColumns.Concat(VersionColumnDefinition is null ? [] : [VersionColumnDefinition]);
        using var command = Command($"SELECT {string.Join(", ", columns.Select(column => Quote(column.Name)))} FROM {Quote(Unit.Name)} WHERE {where};");
        AddParameters(command, parameters);
        observer?.Observe(new WritePathEvent("sqlserver.write-probe", command.CommandText, IsProbe: true));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < UserColumns.Count; i++) values[UserColumns[i].Name] = FromSqlServer(reader.GetValue(i), UserColumns[i]);
        var version = VersionColumnDefinition is null ? (long?)null : Convert.ToInt64(reader.GetValue(UserColumns.Count), CultureInfo.InvariantCulture);
        return new StoredEntry(new StorageValues(values), version);
    }

    private void ValidateValues(
        IReadOnlyDictionary<string, object?> values,
        bool requireAllNonNullable,
        bool allowGeneratedLocator = false)
    {
        var known = UserColumns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.FirstOrDefault(key => !known.Contains(key));
        if (unknown is not null) throw new ArgumentException($"Column '{unknown}' is not declared by '{Unit.Name}'.", nameof(values));
        foreach (var generated in UserColumns.Where(column => column.Generation == ColumnGeneration.ProviderSequence))
            if (values.ContainsKey(generated.Name) && !allowGeneratedLocator)
                throw new ArgumentException($"ProviderSequence column '{generated.Name}' is assigned by SQL Server; it may only be supplied as the locator for Update or Upsert.", nameof(values));
        if (requireAllNonNullable)
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
        if (VersionColumnDefinition is null) return;
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

    private (string Predicate, Dictionary<string, (object? Value, ColumnDefinition Definition)> Parameters) KeyPredicate(IReadOnlyDictionary<string, object?> values)
    {
        var clauses = new List<string>();
        var parameters = new Dictionary<string, (object?, ColumnDefinition)>(StringComparer.Ordinal);
        foreach (var column in LogicalKeyColumns)
        {
            if (!values.TryGetValue(column, out var value)) throw new ArgumentException($"Key column '{column}' is required.", nameof(values));
            var parameter = "@key_" + column;
            clauses.Add($"{Quote(column)}={parameter}");
            parameters[parameter] = (value, Column(column));
        }
        if (ScopeColumnDefinition is not null)
        {
            clauses.Add($"{Quote(ScopeColumnDefinition.Name)}=@__groundwork_scope");
            parameters["@__groundwork_scope"] = (Access.Scope!.Value, ScopeColumnDefinition);
        }
        return (string.Join(" AND ", clauses), parameters);
    }

    private Dictionary<string, (object? Value, ColumnDefinition Definition)> BuildParameters(IReadOnlyDictionary<string, object?> values, IEnumerable<ColumnDefinition> columns) =>
        columns.Where(column => values.ContainsKey(column.Name)).ToDictionary(column => "@" + column.Name, column => (values[column.Name], column), StringComparer.Ordinal);

    private SqlCommand Command(string sql)
    {
        if (closed) throw new ObjectDisposedException(nameof(SqlServerStorageSession));
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = activeTransaction ?? transaction;
        return command;
    }

    private static void AddParameters(SqlCommand command, IReadOnlyDictionary<string, (object? Value, ColumnDefinition Definition)> parameters)
    {
        foreach (var pair in parameters) SqlServerProviderConnection.AddParameter(command, pair.Key, pair.Value.Value, pair.Value.Definition);
    }

    private T Execute<T>(Func<T> operation)
    {
        try
        {
            if (transaction is not null) return operation();
            owner.ThrowIfDisposed();
            return operation();
        }
        catch (ConcurrencyConflictException exception) when (typeof(T) == typeof(WriteOutcome))
        {
            return (T)(object)new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, exception.Version);
        }
    }

    private T ExecuteWrite<T>(Func<T> operation)
    {
        if (transaction is not null) return Translate(operation);
        lock (owner.Gate)
        {
            owner.ThrowIfDisposed();
            using var writeTransaction = connection.BeginTransaction(IsolationLevel.Serializable);
            activeTransaction = writeTransaction;
            try { var result = Translate(operation); writeTransaction.Commit(); return result; }
            catch { writeTransaction.Rollback(); throw; }
            finally { activeTransaction = null; }
        }
    }

    private static T Translate<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (ConcurrencyConflictException exception) when (typeof(T) == typeof(WriteOutcome))
        { return (T)(object)new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, exception.Version); }
    }

    private ColumnDefinition Column(string name) => UserColumns.First(column => column.Name == name);
    private IReadOnlyList<ColumnDefinition> UserColumns => Unit.Columns.Where(column => column.Name is not SqlServerSchemaCoordinator.ScopeColumn and not SqlServerSchemaCoordinator.VersionColumn).ToArray();
    private IReadOnlyList<string> LogicalKeyColumns => Unit.Key.Columns.Where(column => column != SqlServerSchemaCoordinator.ScopeColumn).ToArray();
    private ColumnDefinition? ScopeColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqlServerSchemaCoordinator.ScopeColumn);
    private ColumnDefinition? VersionColumnDefinition => Unit.Columns.FirstOrDefault(column => column.Name == SqlServerSchemaCoordinator.VersionColumn);
    private ColumnDefinition? SequenceColumnDefinition => UserColumns.FirstOrDefault(column => column.Generation == ColumnGeneration.ProviderSequence);
    private static string Quote(string value) => SqlServerProviderConnection.QuoteIdentifier(value);

    private static object? FromSqlServer(object value, ColumnDefinition definition)
    {
        if (value is DBNull) return null;
        return definition.Type switch
        {
            PortableType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
            PortableType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            PortableType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            PortableType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            PortableType.Guid => (Guid)value,
            PortableType.DateTimeOffset => ((DateTimeOffset)value).ToUniversalTime(),
            PortableType.Binary => ((byte[])value).ToArray(),
            PortableType.Json => JsonDocument.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!).RootElement.Clone(),
            _ => value
        };
    }

    private sealed class ConcurrencyConflictException(long? version = null) : Exception
    {
        public long? Version { get; } = version;
    }

    private enum Mutation { Insert, Update, Upsert, Delete }
}
