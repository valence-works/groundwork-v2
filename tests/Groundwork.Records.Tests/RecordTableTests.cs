using Groundwork.Kernel;
using Groundwork.Records;
using Groundwork.Records.TestingAdapter;
using Groundwork.Query.Model;
using Groundwork.Testing;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Records.Tests;

public sealed class RecordTableTests
{
    [Fact]
    public void Mapping_compiles_accessors_once_and_round_trips_a_constructor_record()
    {
        var before = RecordTable<Customer>.AccessorCompilationCount;
        var reflectionBefore = RecordTable<Customer>.AccessorReflectionInspectionCount;
        var table = CustomerTable();
        var afterBuild = RecordTable<Customer>.AccessorCompilationCount;
        var value = Customer.Create("Ada", "ada@example.test");

        var first = table.ToRowValues(value);
        var second = table.Map(value);
        var roundTrip = table.FromRowValues(first);

        Assert.True(afterBuild >= before);
        Assert.Equal(afterBuild, RecordTable<Customer>.AccessorCompilationCount);
        Assert.Equal(reflectionBefore, RecordTable<Customer>.AccessorReflectionInspectionCount);
        Assert.Equal(first.Values, second.Values);
        Assert.Equal(value, roundTrip);
        Assert.DoesNotContain("version", first.Values.Keys);
    }

    [Fact]
    public void Materializer_populates_every_member_of_mutable_and_mixed_constructor_shapes()
    {
        var mutable = RecordTable.For<MutableCustomer>("mutable_customers")
            .Key(row => row.Id)
            .Build();
        var mixed = RecordTable.For<MixedCustomer>("mixed_customers")
            .Key(row => row.Id)
            .Build();
        var mutableStruct = RecordTable.For<MutableStructCustomer>("mutable_struct_customers")
            .Key(row => row.Id)
            .Build();
        var id = Guid.NewGuid();
        var values = new RowValues(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["name"] = "Ada",
            ["email"] = "ada@example.test"
        });

        var mutableValue = mutable.FromRowValues(values);
        var mixedValue = mixed.FromRowValues(values);
        var mutableStructValue = mutableStruct.FromRowValues(values);

        Assert.Equal((id, "Ada", "ada@example.test"), (mutableValue.Id, mutableValue.Name, mutableValue.Email));
        Assert.Equal((id, "Ada", "ada@example.test"), (mixedValue.Id, mixedValue.Name, mixedValue.Email));
        Assert.Equal((id, "Ada", "ada@example.test"), (mutableStructValue.Id, mutableStructValue.Name, mutableStructValue.Email));
    }

    [Fact]
    public void System_owned_version_member_is_excluded_from_queries_and_defaults_without_missing_column_errors()
    {
        var table = RecordTable.For<VersionedCustomer>("versioned_" + Guid.NewGuid().ToString("N"))
            .Key(row => row.Id)
            .OptimisticConcurrency()
            .Build();
        using var connection = new InMemoryProviderFactory().Create("memory://version-member-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(table.Definition).Applied);
        var records = table.Open(connection);
        var customer = new VersionedCustomer(Guid.NewGuid(), "Ada", 42);

        Assert.Equal(RecordWriteStatus.Inserted, records.Insert(customer).Status);
        var match = Assert.Single(records.Query(table.Query.Where(row => row.Name == "Ada")));

        Assert.Equal(customer.Id, match.Id);
        Assert.Equal("Ada", match.Name);
        Assert.Equal(0, match.Version);
        Assert.DoesNotContain(table.Query.ToQueryRequest().Projection.Columns, column => column.Name == "version");
    }

    [Fact]
    public void Build_refuses_optimistic_token_key_and_metadata_conflicts_at_the_declaration_boundary()
    {
        var keyConflict = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<VersionedCustomer>("bad_key")
                .Key(row => row.Version)
                .OptimisticConcurrency()
                .Build());
        var metadataConflict = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<InvalidVersionCustomer>("bad_metadata")
                .Key(row => row.Id)
                .OptimisticConcurrency()
                .Build());
        var defaultConflict = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<VersionedCustomer>("bad_default")
                .Key(row => row.Id)
                .Column(row => row.Version, column => column.Default(7L))
                .OptimisticConcurrency()
                .Build());

        Assert.Contains(keyConflict.Diagnostics, diagnostic => diagnostic.Code == "GW-DECL-CONCURRENCY-001" && diagnostic.Path == "concurrency");
        Assert.Contains(metadataConflict.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-DECL-CONCURRENCY-001" && diagnostic.Message.Contains("non-null Int64 with default 0", StringComparison.Ordinal));
        Assert.Contains(defaultConflict.Diagnostics, diagnostic => diagnostic.Code == "GW-DECL-CONCURRENCY-001");
    }

    [Fact]
    public void Build_refuses_an_index_over_the_system_owned_optimistic_token_in_either_builder_order()
    {
        var indexThenOptimistic = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<VersionedCustomer>("bad_version_index_first")
                .Key(row => row.Id)
                .Index("by-version", row => row.Version)
                .OptimisticConcurrency()
                .Build());
        var optimisticThenIndex = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<VersionedCustomer>("bad_optimistic_first")
                .Key(row => row.Id)
                .OptimisticConcurrency()
                .Index("by-version", row => row.Version)
                .Build());

        Assert.All(
            new[] { indexThenOptimistic, optimisticThenIndex },
            error => Assert.Contains(error.Diagnostics, diagnostic =>
                diagnostic.Code == "GW-DECL-CONCURRENCY-001" &&
                diagnostic.Path == "concurrency" &&
                diagnostic.Message.Contains("index 'by-version'", StringComparison.Ordinal)));
    }

    [Fact]
    public void Build_refuses_typed_regular_and_unique_indexes_over_json_before_the_index_is_used()
    {
        var regular = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<JsonCustomer>("bad_json_index")
                .Key(row => row.Id)
                .Index("by-payload", row => row.Payload)
                .Build());
        var unique = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<JsonCustomer>("bad_unique_json_index")
                .Key(row => row.Id)
                .UniqueIndex("by-payload", row => row.Payload)
                .Build());

        Assert.All(new[] { regular, unique }, error => Assert.Contains(
            error.Diagnostics,
            diagnostic => diagnostic.Code == "GW-DECL-INDEX-003" &&
                diagnostic.Path == "indexes.by-payload.columns[0]" &&
                diagnostic.Message.Contains("JSON", StringComparison.Ordinal)));
    }

    [Fact]
    public void Unindexed_json_members_remain_valid_and_mappable()
    {
        var table = RecordTable.For<JsonCustomer>("json_records")
            .Key(row => row.Id)
            .Build();
        var payload = new Dictionary<string, object?> { ["name"] = "Ada" };
        var value = new JsonCustomer(Guid.NewGuid(), payload);

        var mapped = table.ToRowValues(value);
        var materialized = table.FromRowValues(mapped);

        Assert.Equal(value.Id, materialized.Id);
        var materializedPayload = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(materialized.Payload);
        Assert.Equal("Ada", materializedPayload["name"]);
    }

    [Fact]
    public void Typed_partial_projections_materialize_anonymous_and_same_type_shapes_without_omitted_columns()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://projections-" + Guid.NewGuid().ToString("N"));
        var table = CustomerTable();
        Assert.True(connection.Schema.Apply(table.Definition).Applied);
        var records = table.Open(connection);
        var customer = Customer.Create("Ada", "ada@example.test");
        const string email = "ada@example.test";
        Assert.Equal(RecordWriteStatus.Inserted, records.Insert(customer).Status);

        var anonymous = records.Query(table.Select(
            table.Query.Where(row => row.Email == email),
            row => new { row.Id, row.Name }));
        var sameType = records.Query(table.Select(
            table.Query.Where(row => row.Email == email),
            row => new Customer(row.Id, row.Name, "intentionally omitted")));

        Assert.Equal((customer.Id, "Ada"), (Assert.Single(anonymous).Id, Assert.Single(anonymous).Name));
        Assert.Equal(new Customer(customer.Id, "Ada", "intentionally omitted"), Assert.Single(sameType));
    }

    [Fact]
    public void Selected_index_metadata_reaches_the_public_record_store_seam()
    {
        var table = CustomerTable();
        var store = new CapturingRecordStore();
        var records = table.Open(store);

        _ = records.Query(
            table.Query.Where(row => row.Email == "ada@example.test"),
            RecordQueryOptions.UsingIndex("by-email"));

        Assert.Equal("by-email", store.Options?.SelectedIndex);
        var index = Assert.Single(store.Options!.Indexes);
        Assert.Equal("by-email", index.Name);
        Assert.Equal<string>(["email"], index.Columns);
    }

    [Fact]
    public void Optimistic_concurrency_is_explicit_and_system_owned()
    {
        var table = CustomerTable();
        var definition = table.Definition;

        Assert.Equal(ConcurrencyKind.Optimistic, definition.Concurrency.Kind);
        Assert.Equal("version", definition.Concurrency.TokenColumn);
        Assert.Contains(definition.Columns, column => column.Name == "version");
        Assert.DoesNotContain("version", table.ToRowValues(Customer.Create("Ada", "ada@example.test")).Values.Keys);
    }

    [Fact]
    public void Version_preconditions_require_an_explicit_optimistic_declaration()
    {
        var table = RecordTestFixture.CustomerTable(optimistic: false);
        using var connection = new InMemoryProviderFactory().Create("memory://records-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(table.Definition).Applied);
        var records = table.Open(connection);

        var error = Assert.Throws<InvalidOperationException>(() =>
            records.Insert(Customer.Create("Ada", "ada@example.test"), RecordWriteOptions.IfVersion(1)));

        Assert.Equal("Storage unit '" + table.Definition.Name + "' does not declare version machinery.", error.Message);
    }

    [Fact]
    public void Typed_crud_and_query_use_the_shipped_public_connection_adapter()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://records-" + Guid.NewGuid().ToString("N"));
        var table = CustomerTable();
        Assert.True(connection.Schema.Apply(table.Definition).Applied);
        var records = table.Open(connection);
        var customer = Customer.Create("Ada", "ada@example.test");

        var inserted = records.Insert(customer);
        Assert.Equal(RecordWriteStatus.Inserted, inserted.Status);
        Assert.Equal(1, inserted.Version);

        var updated = customer with { Name = "Ada Lovelace" };
        var update = records.Update(updated, RecordWriteOptions.IfVersion(inserted.Version!.Value));
        Assert.Equal(RecordWriteStatus.Updated, update.Status);
        Assert.Equal(2, update.Version);

        var stale = records.Update(customer with { Name = "stale" }, RecordWriteOptions.IfVersion(inserted.Version.Value));
        Assert.Equal(RecordWriteStatus.ConcurrencyConflict, stale.Status);

        var query = table.Query
            .Where(row => row.Email == "ada@example.test")
            .OrderBy(row => row.Name);
        var result = records.Query(query);
        var match = Assert.Single(result);
        Assert.Equal("Ada Lovelace", match.Name);
    }

    [Fact]
    public void Records_has_no_provider_assembly_reference()
    {
        var references = typeof(RecordTable<>).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("Groundwork.", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.All(references, name => Assert.True(
            name == "Groundwork.Kernel" || name == "Groundwork.Query.Linq" || name == "Groundwork.Query.Model",
            $"Groundwork.Records references forbidden assembly '{name}'."));
    }

    private static RecordTable<Customer> CustomerTable() => RecordTestFixture.CustomerTable();

    public sealed class MutableCustomer
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "unset";
        public string Email { get; set; } = "unset";
    }

    public sealed class MixedCustomer(Guid id)
    {
        public Guid Id { get; } = id;
        public string Name { get; set; } = "unset";
        public string Email { get; set; } = "unset";
    }

    public struct MutableStructCustomer
    {
        public MutableStructCustomer()
        {
            Id = default;
            Name = string.Empty;
            Email = string.Empty;
        }

        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
    }

    public sealed record VersionedCustomer(Guid Id, string Name, long Version);
    public sealed record InvalidVersionCustomer(Guid Id, string Name, string Version);
    public sealed record JsonCustomer(Guid Id, object Payload);

    private sealed class CapturingRecordStore : IRecordStore
    {
        public QueryRenderOptions? Options { get; private set; }
        public RecordWriteResult Insert(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) => throw new NotSupportedException();
        public RecordWriteResult Update(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) => throw new NotSupportedException();
        public RecordWriteResult Upsert(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) => throw new NotSupportedException();
        public RecordWriteResult Delete(Groundwork.Kernel.StorageUnit unit, RowValues key, RecordWriteOptions? options = null) => throw new NotSupportedException();
        public RecordQueryResult Query(QueryRequest request, QueryRenderOptions? options = null)
        {
            Options = options;
            return new RecordQueryResult([]);
        }
    }
}
