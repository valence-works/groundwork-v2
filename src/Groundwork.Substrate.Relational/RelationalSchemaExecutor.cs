using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Shared relational schema executor. Providers own only the public <see cref="RelationalDialect"/>
/// contract; lifecycle, fencing, operation dispatch, and connection cleanup remain common.
/// </summary>
public sealed class RelationalSchemaExecutor : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector
{
    private readonly Func<DbConnection> createConnection;
    private readonly RelationalDialect dialect;

    public RelationalSchemaExecutor(
        Func<DbConnection> createConnection,
        RelationalDialect dialect)
    {
        this.createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    }

    public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var connection = createConnection()
            ?? throw new InvalidOperationException("The relational connection factory returned null.");
        try
        {
            if (connection.State != ConnectionState.Open)
                connection.Open();
            dialect.EnsureInfrastructure(connection);
            var resource = $"groundwork:schema:{target}";
            dialect.AcquireApplicationLock(connection, resource);
            try
            {
                var owner = Guid.NewGuid().ToString("N");
                var fence = dialect.AcquireFence(connection, target, owner);
                var sessionId = dialect.ReadServerSessionId(connection);
                return new RelationalApplicationLock(connection, dialect, target, resource, owner, fence, sessionId);
            }
            catch
            {
                dialect.ReleaseApplicationLock(connection, resource);
                throw;
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public PhysicalSchemaHistoryState ReadHistory(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        var lease = RequireLock(target, applicationLock);
        lease.Verify();
        return dialect.ReadHistory(lease.Connection, target);
    }

    public PhysicalSchemaOperationAcknowledgement ApplyOperation(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var lease = RequireLock(target, applicationLock);
        lease.Verify();
        return ApplyOperationBatchCore(lease, target, [operation])[0];
    }

    public IReadOnlyList<PhysicalSchemaOperationAcknowledgement> ApplyOperationBatch(
        PhysicalSchemaTargetIdentity target,
        IReadOnlyList<PhysicalSchemaOperation> operations,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var snapshot = operations.ToArray();
        if (snapshot.Any(operation => operation is null))
            throw new ArgumentException("A schema operation batch cannot contain null operations.", nameof(operations));
        var lease = RequireLock(target, applicationLock);
        lease.Verify();
        return ApplyOperationBatchCore(lease, target, snapshot);
    }

    public void PublishAppliedState(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(state);
        var lease = RequireLock(state.TargetIdentity, applicationLock);
        lease.Verify();
        using var transaction = dialect.BeginTransaction(lease.Connection);
        try
        {
            dialect.AssertFence(lease.Connection, transaction, state.TargetIdentity, lease.Owner, lease.Fence);
            dialect.PublishHistory(
                lease.Connection,
                transaction,
                state.TargetIdentity,
                state,
                expectedAppliedTargetFingerprint,
                lease.Owner,
                lease.Fence);
            dialect.AssertFence(lease.Connection, transaction, state.TargetIdentity, lease.Owner, lease.Fence);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var connection = OpenConnection();
        dialect.EnsureInfrastructure(connection);
        var history = dialect.ReadHistory(connection, target.Identity);
        if (history.AppliedState is null)
            return new PhysicalSchemaInspectionResult(history, IsAppliedSchemaValid: true);

        var applied = history.AppliedState;
        var appliedTarget = new PhysicalSchemaTarget(
            applied.Snapshot.Subject,
            applied.Provider,
            applied.Snapshot.ProviderDefinitions);
        using var transaction = dialect.BeginTransaction(connection);
        try
        {
            var inspection = InspectTarget(connection, transaction, appliedTarget, history);
            transaction.Commit();
            return inspection;
        }
        catch (InvalidOperationException)
        {
            transaction.Rollback();
            return new PhysicalSchemaInspectionResult(history, IsAppliedSchemaValid: false);
        }
    }

    public bool TryMapUniqueViolation(DbException exception, out string indexName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return dialect.TryMapUniqueViolation(exception, out indexName);
    }

    private void ExecuteOperation(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaOperation operation)
    {
        switch (operation)
        {
            case CreatePrimaryStorageOperation create:
                Execute(connection, transaction, RelationalSql.CreateTable(dialect, create.Subject.Definition));
                break;
            case AddColumnOperation add:
                // Derived keys for non-null source columns are added as a nullable staging
                // column, populated by the provider-neutral algorithm, then finalized below.
                // Fresh CREATE TABLE plans still materialize the target nullability directly.
                var stagedColumn = add.Column.Name.StartsWith(SearchKeyProjection.Prefix, StringComparison.Ordinal) &&
                                   !add.Column.IsNullable
                    ? add.Column with { IsNullable = true }
                    : add.Column;
                Execute(connection, transaction, RelationalSql.AddColumn(dialect, add.Subject.Name, stagedColumn));
                break;
            case BackfillColumnOperation backfill:
                if (backfill.Derived is not null)
                    BackfillDerivedColumn(connection, transaction, backfill);
                else
                    ExecuteOptional(
                        connection,
                        transaction,
                        dialect.BackfillColumnSql(backfill.Subject.Name, backfill.Column),
                        "BackfillColumn");
                break;
            case FinalizeColumnOperation finalize:
                dialect.FinalizeColumn(connection, transaction, finalize.Subject.Name, finalize.Column);
                break;
            case CreatePhysicalIndexOperation createIndex:
                Execute(connection, transaction, RelationalSql.CreateIndex(dialect, createIndex.Subject.Name, createIndex.Index));
                break;
            case RebuildPhysicalIndexOperation rebuild:
                Execute(connection, transaction, RelationalSql.DropIndex(dialect, rebuild.Subject.Name, rebuild.Index.Name));
                Execute(connection, transaction, RelationalSql.CreateIndex(dialect, rebuild.Subject.Name, rebuild.Index));
                break;
            case ApplyProviderPhysicalSchemaDefinitionOperation applyProvider:
                dialect.ApplyProviderDefinition(connection, transaction, applyProvider.Definition);
                break;
            case ValidatePhysicalSchemaOperation validate:
                ValidateTarget(connection, transaction, validate.Target);
                break;
            case PublishAppliedStateOperation:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, "Unsupported relational schema operation.");
        }
    }

    private static void Execute(DbConnection connection, DbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteOptional(
        DbConnection connection,
        DbTransaction transaction,
        string? sql,
        string operation)
    {
        if (sql is null)
            throw new NotSupportedException($"The relational dialect does not implement {operation}.");
        Execute(connection, transaction, sql);
    }

    private void BackfillDerivedColumn(
        DbConnection connection,
        DbTransaction transaction,
        BackfillColumnOperation operation)
    {
        // A dialect may provide an authoritative SQL backfill for a derived column (for
        // example, a provider-specific generated expression). The shipped dialects return
        // null here because the portable search-key algorithm must run in the host process;
        // retaining this hook keeps custom dialects and contract-test doubles operational.
        if (dialect.BackfillColumnSql(operation.Subject.Name, operation.Column) is { } sql)
        {
            Execute(connection, transaction, sql);
            return;
        }

        var source = operation.Derived!.SourceColumn;
        var keyColumns = operation.Subject.Key.Columns;
        var selected = new[] { source }
            .Concat(keyColumns.Where(column => !string.Equals(column, source, StringComparison.Ordinal)))
            .ToArray();
        var select = $"SELECT {string.Join(", ", selected.Select(dialect.QuoteIdentifier))} FROM {dialect.QuoteIdentifier(operation.Subject.Name)};";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = select;
        using var reader = command.ExecuteReader();
        var definitions = operation.Subject.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var hidden = operation.Column.Name;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (reader.Read())
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < selected.Length; index++)
                values[selected[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(values);
        }
        reader.Close();

        foreach (var values in rows)
        {
            var projected = SearchKeyProjection.Populate(operation.Subject.Definition, values);
            projected.TryGetValue(hidden, out var searchKey);

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {dialect.QuoteIdentifier(operation.Subject.Name)} SET {dialect.QuoteIdentifier(hidden)}=@value WHERE " +
                string.Join(" AND ", keyColumns.Select((column, index) =>
                    $"{dialect.QuoteIdentifier(column)}=@key{index}")) + ";";
            AddParameter(update, "@value", dialect.ConvertValue(searchKey, operation.Column));
            for (var index = 0; index < keyColumns.Count; index++)
                AddParameter(update, "@key" + index, dialect.ConvertValue(values[keyColumns[index]], definitions[keyColumns[index]]));
            update.ExecuteNonQuery();
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private IReadOnlyList<PhysicalSchemaOperationAcknowledgement> ApplyOperationBatchCore(
        RelationalApplicationLock lease,
        PhysicalSchemaTargetIdentity target,
        IReadOnlyList<PhysicalSchemaOperation> operations)
    {
        using var transaction = dialect.BeginTransaction(lease.Connection);
        var acknowledgements = new List<PhysicalSchemaOperationAcknowledgement>(operations.Count);
        try
        {
            dialect.AssertFence(lease.Connection, transaction, target, lease.Owner, lease.Fence);
            foreach (var operation in operations)
            {
                dialect.AssertFence(lease.Connection, transaction, target, lease.Owner, lease.Fence);
                // The neutral planner records Add/Backfill/Finalize for every declaration so
                // providers can acknowledge one stable ledger. Dialects that create all columns
                // in their CREATE TABLE statement must not execute those duplicate operations.
                if (!(dialect.CreateTableIncludesColumns &&
                      operations.Any(item => item is CreatePrimaryStorageOperation) &&
                      operation is AddColumnOperation or BackfillColumnOperation or FinalizeColumnOperation))
                    ExecuteOperation(lease.Connection, transaction, operation);
                acknowledgements.Add(new PhysicalSchemaOperationAcknowledgement(
                    operation.Identity,
                    operation.Fingerprint,
                    DateTimeOffset.UtcNow));
            }

            dialect.AssertFence(lease.Connection, transaction, target, lease.Owner, lease.Fence);
            transaction.Commit();
            return acknowledgements;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void ValidateTarget(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTarget target)
    {
        var inspection = InspectTarget(connection, transaction, target, PhysicalSchemaHistoryState.Empty);
        if (inspection.ColumnDrift.Any() || inspection.IndexDrift.Any())
        {
            var refusal = inspection.ColumnDrift.FirstOrDefault() ?? inspection.IndexDrift.First();
            throw new InvalidOperationException(refusal.Message);
        }
    }

    private PhysicalSchemaInspectionResult InspectTarget(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTarget target,
        PhysicalSchemaHistoryState history)
    {
        var table = target.Subject.Name;
        if (!dialect.TableExists(connection, transaction, table))
        {
            return new PhysicalSchemaInspectionResult(
                history,
                IsAppliedSchemaValid: false,
                ColumnDrift: [new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Relational schema table '{table}' does not exist.",
                    "table")]);
        }

        var columns = dialect.ReadColumns(connection, transaction, table)
            ?? throw new InvalidOperationException($"The relational dialect returned no column catalog for '{table}'.");
        var columnDrift = new List<SchemaRefusal>();
        foreach (var expected in target.Subject.Columns)
        {
            if (!columns.TryGetValue(expected.Name, out var actual) || actual is null)
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Relational schema table '{table}' is missing column '{expected.Name}'.",
                    $"columns.{expected.Name}"));
                continue;
            }
            var expectedKeyOrder = Array.IndexOf(target.Subject.Key.Columns.ToArray(), expected.Name) + 1;
            var differences = new List<string>();
            var expectedType = dialect.MapType(expected);
            var expectedDefault = dialect.MapDefault(expected);
            var expectedCollation = dialect.MapCollation(expected);
            if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal))
                differences.Add($"name '{actual.Name}' != '{expected.Name}'");
            if (!string.Equals(actual.StoreType, expectedType, StringComparison.OrdinalIgnoreCase))
                differences.Add($"type '{actual.StoreType}' != '{expectedType}'");
            if (actual.IsNullable != expected.IsNullable)
                differences.Add($"nullability {actual.IsNullable} != {expected.IsNullable}");
            if (!string.Equals(actual.DefaultValue, expectedDefault, StringComparison.Ordinal))
                differences.Add($"default '{actual.DefaultValue ?? "<none>"}' != '{expectedDefault ?? "<none>"}'");
            if (!string.Equals(actual.Collation, expectedCollation, StringComparison.OrdinalIgnoreCase))
                differences.Add($"collation '{actual.Collation ?? "<none>"}' != '{expectedCollation ?? "<none>"}'");
            if (actual.PrimaryKeyOrder != expectedKeyOrder)
                differences.Add($"primary-key order {actual.PrimaryKeyOrder} != {expectedKeyOrder}");
            if (actual.Generation != expected.Generation)
                differences.Add($"generation {actual.Generation} != {expected.Generation}");
            if (actual.IsComputed)
                differences.Add("computed column is true");
            if (actual.IsPersisted)
                differences.Add("persisted computed column is true");
            if (actual.ComputedDefinition is not null)
                differences.Add($"computed definition '{actual.ComputedDefinition}' is present");
            if (differences.Count != 0)
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Relational schema column '{table}.{expected.Name}' differs: {string.Join(", ", differences)}.",
                    $"columns.{expected.Name}"));
            }
        }

        if (target.Subject.DerivedColumns.Length != 0)
        {
            var algorithms = dialect.ReadDerivedSearchKeyAlgorithms(connection, transaction, table)
                ?? throw new InvalidOperationException(
                    $"The relational dialect returned no search-key algorithm catalog for '{table}'.");
            foreach (var expected in target.Subject.DerivedColumns)
            {
                var expectedAlgorithm = ProjectionAlgorithmId(expected);
                if (!algorithms.TryGetValue(expected.Name, out var actualAlgorithm) ||
                    !string.Equals(actualAlgorithm, expectedAlgorithm, StringComparison.Ordinal))
                {
                    columnDrift.Add(new SchemaRefusal(
                        "GW-RUNTIME-001",
                        $"Relational persisted search-key algorithm for derived column '{table}.{expected.Name}' differs: " +
                        $"'{actualAlgorithm ?? "<missing>"}' != '{expectedAlgorithm}'.",
                        $"columns.{expected.Name}.searchKeyAlgorithm"));
                }
            }
        }

        var indexDrift = new List<SchemaRefusal>();
        foreach (var expectedIndex in target.Subject.Indexes)
        {
            var actual = dialect.ReadIndex(connection, transaction, table, expectedIndex.Name);
            if (actual is null)
            {
                indexDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Relational schema table '{table}' is missing index '{expectedIndex.Name}'.",
                    $"indexes.{expectedIndex.Name}"));
                continue;
            }
            if (actual.IsUnique != expectedIndex.IsUnique)
            {
                indexDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Relational schema index '{table}.{expectedIndex.Name}' has unexpected uniqueness.",
                    $"indexes.{expectedIndex.Name}"));
                continue;
            }
            var expectedColumns = expectedIndex.Columns
                .Select(column => new RelationalIndexColumnMetadata(column.Column, column.Direction))
                .ToArray();
            if (!IndexColumnsMatch(actual.Columns, expectedColumns) ||
                !string.Equals(
                    NormalizeIndexFilter(actual.Filter),
                    NormalizeIndexFilter(dialect.IndexFilter(expectedIndex)),
                    StringComparison.Ordinal))
            {
                indexDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Relational schema index '{table}.{expectedIndex.Name}' does not match its declaration.",
                    $"indexes.{expectedIndex.Name}"));
            }
        }

        var inspection = new PhysicalSchemaInspectionResult(
            history,
            IsAppliedSchemaValid: columnDrift.Count == 0,
            columnDrift.ToImmutableArray(),
            indexDrift.ToImmutableArray());

        if (inspection.IsAppliedSchemaValid)
        {
            try
            {
                // Provider invariants remain part of the non-mutating open/inspection path. A
                // provider may reject a catalog shape that the neutral checks cannot describe.
                dialect.ValidateTarget(connection, transaction, target);
            }
            catch (InvalidOperationException exception)
            {
                return inspection with
                {
                    IsAppliedSchemaValid = false,
                    ColumnDrift = [new SchemaRefusal(
                        "GW-RUNTIME-001",
                        $"Relational provider invariant failed: {exception.Message}",
                        "provider")]
                };
            }
        }

        return inspection;
    }

    private static bool IndexColumnsMatch(
        IReadOnlyList<RelationalIndexColumnMetadata> actual,
        IReadOnlyList<RelationalIndexColumnMetadata> expected)
    {
        if (actual.Count != expected.Count)
            return false;
        for (var index = 0; index < expected.Count; index++)
        {
            var actualColumn = actual[index];
            var expectedColumn = expected[index];
            if (!string.Equals(actualColumn.Name, expectedColumn.Name, StringComparison.Ordinal) ||
                actualColumn.Direction != expectedColumn.Direction)
                return false;

            // Dialects that expose provider null ordering report it. A null value preserves
            // the historical direction-only contract for catalogs that cannot expose this bit.
            var expectedNullsFirst = expectedColumn.Direction == SortDirection.Ascending;
            if (actualColumn.NullsFirst is { } nullsFirst && nullsFirst != expectedNullsFirst)
                return false;
        }
        return true;
    }

    private static string ProjectionAlgorithmId(DerivedColumnDefinition definition) => definition.AlgorithmId ?? definition.Projection switch
    {
        PortableProjection.UnicodeFold => PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId,
        PortableProjection.BoundarySearchKey => PortableStringComparison.SearchKeyAlgorithmId,
        PortableProjection.Sha256 => PortableStringComparison.LookupHashAlgorithmId,
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Projection, null)
    };

    private static string? NormalizeIndexFilter(string? filter) =>
        filter is null
            ? null
            : new string(filter.Where(character =>
                !char.IsWhiteSpace(character) &&
                character is not ('"' or '[' or ']' or '`' or '(' or ')')).ToArray());

    private DbConnection OpenConnection()
    {
        var connection = createConnection()
            ?? throw new InvalidOperationException("The relational connection factory returned null.");
        try
        {
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private RelationalApplicationLock RequireLock(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (applicationLock is not RelationalApplicationLock lease || lease.Target != target)
            throw new ArgumentException("The application lock was not created for this relational executor and target.", nameof(applicationLock));
        return lease;
    }
}

/// <summary>Dedicated connection and fencing lease returned by the relational executor.</summary>
public sealed class RelationalApplicationLock : IPhysicalSchemaApplicationLock
{
    private readonly RelationalDialect dialect;
    private readonly string resource;
    private bool disposed;

    internal RelationalApplicationLock(
        DbConnection connection,
        RelationalDialect dialect,
        PhysicalSchemaTargetIdentity target,
        string resource,
        string owner,
        long fence,
        long serverSessionId)
    {
        Connection = connection;
        this.dialect = dialect;
        Target = target;
        this.resource = resource;
        Owner = owner;
        Fence = fence;
        ServerSessionId = serverSessionId;
    }

    public PhysicalSchemaTargetIdentity Target { get; }

    internal DbConnection Connection { get; }

    internal string Owner { get; }

    internal long Fence { get; }

    /// <summary>Provider session identity captured with the application lock.</summary>
    public long ServerSessionId { get; }

    internal void Verify()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!dialect.VerifyApplicationLock(Connection, resource))
            throw new InvalidOperationException($"The relational application lock '{resource}' is no longer held.");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            dialect.ReleaseApplicationLock(Connection, resource);
        }
        finally
        {
            Connection.Dispose();
        }
    }
}
