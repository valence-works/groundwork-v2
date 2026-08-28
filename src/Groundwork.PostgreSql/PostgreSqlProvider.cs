using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using Groundwork.Store;
using Groundwork.Diagnostics;
using Npgsql;

namespace Groundwork.PostgreSql;

/// <summary>Creates PostgreSQL provider-neutral storage connections.</summary>
public sealed class PostgreSqlProviderFactory : IStorageProviderFactory
{
    public IStorageProviderConnection Create(string connectionString) =>
        new PostgreSqlProviderConnection(connectionString);
}

public sealed class PostgreSqlProviderConnection : IStorageProviderConnection, IQueryAdmissionProviderConnection
{
    /// <summary>
    /// The budget PostgreSqlQueryRenderer enforces, so the pre-execution fence and the renderer cannot
    /// disagree about it.
    /// </summary>
    public QueryAdmissionProfile QueryAdmission { get; } = new()
    {
        MaximumParameters = PostgreSqlQueryRenderer.ParameterBudget,
        MaximumBatchReadKeys = PostgreSqlQueryRenderer.ParameterBudget
    };

    private readonly string connectionString;

    /// <summary>
    /// Serializes the operations of every session this connection owns.
    /// </summary>
    /// <remarks>
    /// Consumers cache and share sessions — Elsa's storage session source keys them by target/unit/access
    /// and hands the same instance to concurrent callers — and Npgsql refuses concurrent commands on one
    /// connection outright (<c>NpgsqlOperationInProgressException</c>). Serializing makes a shared session
    /// slow under contention rather than unusable. Mirrors the gate SqlServerProviderConnection already
    /// carries; a semaphore rather than a monitor because the async paths hold it across an await.
    ///
    /// It is NOT re-entrant. The session takes it in exactly two places — its Execute and ExecuteWrite
    /// funnels — which are the outermost wrapper of every public operation and never nest inside one
    /// another: on-append retention runs sequentially after its write completes and reaches the retention
    /// core directly rather than through a funnel.
    /// </remarks>
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object connectionRegistryGate = new();

    private readonly ConcurrentDictionary<StorageUnitId, StorageUnit> units = new();

    internal IDisposable EnterGate()
    {
        gate.Wait();
        return new GateScope(gate);
    }

    internal ValueTask<IDisposable> EnterGate(RelationalExecution mode) =>
        mode.IsAsync ? EnterGateAsync(mode.CancellationToken) : new(EnterGate());

    private async ValueTask<IDisposable> EnterGateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateScope(gate);
    }

    private sealed class GateScope(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
    private readonly ConcurrentBag<NpgsqlConnection> ownedConnections = [];
    private readonly PostgreSqlSchemaCoordinator schemaCoordinator;
    private volatile ISessionRegistrationObserver? activeRegistrationObserver;
    private volatile bool disposed;

    public PostgreSqlProviderConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
        schemaCoordinator = new PostgreSqlSchemaCoordinator(this);
        Schema = schemaCoordinator;
        Catalog = new PostgreSqlProviderCatalog(this);
    }

    public IProviderCatalog Catalog { get; }

    public ISchemaCoordinator Schema { get; }

    public IReadOnlyList<CapabilityDescriptor> Capabilities => BatchWriteCapabilities.ForProvider(
        "PostgreSQL", nativeBatch: true,
        exactOutcomeCost: "one RETURNING result per native batch",
        batchCost: "uses multi-row INSERT/ON CONFLICT with a 32,000-parameter safety limit; secondary unique declarations use the row-attributed fallback",
        exactAppendOutcomes: true,
        durableHighWaterInspection: true,
        exactRetention: true,
        atomicCommit: true,
        compareAndDelete: true,
        setMutation: "Updates or deletes every row matching an index-covered portable predicate on PostgreSQL in one UPDATE/DELETE statement; the statement is atomic and reports its affected-row count.");

    internal string ConnectionString => connectionString;

    internal void Remember(StorageUnit source) =>
        units[source.Id] = PostgreSqlSchemaCoordinator.Physicalize(source);

    internal StorageUnit Resolve(StorageUnit source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return units.TryGetValue(source.Id, out var physical)
            ? physical
            : PostgreSqlSchemaCoordinator.Physicalize(source);
    }

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access, IProviderCommandObserver? observer = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        PortabilityValidator.EnsurePhysicalIdentifiers(unit);
        PostgreSqlSchemaCoordinator.ValidateAccess(unit, access);
        var physicalUnit = Resolve(unit);
        var connection = OpenConnection();
        try
        {
            schemaCoordinator.EnsureRuntimeAdmission(unit, observer, connection);
            OwnConnection(connection, observer);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        return new PostgreSqlStorageSession(this, physicalUnit, access, connection, null, observer);
    }

    public IOwnedStorageSession OpenOwnedSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        PortabilityValidator.EnsurePhysicalIdentifiers(unit);
        PostgreSqlSchemaCoordinator.ValidateAccess(unit, access);
        var physicalUnit = Resolve(unit);
        var connection = OpenConnection();
        try
        {
            schemaCoordinator.EnsureRuntimeAdmission(unit, observer, connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        // Deliberately NOT OwnConnection: the caller releases it, which is the whole point — a per-caller
        // session that neither leaks a connection nor queues behind unrelated callers on a shared one.
        return new OwnedPostgreSqlStorageSession(this, physicalUnit, access, connection, observer);
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units)
        => BeginUnitOfWork(access, BatchWriteOptions.Default, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        params StorageUnit[] units)
        => BeginUnitOfWork(access, options, observer: null, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        IProviderCommandObserver? observer,
        params StorageUnit[] units)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(units);
        StorageAccessValidation.EnsureUnitOfWork(access);
        if (units.Length == 0)
            throw new ArgumentException("A unit of work must declare at least one storage unit.", nameof(units));
        if (units.Select(unit => unit.Id).Distinct().Count() != units.Length)
            throw new ArgumentException("A unit of work cannot list the same storage unit twice.", nameof(units));
        var connection = OpenConnection();
        try
        {
            foreach (var unit in units)
            {
                ArgumentNullException.ThrowIfNull(unit);
                PortabilityValidator.EnsurePhysicalIdentifiers(unit);
                PostgreSqlSchemaCoordinator.ValidateAccess(unit, access);
                schemaCoordinator.EnsureRuntimeAdmission(unit, observer, connection);
            }

            var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            OwnConnection(connection);
            return new PostgreSqlUnitOfWork(this, connection, transaction, units, access, options, observer);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal NpgsqlConnection OpenConnection()
    {
        ThrowIfDisposed();
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal void OwnConnection(
        NpgsqlConnection connection,
        IProviderCommandObserver? observer = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (connectionRegistryGate)
        {
            if (disposed)
            {
                connection.Dispose();
                throw new ObjectDisposedException(nameof(PostgreSqlProviderConnection));
            }
            if (observer is ISessionRegistrationObserver registrationObserver)
            {
                activeRegistrationObserver = registrationObserver;
                try
                {
                    registrationObserver.OnSessionRegistrationEligibilityChecked();
                    if (disposed)
                        throw new ObjectDisposedException(nameof(PostgreSqlProviderConnection));
                    ownedConnections.Add(connection);
                }
                finally
                {
                    activeRegistrationObserver = null;
                }
            }
            else
            {
                ownedConnections.Add(connection);
            }
        }
    }

    internal void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(PostgreSqlProviderConnection));
    }

    public void Dispose()
    {
        activeRegistrationObserver?.OnProviderDisposalAttempted();
        List<NpgsqlConnection> connections = [];
        lock (connectionRegistryGate)
        {
            if (disposed)
                return;
            disposed = true;
            while (ownedConnections.TryTake(out var connection))
                connections.Add(connection);
        }
        foreach (var connection in connections)
            connection.Dispose();
    }
}

internal sealed class PostgreSqlSchemaCoordinator : ISchemaCoordinator
{
    internal const string ScopeColumn = "__groundwork_scope";
    internal const string VersionColumn = "__groundwork_version";
    internal static readonly ProviderIdentity Identity = new("PostgreSQL", "1.0");
    internal static readonly ProviderOwnedColumnPolicy ColumnPolicy = new() { ProviderName = "PostgreSQL" };
    private readonly PostgreSqlProviderConnection owner;
    private readonly RelationalSchemaExecutor executor;
    private readonly PostgreSqlDialect dialect = new();
    private readonly ConcurrentDictionary<StorageUnitId, StorageUnit> units = new();
    private readonly RelationalRuntimeAdmission admission;

    internal PostgreSqlSchemaCoordinator(PostgreSqlProviderConnection owner)
    {
        this.owner = owner;
        executor = new RelationalSchemaExecutor(owner.OpenConnection, dialect);
        admission = new RelationalRuntimeAdmission(
            "postgresql.schema-admission",
            desired => Target(Physicalize(desired)),
            (target, connection) => connection is null
                ? executor.InspectDeployedHistory(target)
                : executor.InspectDeployedHistory(target, connection));
    }

    internal StorageUnit? Find(StorageUnitId id) => units.TryGetValue(id, out var unit) ? unit : null;

    internal void EnsureRuntimeAdmission(
        StorageUnit desired,
        IProviderCommandObserver? observer = null,
        DbConnection? connection = null) =>
        admission.EnsureAdmitted(desired, observer, connection);

    public GroundworkRuntimeSchemaAdmissionResult InspectRuntimeAdmission(
        StorageUnit desired,
        GroundworkRuntimeSchemaAdmissionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var physical = Physicalize(desired);
        Remember(desired, physical);
        var target = Target(physical);
        var result = GroundworkRuntimeSchemaAdmission.InspectRuntimeAdmission(
            executor,
            target,
            options,
            inspected: executor.InspectDeployedHistory(target),
            inspectAfterApplication: () => executor.InspectDeployedHistory(target));
        if (result.AppliedOperationCount != 0)
        {
            owner.Remember(desired);
            admission.Invalidate(desired.Id);
        }
        return result;
    }

    public SchemaDiff Diff(StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var physical = Physicalize(desired);
        Remember(desired, physical);
        var target = Target(physical);
        using var lease = executor.AcquireApplicationLock(target.Identity);
        var history = executor.ReadHistory(target.Identity, lease);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UtcNow);
        return new SchemaDiff(SchemaChangeMapping.Describe(
            plan.Operations,
            plan.PreviousDefinition,
            physical));
    }

    public SchemaApplyResult Apply(StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var physical = Physicalize(desired);
        Remember(desired, physical);
        var target = Target(physical);
        try
        {
            var result = PhysicalSchemaApplication.ApplyRecoverableWork(target, executor);
            owner.Remember(desired);
            return new SchemaApplyResult(new SchemaDiff(SchemaChangeMapping.Describe(
                    result.Plan.Operations,
                    result.Plan.PreviousDefinition,
                    physical)),
                result.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.NoChanges);
        }
        finally
        {
            admission.Invalidate(desired.Id);
        }
    }

    internal static PhysicalSchemaTarget Target(StorageUnit physical) =>
        new(
            new SchemaSubject(physical),
            Identity,
            physical.DerivedColumns.Select(derived => new ProviderPhysicalSchemaDefinition(
                "PostgreSQL",
                physical.Id,
                RelationalDialect.SearchKeyDefinitionKind,
                physical.Name + RelationalDialect.SearchKeyDefinitionSeparator + derived.Name,
                derived.AlgorithmId ?? throw new InvalidOperationException($"Derived search-key column '{derived.Name}' is missing its algorithm identity."))).ToArray());

    internal static void ValidateAccess(StorageUnit unit, StorageAccess access)
    {
        if (unit.Scope != access.Policy)
            throw new InvalidOperationException($"Storage unit '{unit.Name}' requires {unit.Scope} access.");
        if (unit.Scope == ScopePolicy.Scoped && access.Scope is null && !access.IsPrivilegedAcrossScopes)
            throw new InvalidOperationException($"Storage unit '{unit.Name}' requires a storage scope.");
        if (unit.Scope == ScopePolicy.Global && access.Scope is not null)
            throw new InvalidOperationException($"Storage unit '{unit.Name}' is global and cannot use a storage scope.");
    }

    internal static StorageUnit Physicalize(StorageUnit source)
    {
        ArgumentNullException.ThrowIfNull(source);
        PortabilityValidator.EnsurePortableDefaults(source);
        PortabilityValidator.EnsurePhysicalIdentifiers(source);
        EnsurePhysicalIndexNames(source);
        if (source.Retention is not null)
        {
            var portability = PortabilityValidator.Validate(source);
            if (!portability.IsPortable)
                throw new InvalidOperationException(string.Join(
                    Environment.NewLine,
                    portability.Refusals.Select(refusal =>
                        $"{refusal.Code} at {refusal.Path}: {refusal.Message}")));
        }
        if (source.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence))
        {
            var portability = PortabilityValidator.Validate(source);
            if (!portability.IsPortable)
                throw new InvalidOperationException(string.Join(
                    Environment.NewLine,
                    portability.Refusals.Select(refusal =>
                        $"{refusal.Code} at {refusal.Path}: {refusal.Message}")));
        }
        return ProviderOwnedColumns.Physicalize(source, ColumnPolicy);
    }

    private static void EnsurePhysicalIndexNames(StorageUnit source)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var index in source.Indexes)
        {
            var physicalName = PostgreSqlDialect.PhysicalIndexName(source.Name, index.Name);
            var path = $"indexes.{index.Name}.physicalName";
            PortabilityValidator.EnsurePhysicalIdentifier(
                physicalName,
                path,
                allowProviderOwnedPrefix: true);
            if (seen.TryGetValue(physicalName, out var previous))
            {
                throw new InvalidOperationException(
                    $"GW-PORT-011 at {path}: Provider-generated physical index name '{physicalName}' " +
                    $"collides with index '{previous}'; choose identifiers whose composed names remain unique.");
            }
            seen.Add(physicalName, index.Name);
        }
    }

    private void Remember(StorageUnit original, StorageUnit physical) => units[original.Id] = physical;
}

internal sealed class PostgreSqlProviderCatalog
    : IProviderCatalog
{
    private readonly PostgreSqlProviderConnection owner;
    private readonly PostgreSqlDialect dialect = new();

    internal PostgreSqlProviderCatalog(PostgreSqlProviderConnection owner) => this.owner = owner;

    public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId)
    {
        owner.ThrowIfDisposed();
        var unit = ((PostgreSqlSchemaCoordinator)owner.Schema).Find(storageUnitId)
            ?? throw new InvalidOperationException($"Storage unit '{storageUnitId.Value}' has not been applied by this connection.");
        using var connection = owner.OpenConnection();
        var indexes = new List<ProviderIndex>();
        foreach (var index in unit.Indexes)
        {
            var metadata = dialect.ReadIndex(connection, null, unit.Name, index.Name);
            if (metadata is null)
                continue;
            indexes.Add(new ProviderIndex(index.Name,
                metadata.Columns.Where(column => column.Name != PostgreSqlSchemaCoordinator.ScopeColumn)
                    .Select(column => new ProviderIndexColumn(column.Name, column.Direction)).ToArray(),
                metadata.IsUnique, index.MissingValues, index.SchemaVersion));
        }
        return indexes;
    }
}
