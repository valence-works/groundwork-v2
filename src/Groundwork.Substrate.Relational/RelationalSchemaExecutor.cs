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
        using var transaction = lease.Connection.BeginTransaction();
        dialect.AssertFence(lease.Connection, transaction, target, lease.Owner, lease.Fence);
        ExecuteOperation(lease.Connection, transaction, operation);
        dialect.AssertFence(lease.Connection, transaction, target, lease.Owner, lease.Fence);
        transaction.Commit();
        return new PhysicalSchemaOperationAcknowledgement(
            operation.Identity,
            operation.Fingerprint,
            DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<PhysicalSchemaOperationAcknowledgement> ApplyOperationBatch(
        PhysicalSchemaTargetIdentity target,
        IReadOnlyList<PhysicalSchemaOperation> operations,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations.Select(operation => ApplyOperation(target, operation, applicationLock)).ToArray();
    }

    public void PublishAppliedState(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (expectedAppliedTargetFingerprint is not null &&
            !string.Equals(expectedAppliedTargetFingerprint, state.TargetFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The applied schema fingerprint does not match the expected fingerprint.");
        }

        var lease = RequireLock(state.TargetIdentity, applicationLock);
        lease.Verify();
        dialect.PublishHistory(lease.Connection, state);
    }

    public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var connection = OpenConnection();
        dialect.EnsureInfrastructure(connection);
        var history = dialect.ReadHistory(connection, target.Identity);
        return new PhysicalSchemaInspectionResult(history, IsAppliedSchemaValid: true);
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
                Execute(connection, transaction, RelationalSql.FinalizeColumn(dialect, finalize.Subject.Name, finalize.Column.Name));
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
                dialect.ValidateTarget(connection, transaction, validate.Target);
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

    public DbConnection Connection { get; }

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
