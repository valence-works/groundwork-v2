using System.Collections.Concurrent;
using System.Data;
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

public sealed class PostgreSqlProviderConnection : IStorageProviderConnection
{
    private readonly string connectionString;
    private readonly ConcurrentDictionary<StorageUnitId, StorageUnit> units = new();
    private readonly ConcurrentBag<NpgsqlConnection> ownedConnections = [];
    private readonly PostgreSqlSchemaCoordinator schemaCoordinator;
    private bool disposed;

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
        atomicCommit: true);

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

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        PortabilityValidator.EnsurePhysicalIdentifiers(unit);
        PostgreSqlSchemaCoordinator.ValidateAccess(unit, access);
        schemaCoordinator.EnsureRuntimeAdmission(unit);
        var connection = OpenConnection();
        OwnConnection(connection);
        return new PostgreSqlStorageSession(this, Resolve(unit), access, connection, null);
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units)
        => BeginUnitOfWork(access, BatchWriteOptions.Default, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
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
        foreach (var unit in units)
        {
            ArgumentNullException.ThrowIfNull(unit);
            PortabilityValidator.EnsurePhysicalIdentifiers(unit);
            PostgreSqlSchemaCoordinator.ValidateAccess(unit, access);
            schemaCoordinator.EnsureRuntimeAdmission(unit);
        }

        var connection = OpenConnection();
        try
        {
            var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            OwnConnection(connection);
            return new PostgreSqlUnitOfWork(this, connection, transaction, units, access, options);
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

    internal void OwnConnection(NpgsqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (disposed)
        {
            connection.Dispose();
            throw new ObjectDisposedException(nameof(PostgreSqlProviderConnection));
        }
        ownedConnections.Add(connection);
    }

    internal void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(PostgreSqlProviderConnection));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        while (ownedConnections.TryTake(out var connection))
            connection.Dispose();
    }
}

internal sealed class PostgreSqlSchemaCoordinator : ISchemaCoordinator
{
    internal const string ScopeColumn = "__groundwork_scope";
    internal const string VersionColumn = "__groundwork_version";
    private readonly PostgreSqlProviderConnection owner;
    private readonly RelationalSchemaExecutor executor;
    private readonly PostgreSqlDialect dialect = new();
    private readonly ConcurrentDictionary<StorageUnitId, StorageUnit> units = new();

    internal PostgreSqlSchemaCoordinator(PostgreSqlProviderConnection owner)
    {
        this.owner = owner;
        executor = new RelationalSchemaExecutor(owner.OpenConnection, dialect);
    }

    internal StorageUnit? Find(StorageUnitId id) => units.TryGetValue(id, out var unit) ? unit : null;

    internal void EnsureRuntimeAdmission(StorageUnit desired)
    {
        var physical = Physicalize(desired);
        if (physical.DerivedColumns.Count == 0)
            return;
        var target = Target(physical);
        var inspection = executor.InspectHistory(target);
        var applied = inspection.History.AppliedState;
        if (applied is null)
            return;
        if (!string.Equals(applied.TargetFingerprint, target.Fingerprint, StringComparison.Ordinal) ||
            !inspection.IsAppliedSchemaValid || inspection.HasColumnDrift)
        {
            throw new InvalidOperationException(
                $"Storage unit '{desired.Name}' has folded search-key schema drift. Apply the exact schema and rebuild the derived search-key column before opening a session." +
                (inspection.ColumnDrift.Length == 0 ? string.Empty : " " + string.Join(" ", inspection.ColumnDrift.Select(refusal => refusal.Message))));
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
        return new SchemaDiff(MapChanges(plan.Operations));
    }

    public SchemaApplyResult Apply(StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var physical = Physicalize(desired);
        Remember(desired, physical);
        var target = Target(physical);
        var result = PhysicalSchemaApplication.Apply(target, executor);
        owner.Remember(desired);
        return new SchemaApplyResult(new SchemaDiff(MapChanges(result.Plan.Operations)),
            result.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.NoChanges);
    }

    internal static PhysicalSchemaTarget Target(StorageUnit physical) =>
        new(
            new SchemaSubject(physical),
            new ProviderIdentity("PostgreSQL", "1.0"),
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
        PortabilityValidator.EnsurePhysicalIdentifiers(source);
        EnsurePhysicalIndexNames(source);
        ProviderOwnedColumns.ValidateLogicalDeclaration(source);
        ConcurrencyDeclaration.ValidateDeclaration(source);
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
        source = SearchKeyProjection.Expand(source);
        var columns = source.Columns.ToList();
        var key = source.Key.Columns.ToList();
        var indexes = source.Indexes.ToList();
        if (columns.Any(column => column.Name is ScopeColumn or VersionColumn))
            throw new ArgumentException($"'{ScopeColumn}' and '{VersionColumn}' are reserved PostgreSQL columns.", nameof(source));
        if (source.Scope == ScopePolicy.Scoped)
        {
            columns.Add(new ColumnDefinition { Name = ScopeColumn, Type = PortableType.String, IsNullable = false, Default = new PortableDefault(string.Empty) });
            key.Insert(0, ScopeColumn);
            indexes = indexes.Select(index => new IndexDefinition
            {
                Name = index.Name,
                Columns = [new IndexColumn(ScopeColumn), .. index.Columns],
                IsUnique = index.IsUnique,
                MissingValues = index.MissingValues,
                SchemaVersion = index.SchemaVersion
            }).ToList();
        }
        if (source.Concurrency.IsOptimistic)
        {
            RemoveDeclaredToken(source, columns);
            columns.Add(new ColumnDefinition { Name = VersionColumn, Type = PortableType.Int64, IsNullable = false, Default = new PortableDefault(0L) });
        }
        return new StorageUnit
        {
            Id = source.Id,
            Name = source.Name,
            Columns = columns,
            Key = new KeyDefinition { Columns = key },
            DerivedColumns = source.DerivedColumns,
            Indexes = indexes,
            AggregationProfiles = source.AggregationProfiles.Select(AggregationProfileSnapshot.Capture).ToArray(),
            Scope = source.Scope,
            AppendIdempotency = source.AppendIdempotency,
            RetentionIdempotency = source.RetentionIdempotency,
            Concurrency = source.Concurrency,
            Timestamps = source.Timestamps,
            Retention = source.Retention,
            SchemaVersion = source.SchemaVersion
        };
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

    private static void RemoveDeclaredToken(StorageUnit source, List<ColumnDefinition> columns)
    {
        var token = source.Concurrency.TokenColumn!;
        var declared = columns.FirstOrDefault(column => column.Name == token);
        if (declared is null) return;
        if (declared.Type != PortableType.Int64 || declared.IsNullable ||
            declared.Default?.Value is not long defaultValue || defaultValue != 0)
        {
            throw new ArgumentException(
                $"Optimistic token column '{token}' must be a non-null Int64 with default 0.", nameof(source));
        }
        columns.Remove(declared);
    }

    private void Remember(StorageUnit original, StorageUnit physical) => units[original.Id] = physical;

    private static IReadOnlyList<SchemaChange> MapChanges(IEnumerable<PhysicalSchemaOperation> operations) =>
        operations.Where(operation => operation.Kind is not PhysicalSchemaOperationKind.ValidatePhysicalSchema and
                                      not PhysicalSchemaOperationKind.PublishAppliedState and
                                      not PhysicalSchemaOperationKind.BackfillColumn and
                                      not PhysicalSchemaOperationKind.FinalizeColumn)
            .Select(operation => new SchemaChange(
                operation.Kind switch
                {
                    PhysicalSchemaOperationKind.CreatePrimaryStorage => SchemaChangeKind.CreateStorageUnit,
                    PhysicalSchemaOperationKind.AddColumn => operation.SubjectIdentity.StartsWith("__groundwork_", StringComparison.Ordinal)
                        ? SchemaChangeKind.AddDerivedColumn : SchemaChangeKind.AddColumn,
                    PhysicalSchemaOperationKind.CreatePhysicalIndex or PhysicalSchemaOperationKind.RebuildPhysicalIndex => SchemaChangeKind.CreateIndex,
                    _ => SchemaChangeKind.AddDerivedColumn
                }, operation.SubjectIdentity))
            .ToArray();
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
            var metadata = dialect.ReadIndex(connection, null!, unit.Name, index.Name);
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
