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

    public SchemaDiff Diff(StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var physical = Prepare(desired);
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
        var physical = Prepare(desired);
        Remember(desired, physical);
        var target = Target(physical);
        try
        {
            var result = PhysicalSchemaApplication.Apply(target, executor);
            return new SchemaApplyResult(
                new SchemaDiff(MapChanges(result.Plan.Operations)),
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
        JsonSerializer.Serialize(physical.Columns.Select(column => new
        {
            column.Name,
            Type = (int)column.Type,
            column.MaxLength,
            column.Precision,
            column.Scale,
            Collation = column.Collation?.ToString()
        }));

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
        PortabilityValidator.EnsurePhysicalIdentifiers(source);
        EnsurePhysicalIndexNames(source);
        ProviderOwnedColumns.ValidateLogicalDeclaration(source);
        ConcurrencyDeclaration.ValidateDeclaration(source);
        source = SearchKeyProjection.Expand(source);
        var columns = source.Columns.Select(column => column with { }).ToList();
        var key = source.Key.Columns.ToList();
        var indexes = source.Indexes.ToList();
        if (columns.Any(column => column.Name is ScopeColumn or VersionColumn))
            throw new ArgumentException($"'{ScopeColumn}' and '{VersionColumn}' are reserved SQL Server columns.", nameof(source));

        if (source.Scope == ScopePolicy.Scoped)
        {
            columns.Add(new ColumnDefinition
            {
                Name = ScopeColumn,
                Type = PortableType.String,
                MaxLength = 128,
                IsNullable = false,
                Default = new PortableDefault(string.Empty)
            });
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
            columns.Add(new ColumnDefinition
            {
                Name = VersionColumn,
                Type = PortableType.Int64,
                IsNullable = false,
                Default = new PortableDefault(0L)
            });
        }

        return new StorageUnit
        {
            Id = source.Id,
            Name = SqlServerPhysicalName.Normalize(source.Name),
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

    private static StorageUnit Prepare(StorageUnit desired)
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
