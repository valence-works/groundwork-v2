using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using MySqlConnector;

namespace Groundwork.MySql;

/// <summary>Creates MySQL/MariaDB provider connections.</summary>
public sealed class MySqlProviderFactory : IStorageProviderFactory
{
    public IStorageProviderConnection Create(string connectionString) =>
        new MySqlProviderConnection(connectionString);
}

/// <summary>A provider connection backed by MySqlConnector and the shared relational runtime.</summary>
public sealed class MySqlProviderConnection : IStorageProviderConnection, IQueryAdmissionProviderConnection
{
    private readonly string connectionString;
    private readonly object registryGate = new();
    private readonly ConcurrentBag<MySqlConnection> ownedConnections = [];
    private readonly ConcurrentDictionary<StorageUnitId, StorageUnit> units = new();
    private readonly MySqlSchemaCoordinator schema;
    private readonly SchemaSessionPublicationRegistry schemaSessions = new();
    private bool disposed;

    public MySqlProviderConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            // Groundwork classifies a matched no-op update as Updated. CLIENT_FOUND_ROWS keeps
            // that result independent of whether a supplied value happened to change.
            UseAffectedRows = false
        }.ConnectionString;
        schema = new MySqlSchemaCoordinator(this);
        Schema = schema;
        Catalog = new MySqlProviderCatalog(this);
    }

    public QueryAdmissionProfile QueryAdmission { get; } = new()
    {
        MaximumParameters = MySqlQueryRenderer.ParameterBudget,
        MaximumBatchReadKeys = MySqlQueryRenderer.ParameterBudget
    };

    public IProviderCatalog Catalog { get; }

    public ISchemaCoordinator Schema { get; }

    public IReadOnlyList<CapabilityDescriptor> Capabilities =>
        SchemaCapabilityAdmission.AdvertiseEnforcedConstraints(BatchWriteCapabilities.ForProvider(
            "MySQL/MariaDB",
            nativeBatch: false,
            exactOutcomeCost: "one native command per row through the shared relational fallback",
            batchCost: "uses the shared transaction-bound row fallback on the first provider release",
            exactAppendOutcomes: true,
            durableHighWaterInspection: false,
            exactRetention: true,
            atomicCommit: true,
            compareAndDelete: false,
            setMutation: "Updates or deletes every row matching an index-covered portable predicate in one MySQL/MariaDB statement."));

    public IStorageSession OpenSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer = null)
    {
        var connection = PrepareSession(unit, access, observer);
        var lifetime = new MySqlSessionLifetime(nameof(MySqlStorageSession));
        try
        {
            var physical = Resolve(unit);
            var session = new MySqlStorageSession(
                this, physical, access, connection, transaction: null,
                new SemaphoreSlim(1, 1), lifetime, ownsConnection: false,
                CaptureSchemaSession(physical), observer);
            Own(connection);
            return session;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public IOwnedStorageSession OpenOwnedSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer = null)
    {
        var connection = PrepareSession(unit, access, observer);
        try
        {
            var physical = Resolve(unit);
            return new OwnedMySqlStorageSession(
                this, physical, access, connection, CaptureSchemaSession(physical), observer);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units) =>
        BeginUnitOfWork(access, BatchWriteOptions.Default, observer: null, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        params StorageUnit[] units) =>
        BeginUnitOfWork(access, options, observer: null, units);

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
        if (units.Any(unit => unit is null))
            throw new ArgumentException("A unit of work cannot contain a null storage unit.", nameof(units));
        if (units.Select(unit => unit.Id).Distinct().Count() != units.Length)
            throw new ArgumentException("A unit of work cannot list the same storage unit twice.", nameof(units));

        var connection = OpenConnection();
        try
        {
            foreach (var unit in units)
            {
                MySqlSchemaCoordinator.ValidateAccess(unit, access);
                schema.EnsureRuntimeAdmission(unit, observer, connection);
                EnsureSessionInfrastructure(connection, MySqlSchemaCoordinator.Physicalize(unit));
            }

            var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            Own(connection);
            var physical = units.ToDictionary(unit => unit.Id, Resolve);
            var gate = new SemaphoreSlim(1, 1);
            var lifetime = new RelationalUnitOfWorkLifetime(
                connection,
                transaction,
                supportsAsync: true,
                disposeTransaction: true);
            return new RelationalUnitOfWork(
                units,
                options,
                unit =>
                {
                    var session = new MySqlStorageSession(
                        this,
                        physical[unit.Id],
                        access,
                        connection,
                        transaction,
                        gate,
                        new MySqlSessionLifetime(nameof(MySqlStorageSession)),
                        ownsConnection: false,
                        CaptureSchemaSession(physical[unit.Id]),
                        observer);
                    return new RelationalUnitOfWorkSession(session, session.Close);
                },
                lifetime);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal MySqlConnection OpenConnection()
    {
        ThrowIfDisposed();
        var connection = new MySqlConnection(connectionString);
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

    internal void Remember(StorageUnit source) =>
        units[source.Id] = MySqlSchemaCoordinator.Physicalize(source);

    internal StorageUnit Resolve(StorageUnit source) =>
        units.TryGetValue(source.Id, out var physical)
            ? physical
            : MySqlSchemaCoordinator.Physicalize(source);

    internal SchemaSessionLease CaptureSchemaSession(StorageUnit physicalUnit) =>
        schemaSessions.Capture(MySqlSchemaCoordinator.Target(physicalUnit));

    internal void PublishSchema(PhysicalSchemaTarget target) => schemaSessions.Publish(target);

    internal void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MySqlProviderConnection));
    }

    public void Dispose()
    {
        List<MySqlConnection> connections = [];
        lock (registryGate)
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

    private MySqlConnection PrepareSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        PortabilityValidator.EnsurePhysicalIdentifiers(unit);
        MySqlSchemaCoordinator.ValidateAccess(unit, access);
        var connection = OpenConnection();
        try
        {
            schema.EnsureRuntimeAdmission(unit, observer, connection);
            EnsureSessionInfrastructure(connection, MySqlSchemaCoordinator.Physicalize(unit));
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Own(MySqlConnection connection)
    {
        lock (registryGate)
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(MySqlProviderConnection));
            ownedConnections.Add(connection);
        }
    }

    private static void EnsureSessionInfrastructure(MySqlConnection connection, StorageUnit unit)
    {
        foreach (var ledger in new[]
                 {
                     unit.AppendIdempotency?.LedgerName,
                     unit.RetentionIdempotency?.LedgerName
                 }.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal))
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE IF NOT EXISTS {MySqlDialect.Quote(ledger!)} (" +
                $"`unit` varchar(191) CHARACTER SET utf8mb4 COLLATE {MySqlDialect.OrdinalCollation} NOT NULL, " +
                $"`scope` varchar(191) CHARACTER SET utf8mb4 COLLATE {MySqlDialect.OrdinalCollation} NOT NULL, " +
                $"`nonce` varchar(256) CHARACTER SET utf8mb4 COLLATE {MySqlDialect.OrdinalCollation} NOT NULL, " +
                "`committed_at` varchar(40) CHARACTER SET ascii COLLATE ascii_bin NOT NULL, " +
                "`input_fingerprint` varchar(128) CHARACTER SET ascii COLLATE ascii_bin NULL, " +
                $"`exact_result` longtext CHARACTER SET utf8mb4 COLLATE {MySqlDialect.OrdinalCollation} NULL, " +
                $"PRIMARY KEY (`unit`, `scope`, `nonce`)) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE {MySqlDialect.OrdinalCollation};";
            command.ExecuteNonQuery();
        }
    }
}

internal sealed class MySqlSchemaCoordinator : ISchemaCoordinator
{
    internal static readonly ProviderIdentity Identity = new("MySQL/MariaDB", "1.0");
    internal static readonly ProviderOwnedColumnPolicy ColumnPolicy = new()
    {
        ProviderName = "MySQL/MariaDB",
        ScopeMaxLength = 128,
        ScopeJoinsGeneratedKey = false,
        DeclaresAppendAction = true
    };
    private readonly MySqlProviderConnection owner;
    private readonly RelationalSchemaExecutor executor;
    private readonly ConcurrentDictionary<StorageUnitId, StorageUnit> units = new();
    private readonly RelationalRuntimeAdmission admission;
    private readonly object applicationGate = new();

    internal MySqlSchemaCoordinator(MySqlProviderConnection owner)
    {
        this.owner = owner;
        executor = new RelationalSchemaExecutor(owner.OpenConnection, new MySqlDialect());
        admission = new RelationalRuntimeAdmission(
            "mysql.schema-admission",
            desired => Target(Physicalize(desired)),
            (target, connection) => connection is null
                ? executor.InspectDeployedHistory(target)
                : executor.InspectDeployedHistory(target, connection));
    }

    internal StorageUnit? Find(StorageUnitId id) => units.TryGetValue(id, out var unit) ? unit : null;

    internal void EnsureRuntimeAdmission(
        StorageUnit desired,
        IProviderCommandObserver? observer,
        DbConnection connection) => admission.EnsureAdmitted(desired, observer, connection);

    public GroundworkRuntimeSchemaAdmissionResult InspectRuntimeAdmission(
        StorageUnit desired,
        GroundworkRuntimeSchemaAdmissionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        lock (applicationGate)
        {
            var physical = Physicalize(desired);
            Remember(desired, physical);
            var target = Target(physical);
            var result = GroundworkRuntimeSchemaAdmission.InspectRuntimeAdmission(
                executor,
                target,
                options,
                inspected: executor.InspectDeployedHistory(target),
                inspectAfterApplication: () => executor.InspectDeployedHistory(target));
            if (result.Application?.Outcome is PhysicalSchemaApplicationOutcome.Applied or
                PhysicalSchemaApplicationOutcome.NoChanges)
            {
                owner.PublishSchema(target);
                owner.Remember(desired);
                admission.Invalidate(desired.Id);
            }
            return result;
        }
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
        return new SchemaDiff(SchemaChangeMapping.Describe(plan, physical));
    }

    public SchemaApplyResult Apply(StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        lock (applicationGate)
        {
            var physical = Physicalize(desired);
            Remember(desired, physical);
            var target = Target(physical);
            try
            {
                var result = PhysicalSchemaApplication.ApplyRecoverableWork(target, executor);
                if (result.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.NoChanges)
                    owner.PublishSchema(target);
                owner.Remember(desired);
                return new SchemaApplyResult(
                    new SchemaDiff(SchemaChangeMapping.Describe(result.Plan, physical)),
                    result.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.NoChanges);
            }
            finally
            {
                admission.Invalidate(desired.Id);
            }
        }
    }

    internal static PhysicalSchemaTarget Target(StorageUnit physical) => new(
        new SchemaSubject(physical),
        Identity,
        RelationalInteropViewDefinition.AppendTo(
            Identity.Name,
            physical,
            physical.DerivedColumns.Select(derived => new ProviderPhysicalSchemaDefinition(
                Identity.Name,
                physical.Id,
                RelationalDialect.SearchKeyDefinitionKind,
                physical.Name + RelationalDialect.SearchKeyDefinitionSeparator + derived.Name,
                derived.AlgorithmId ?? throw new InvalidOperationException(
                    $"Derived search-key column '{derived.Name}' is missing its algorithm identity.")))));

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
        ProviderOwnedColumns.ValidateLogicalDeclaration(source);
        PortabilityValidator.EnsurePortableDefaults(source);
        PortabilityValidator.EnsurePhysicalIdentifiers(source);
        var portability = PortabilityValidator.Validate(source);
        if (!portability.IsPortable)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                portability.Refusals.Select(refusal => $"{refusal.Code} at {refusal.Path}: {refusal.Message}")));
        }
        return ProviderOwnedColumns.Physicalize(source, ColumnPolicy);
    }

    private void Remember(StorageUnit original, StorageUnit physical) => units[original.Id] = physical;
}

internal sealed class MySqlProviderCatalog(MySqlProviderConnection owner) : IProviderCatalog
{
    private readonly MySqlDialect dialect = new();

    public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId)
    {
        owner.ThrowIfDisposed();
        var unit = ((MySqlSchemaCoordinator)owner.Schema).Find(storageUnitId)
            ?? throw new InvalidOperationException(
                $"Storage unit '{storageUnitId.Value}' has not been applied by this connection.");
        using var connection = owner.OpenConnection();
        var indexes = new List<ProviderIndex>();
        foreach (var index in unit.Indexes)
        {
            var metadata = dialect.ReadIndex(connection, null, unit.Name, index.Name);
            if (metadata is null)
                continue;
            indexes.Add(new ProviderIndex(
                index.Name,
                metadata.Columns
                    .Where(column => column.Name != ProviderOwnedColumns.Scope)
                    .Select(column => new ProviderIndexColumn(column.Name, column.Direction))
                    .ToArray(),
                metadata.IsUnique,
                index.MissingValues,
                index.SchemaVersion));
        }
        return indexes;
    }
}
