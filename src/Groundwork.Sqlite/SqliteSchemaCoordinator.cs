using System.Collections.Concurrent;
using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Groundwork.Diagnostics;
using Groundwork.Substrate.Relational;

namespace Groundwork.Sqlite;

internal sealed class SqliteSchemaCoordinator : ISchemaCoordinator
{
    internal const string ScopeColumn = "__groundwork_scope";
    internal const string VersionColumn = "__groundwork_version";
    internal const string ActionColumn = "__groundwork_action";
    internal static readonly ProviderIdentity Identity = new("SQLite", "1.0");
    // An AUTOINCREMENT column must remain SQLite's sole physical primary key. Its values are
    // unit-wide, so the generated identity is already globally unique; scope remains an access
    // predicate rather than part of this key.
    internal static readonly ProviderOwnedColumnPolicy ColumnPolicy = new()
    {
        ProviderName = "SQLite",
        ScopeJoinsGeneratedKey = false,
        DeclaresAppendAction = true
    };
    private readonly SqliteProviderConnection owner;
    private readonly RelationalSchemaExecutor executor;
    private readonly SqliteDialect dialect = new();
    private readonly ConcurrentDictionary<StorageUnitId, StorageUnit> units = new();
    private readonly RelationalRuntimeAdmission admission;
    private readonly object applicationGate = new();

    internal SqliteSchemaCoordinator(SqliteProviderConnection owner)
    {
        this.owner = owner;
        executor = new RelationalSchemaExecutor(owner.CreateIndependentConnection, dialect);
        admission = new RelationalRuntimeAdmission(
            "sqlite.schema-admission",
            desired => Target(Physicalize(desired)),
            InspectDeployed);
    }

    internal StorageUnit? Find(StorageUnitId id) => units.TryGetValue(id, out var unit) ? unit : null;

    internal void EnsureRuntimeAdmission(
        StorageUnit desired,
        IProviderCommandObserver? observer = null,
        DbConnection? connection = null) =>
        admission.EnsureAdmitted(desired, observer, connection);

    private PhysicalSchemaInspectionResult InspectDeployed(PhysicalSchemaTarget target, DbConnection? connection)
    {
        if (owner.UsesSharedSessionConnection && connection is null)
            return executor.InspectDeployedHistory(target, owner.Connection);
        return connection is null
            ? executor.InspectDeployedHistory(target)
            : executor.InspectDeployedHistory(target, connection);
    }

    public GroundworkRuntimeSchemaAdmissionResult InspectRuntimeAdmission(
        StorageUnit desired,
        GroundworkRuntimeSchemaAdmissionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(desired);
        using var gateLease = owner.EnterGate();
        lock (applicationGate)
        {
            var physical = Physicalize(desired);
            Remember(desired, physical);
            var target = Target(physical);
            var result = GroundworkRuntimeSchemaAdmission.InspectRuntimeAdmission(
                executor,
                target,
                options,
                inspected: InspectDeployed(target, null),
                inspectAfterApplication: () => InspectDeployed(target, null));
            if (result.Application?.Outcome is PhysicalSchemaApplicationOutcome.Applied or
                PhysicalSchemaApplicationOutcome.NoChanges)
            {
                owner.PublishSchema(target);
                owner.RefreshSchema();
                admission.Invalidate(desired.Id);
            }
            return result;
        }
    }

    public SchemaDiff Diff(StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        using var gateLease = owner.EnterGate();
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
        using var gateLease = owner.EnterGate();
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
                owner.RefreshSchema();
                return new SchemaApplyResult(new SchemaDiff(SchemaChangeMapping.Describe(result.Plan, physical)),
                    result.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.NoChanges);
            }
            finally
            {
                admission.Invalidate(desired.Id);
            }
        }
    }

    internal static PhysicalSchemaTarget Target(StorageUnit physical) =>
        new(
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
        var physical = ProviderOwnedColumns.Physicalize(source, ColumnPolicy);
        // SQLite has no INCLUDE clause. Keep the declared lookup key as the prefix and lower
        // covering columns to trailing key columns so the planner can still use a covering index.
        return SearchKeyProjection.LowerIncludedColumnsToKey(physical);
    }

    private static void EnsurePhysicalIndexNames(StorageUnit source)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var index in source.Indexes)
        {
            var physicalName = SqliteDialect.PhysicalIndexName(source.Name, index.Name);
            var path = $"indexes.{index.Name}.physicalName";
            PortabilityValidator.EnsurePhysicalIdentifier(
                physicalName,
                path,
                maximumByteLength: 255,
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

internal sealed class SqliteProviderCatalog : IProviderCatalog
{
    private readonly SqliteProviderConnection owner;
    private readonly SqliteDialect dialect = new();

    internal SqliteProviderCatalog(SqliteProviderConnection owner) => this.owner = owner;

    public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId)
    {
        owner.ThrowIfDisposed();
        using var gateLease = owner.EnterGate();
        return ReadIndexesWhileHoldingGate(storageUnitId);
    }

    internal IReadOnlyList<ProviderIndex> ReadIndexesWhileHoldingGate(StorageUnitId storageUnitId)
    {
        owner.ThrowIfDisposed();
        var unit = ((SqliteSchemaCoordinator)owner.Schema).Find(storageUnitId)
            ?? throw new InvalidOperationException($"Storage unit '{storageUnitId.Value}' has not been applied by this connection.");
        return ReadIndexesWhileHoldingGate(unit);
    }

    internal IReadOnlyList<ProviderIndex> ReadIndexesWhileHoldingGate(StorageUnit unit)
    {
        owner.ThrowIfDisposed();
        // Catalog evidence must not come from a pooled native handle whose SQLite schema cache
        // predates the latest application. A fresh handle observes the committed schema cookie.
        using var catalogConnection = owner.CreateCatalogConnection();
        var indexes = new List<ProviderIndex>();
        foreach (var index in unit.Indexes)
        {
            var metadata = dialect.ReadIndex(catalogConnection, null, unit.Name, index.Name);
            if (metadata is null) continue;
            indexes.Add(new ProviderIndex(index.Name,
                metadata.Columns.Where(column => column.Name != SqliteSchemaCoordinator.ScopeColumn)
                    .Select(column => new ProviderIndexColumn(column.Name, column.Direction)).ToArray(),
                metadata.IsUnique, index.MissingValues, index.SchemaVersion));
        }
        return indexes.ToArray();
    }
}
