using System.Collections.Concurrent;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Testing;
using Groundwork.Substrate.Relational;

namespace Groundwork.Sqlite;

internal sealed class SqliteSchemaCoordinator : ISchemaCoordinator
{
    internal const string ScopeColumn = "__groundwork_scope";
    internal const string VersionColumn = "__groundwork_version";
    internal const string ActionColumn = "__groundwork_action";
    private readonly SqliteProviderConnection owner;
    private readonly RelationalSchemaExecutor executor;
    private readonly SqliteDialect dialect = new();
    private readonly ConcurrentDictionary<StorageUnitId, StorageUnit> units = new();

    internal SqliteSchemaCoordinator(SqliteProviderConnection owner)
    {
        this.owner = owner;
        executor = new RelationalSchemaExecutor(owner.CreateIndependentConnection, dialect);
    }

    internal StorageUnit? Find(StorageUnitId id) => units.TryGetValue(id, out var unit) ? unit : null;

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
        owner.RefreshSchema();
        return new SchemaApplyResult(new SchemaDiff(MapChanges(result.Plan.Operations)),
            result.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.NoChanges);
    }

    internal static PhysicalSchemaTarget Target(StorageUnit physical) =>
        new(new SchemaSubject(physical), new ProviderIdentity("SQLite", "1.0"));

    internal static void ValidateAccess(StorageUnit unit, StorageAccess access)
    {
        if (unit.Scope != access.Policy)
            throw new InvalidOperationException($"Storage unit '{unit.Name}' requires {unit.Scope} access.");
        if (unit.Scope == ScopePolicy.Scoped && access.Scope is null)
            throw new InvalidOperationException($"Storage unit '{unit.Name}' requires a storage scope.");
        if (unit.Scope == ScopePolicy.Global && access.Scope is not null)
            throw new InvalidOperationException($"Storage unit '{unit.Name}' is global and cannot use a storage scope.");
    }

    internal static StorageUnit Physicalize(StorageUnit source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence))
        {
            var portability = PortabilityValidator.Validate(source);
            if (!portability.IsPortable)
                throw new InvalidOperationException(string.Join(
                    Environment.NewLine,
                    portability.Refusals.Select(refusal =>
                        $"{refusal.Code} at {refusal.Path}: {refusal.Message}")));
        }
        var columns = source.Columns.ToList();
        var key = source.Key.Columns.ToList();
        var indexes = source.Indexes.ToList();
        if (columns.Any(column => column.Name is ScopeColumn or VersionColumn or ActionColumn))
            throw new ArgumentException($"'{ScopeColumn}', '{VersionColumn}', and '{ActionColumn}' are reserved SQLite columns.", nameof(source));
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
        if (source.Concurrency == ConcurrencyDeclaration.Optimistic)
            columns.Add(new ColumnDefinition { Name = VersionColumn, Type = PortableType.Int64, IsNullable = false, Default = new PortableDefault(1L) });
        else
            columns.Add(new ColumnDefinition { Name = ActionColumn, Type = PortableType.String, MaxLength = 1, IsNullable = false, Default = new PortableDefault("I") });
        return new StorageUnit
        {
            Id = source.Id,
            Name = source.Name,
            Columns = columns,
            Key = new KeyDefinition { Columns = key },
            DerivedColumns = source.DerivedColumns,
            Indexes = indexes,
            Scope = source.Scope,
            Concurrency = source.Concurrency,
            Timestamps = source.Timestamps,
            SchemaVersion = source.SchemaVersion
        };
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

internal sealed class SqliteProviderCatalog : IProviderCatalog
{
    private readonly SqliteProviderConnection owner;
    private readonly SqliteDialect dialect = new();

    internal SqliteProviderCatalog(SqliteProviderConnection owner) => this.owner = owner;

    public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId)
    {
        owner.ThrowIfDisposed();
        var unit = ((SqliteSchemaCoordinator)owner.Schema).Find(storageUnitId)
            ?? throw new InvalidOperationException($"Storage unit '{storageUnitId.Value}' has not been applied by this connection.");
        lock (owner.Gate)
        {
            using var catalogConnection = owner.CreateIndependentConnection();
            var indexes = new List<ProviderIndex>();
            foreach (var index in unit.Indexes)
            {
                var metadata = dialect.ReadIndex(catalogConnection, null!, unit.Name, index.Name);
                if (metadata is null) continue;
                indexes.Add(new ProviderIndex(index.Name,
                    metadata.Columns.Where(column => column.Name != SqliteSchemaCoordinator.ScopeColumn)
                        .Select(column => new ProviderIndexColumn(column.Name, column.Direction)).ToArray(),
                    metadata.IsUnique, index.MissingValues, index.SchemaVersion));
            }
            return indexes;
        }
    }
}
