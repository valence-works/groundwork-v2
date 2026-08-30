using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Groundwork.Diagnostics;
using Groundwork.Substrate.Relational;

namespace Groundwork.SqlServer;

internal sealed class SqlServerSchemaCoordinator : ISchemaCoordinator
{
    internal const string ScopeColumn = "__groundwork_scope";
    internal const string VersionColumn = "__groundwork_version";
    internal const string BatchTypeKind = "table-valued-parameter";
    internal static readonly ProviderIdentity Identity = new("SQLServer", "1.0");
    internal static readonly ProviderOwnedColumnPolicy ColumnPolicy = new()
    {
        ProviderName = "SQL Server",
        ScopeMaxLength = 128
    };
    private readonly RelationalSchemaExecutor executor;
    private readonly SqlServerDialect dialect = new();
    private readonly ConcurrentDictionary<StorageUnitId, StorageUnit> units = new();
    private readonly RelationalRuntimeAdmission admission;

    internal SqlServerSchemaCoordinator(SqlServerProviderConnection owner)
    {
        executor = new RelationalSchemaExecutor(owner.CreateIndependentConnection, dialect);
        admission = new RelationalRuntimeAdmission(
            "sqlserver.schema-admission",
            desired => Target(Prepare(desired)),
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
        var physical = Prepare(desired);
        Remember(desired, physical);
        var target = Target(physical);
        var result = GroundworkRuntimeSchemaAdmission.InspectRuntimeAdmission(
            executor,
            target,
            options,
            inspected: executor.InspectDeployedHistory(target),
            inspectAfterApplication: () => executor.InspectDeployedHistory(target));
        if (result.AppliedOperationCount != 0)
            admission.Invalidate(desired.Id);
        return result;
    }

    public SchemaDiff Diff(StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var physical = Prepare(desired);
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
        var physical = Prepare(desired);
        Remember(desired, physical);
        var target = Target(physical);
        try
        {
            var result = PhysicalSchemaApplication.ApplyRecoverableWork(target, executor);
            return new SchemaApplyResult(
                new SchemaDiff(SchemaChangeMapping.Describe(result.Plan, physical)),
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
            [
                new ProviderPhysicalSchemaDefinition(
                    "SQLServer",
                    physical.Id,
                    BatchTypeKind,
                    BatchTypeName(physical),
                    BatchTypeCanonicalDefinition(physical)),
                .. physical.DerivedColumns.Select(derived => new ProviderPhysicalSchemaDefinition(
                    "SQLServer",
                    physical.Id,
                    RelationalDialect.SearchKeyDefinitionKind,
                    physical.Name + RelationalDialect.SearchKeyDefinitionSeparator + derived.Name,
                    derived.AlgorithmId ?? throw new InvalidOperationException($"Derived search-key column '{derived.Name}' is missing its algorithm identity.")))
            ]);

    internal static string BatchTypeName(StorageUnit physical)
    {
        ArgumentNullException.ThrowIfNull(physical);
        PortabilityValidator.EnsurePhysicalIdentifier(physical.Name, "name");
        var composed = $"__groundwork_batch_type_{physical.Name.Length}_{physical.Name}";
        var nativeName = SqlServerPhysicalName.Normalize(composed);
        PortabilityValidator.EnsurePhysicalIdentifier(
            nativeName,
            "sqlserver.batchType.name",
            maximumByteLength: 128,
            allowProviderOwnedPrefix: true);
        return nativeName;
    }

    private static string BatchTypeCanonicalDefinition(StorageUnit physical) =>
        PortableJsonSerializer.Serialize(physical.Columns.Select(column => (object?)new Dictionary<string, object?>
        {
            ["Name"] = column.Name,
            ["Type"] = (int)column.Type,
            ["MaxLength"] = column.MaxLength,
            ["Precision"] = column.Precision,
            ["Scale"] = column.Scale,
            ["Collation"] = column.Collation?.ToString()
        }).ToArray());

    internal static void ValidateAccess(StorageUnit unit, StorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
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
        return ProviderOwnedColumns.Physicalize(source, ColumnPolicy, SqlServerPhysicalName.Normalize);
    }

    internal static StorageUnit Prepare(StorageUnit desired)
    {
        var physical = Physicalize(desired);
        SqlServerIndexKeyBudgetValidator.Validate(physical);

        var portability = PortabilityValidator.Validate(desired, new PortabilityValidationContext(["sqlserver"]));
        if (!portability.IsPortable)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                portability.Refusals.Select(refusal =>
                    $"{refusal.Code} at {refusal.Path}: {refusal.Message}")));
        }

        if (physical.Id.Value.Length > 450)
            throw new InvalidOperationException("SQL Server storage-unit ids must contain at most 450 UTF-16 code units.");
        return physical;
    }

    private static void EnsurePhysicalIndexNames(StorageUnit source)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var index in source.Indexes)
        {
            var physicalName = SqlServerDialect.PhysicalIndexName(source.Name, index.Name);
            var path = $"indexes.{index.Name}.physicalName";
            PortabilityValidator.EnsurePhysicalIdentifier(
                physicalName,
                path,
                maximumByteLength: 128,
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

internal sealed class SqlServerProviderCatalog(SqlServerProviderConnection owner) : IProviderCatalog
{
    private readonly SqlServerDialect dialect = new();

    public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId)
    {
        owner.ThrowIfDisposed();
        var unit = ((SqlServerSchemaCoordinator)owner.Schema).Find(storageUnitId)
            ?? throw new InvalidOperationException($"Storage unit '{storageUnitId.Value}' has not been applied by this connection.");
        using (owner.EnterGate())
        {
            using var connection = owner.CreateIndependentConnection();
            return unit.Indexes
                .Select(index => (index, metadata: dialect.ReadIndex(connection, null, unit.Name, index.Name)))
                .Where(item => item.metadata is not null)
                .Select(item => new ProviderIndex(
                    item.index.Name,
                    item.metadata!.Columns
                        .Where(column => column.Name != SqlServerSchemaCoordinator.ScopeColumn)
                        .Select(column => new ProviderIndexColumn(column.Name, column.Direction))
                        .ToArray(),
                    item.metadata.IsUnique,
                    item.index.MissingValues,
                    item.index.SchemaVersion))
                .ToArray();
        }
    }
}
