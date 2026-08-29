using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Differential.Tests;

public sealed class ConstraintCapabilityAdvertisementTests
{
    [Fact]
    public void Constraint_advertisement_is_deduplicated_and_immutable()
    {
        var input = new List<CapabilityDescriptor>();
        var advertised = SchemaCapabilityAdmission.AdvertiseEnforcedConstraints(input);

        input.Add(new CapabilityDescriptor(new CapabilityId("sample.late.capability"), "Late", "Late mutation."));
        var deduplicated = SchemaCapabilityAdmission.AdvertiseEnforcedConstraints(advertised);

        Assert.Single(advertised);
        Assert.Single(deduplicated,
            capability => capability.Id == WellKnownCapabilities.EnforcedConstraints);
        var view = Assert.IsAssignableFrom<IList<CapabilityDescriptor>>(advertised);
        Assert.Throws<NotSupportedException>(() => view.Add(advertised[0]));
    }

    [Fact]
    public void Relational_connections_advertise_enforced_constraints()
    {
        using var sqlite = new SqliteProviderFactory().Create("Data Source=:memory:");
        using var postgreSql = new PostgreSqlProviderFactory().Create(
            "Host=localhost;Database=groundwork;Username=groundwork;Password=groundwork");
        using var sqlServer = new SqlServerProviderFactory().Create(
            "Server=localhost;Database=groundwork;User Id=groundwork;Password=groundwork;TrustServerCertificate=True");

        AssertAdvertised(sqlite);
        AssertAdvertised(postgreSql);
        AssertAdvertised(sqlServer);
    }

    [Fact]
    public void Mongo_refuses_physical_enforcement_before_native_schema_or_session_io()
    {
        var native = new RefusingMongoConnection();
        using var connection = new MongoStoreConnection(native);
        var (_, source) = ConstraintUnits(ReferenceEnforcement.Physical);

        Assert.DoesNotContain(connection.Capabilities,
            capability => capability.Id == WellKnownCapabilities.EnforcedConstraints);
        var providerFitReads = native.ProviderFitReads;

        AssertRefused(() => connection.Schema.InspectRuntimeAdmission(source));
        AssertRefused(() => connection.Schema.Diff(source));
        AssertRefused(() => connection.Schema.Apply(source));
        AssertRefused(() => connection.OpenSession(source, StorageAccess.Global));
        AssertRefused(() => connection.OpenOwnedSession(source, StorageAccess.Global));
        AssertRefused(() => connection.BeginUnitOfWork(StorageAccess.Global, source));

        Assert.Equal(0, native.NativeCalls);
        Assert.Equal(0, native.SchemaCalls);
        Assert.Equal(providerFitReads, native.ProviderFitReads);
    }

    [Fact]
    public void Native_Mongo_surface_refuses_without_contacting_a_server()
    {
        using var connection = new MongoDbProviderFactory().Create(
            "mongodb://127.0.0.1:1/groundwork?serverSelectionTimeoutMS=1");
        var (_, source) = ConstraintUnits(ReferenceEnforcement.Physical);

        AssertRefused(() => connection.Schema.InspectRuntimeAdmission(source));
        AssertRefused(() => connection.Schema.Diff(source));
        AssertRefused(() => connection.Schema.Apply(source));
        AssertRefused(() => connection.InspectSchema(source, MongoStorageAccess.Global));
        AssertRefused(() => connection.OpenSession(source, MongoStorageAccess.Global));
        AssertRefused(() => connection.BeginUnitOfWork(MongoStorageAccess.Global, source));
    }

    [Fact]
    public void In_memory_refuses_physical_enforcement_and_names_the_logical_alternative()
    {
        using var connection = new Groundwork.Testing.InMemoryProviderFactory()
            .Create("memory://constraint-capability");
        var (_, source) = ConstraintUnits(ReferenceEnforcement.Physical);

        Assert.DoesNotContain(connection.Capabilities,
            capability => capability.Id == WellKnownCapabilities.EnforcedConstraints);

        AssertRefused(() => connection.Schema.Apply(source));
        AssertRefused(() => connection.Schema.Diff(source));
        AssertRefused(() => connection.Schema.InspectRuntimeAdmission(source));
        AssertRefused(() => connection.OpenSession(source, StorageAccess.Global));
        AssertRefused(() => connection.OpenOwnedSession(source, StorageAccess.Global));
        AssertRefused(() => connection.BeginUnitOfWork(StorageAccess.Global, source));
    }

    [Fact]
    public void Check_only_enforcement_requires_the_same_capability_but_logical_references_do_not()
    {
        using var connection = new Groundwork.Testing.InMemoryProviderFactory()
            .Create("memory://constraint-capability-shapes");
        var (target, logicalSource) = ConstraintUnits(ReferenceEnforcement.LogicalOnly);
        var checkedUnit = StorageUnit.Declare("checked-unit", "checked_unit")
            .Guid("id", column => column.Required())
            .Int32("quantity", column => column.Required())
            .Key("id")
            .Check("ck_quantity", "quantity", CheckConstraintOperator.GreaterThan, 0)
            .Build();

        Assert.True(connection.Schema.Apply(target).Applied);
        Assert.True(connection.Schema.Apply(logicalSource).Applied);
        AssertRefused(() => connection.Schema.Apply(checkedUnit));
    }

    private static void AssertAdvertised(IStorageProviderConnection connection) =>
        Assert.Contains(connection.Capabilities,
            capability => capability.Id == WellKnownCapabilities.EnforcedConstraints);

    private static void AssertRefused(Action action)
    {
        var exception = Assert.Throws<NotSupportedException>(action);
        Assert.Contains("GW-SCHEMA-014", exception.Message, StringComparison.Ordinal);
        Assert.Contains("groundwork.schema.enforced-constraints", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Reference", exception.Message, StringComparison.Ordinal);
        Assert.Contains("logical-only", exception.Message, StringComparison.Ordinal);
    }

    private static (StorageUnit Target, StorageUnit Source) ConstraintUnits(ReferenceEnforcement enforcement)
    {
        var target = StorageUnit.Declare("constraint-target", "constraint_target")
            .Guid("id", column => column.Required())
            .Key("id")
            .Build();
        var builder = StorageUnit.Declare("constraint-source", "constraint_source")
            .Guid("id", column => column.Required())
            .Guid("target_id", column => column.Required())
            .Key("id")
            .Index("by_target", "target_id");
        var source = enforcement == ReferenceEnforcement.Physical
            ? builder.PhysicalReference("fk_source_target", target, "target_id").Build()
            : builder.Reference("source_target", target, "target_id").Build();
        return (target, source);
    }

    private sealed class RefusingMongoConnection : IMongoProviderConnection
    {
        private readonly RefusingMongoSchema schema;

        public RefusingMongoConnection()
        {
            schema = new RefusingMongoSchema(this);
        }

        public int NativeCalls { get; private set; }
        public int SchemaCalls { get; private set; }
        public int ProviderFitReads { get; private set; }
        public IMongoProviderCatalog Catalog { get; } = new EmptyMongoCatalog();
        public IMongoSchemaCoordinator Schema => schema;
        public ProviderFit ProviderSequenceFit
        {
            get
            {
                ProviderFitReads++;
                return new ProviderFit.Unsupported([]);
            }
        }

        public MongoSchemaAdmissionReport InspectSchema(StorageUnit unit, MongoStorageAccess access) =>
            throw NativeCall();

        public IMongoStorageSession OpenSession(
            StorageUnit unit,
            MongoStorageAccess access,
            IProviderCommandObserver? observer = null) => throw NativeCall();

        public IMongoUnitOfWork BeginUnitOfWork(MongoStorageAccess access, params StorageUnit[] units) =>
            throw NativeCall();

        public IMongoUnitOfWork BeginUnitOfWork(
            MongoStorageAccess access,
            IProviderCommandObserver? observer,
            params StorageUnit[] units) => throw NativeCall();

        public void Dispose()
        {
        }

        private Exception NativeCall()
        {
            NativeCalls++;
            return new InvalidOperationException("Native provider I/O was reached.");
        }

        private sealed class RefusingMongoSchema(RefusingMongoConnection owner) : IMongoSchemaCoordinator
        {
            public GroundworkRuntimeSchemaAdmissionResult InspectRuntimeAdmission(
                StorageUnit desired,
                GroundworkRuntimeSchemaAdmissionOptions? options = null) => throw SchemaCall();

            public SchemaDiff Diff(StorageUnit desired) => throw SchemaCall();

            public SchemaApplyResult Apply(StorageUnit desired) => throw SchemaCall();

            private Exception SchemaCall()
            {
                owner.SchemaCalls++;
                return new InvalidOperationException("Native schema I/O was reached.");
            }
        }

        private sealed class EmptyMongoCatalog : IMongoProviderCatalog
        {
            public IReadOnlyList<MongoProviderIndex> ReadIndexes(StorageUnitId storageUnitId) => [];
        }
    }
}
