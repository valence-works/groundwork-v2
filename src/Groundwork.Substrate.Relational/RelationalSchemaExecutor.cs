using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
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
                Execute(connection, transaction, RelationalSql.AddColumn(dialect, add.Subject.Name, add.Column));
                break;
            case BackfillColumnOperation backfill:
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

        dialect.ValidateTarget(connection, transaction, target);
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
            if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal) ||
                !string.Equals(actual.StoreType, dialect.MapType(expected), StringComparison.OrdinalIgnoreCase) ||
                actual.IsNullable != expected.IsNullable ||
                !string.Equals(actual.DefaultValue, dialect.MapDefault(expected), StringComparison.Ordinal) ||
                !string.Equals(actual.Collation, dialect.MapCollation(expected), StringComparison.OrdinalIgnoreCase) ||
                actual.PrimaryKeyOrder != expectedKeyOrder ||
                actual.IsComputed ||
                actual.IsPersisted ||
                actual.ComputedDefinition is not null)
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Relational schema column '{table}.{expected.Name}' does not match its declaration.",
                    $"columns.{expected.Name}"));
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
            if (!actual.Columns.SequenceEqual(expectedColumns) ||
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

        return new PhysicalSchemaInspectionResult(
            history,
            IsAppliedSchemaValid: columnDrift.Count == 0,
            columnDrift.ToImmutableArray(),
            indexDrift.ToImmutableArray());
    }

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
