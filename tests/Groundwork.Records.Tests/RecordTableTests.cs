using Groundwork.Kernel;
using Groundwork.Records;
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
                .Index("by_version", row => row.Version)
                .OptimisticConcurrency()
                .Build());
        var optimisticThenIndex = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<VersionedCustomer>("bad_optimistic_first")
                .Key(row => row.Id)
                .OptimisticConcurrency()
                .Index("by_version", row => row.Version)
                .Build());

        Assert.All(
            new[] { indexThenOptimistic, optimisticThenIndex },
            error => Assert.Contains(error.Diagnostics, diagnostic =>
                diagnostic.Code == "GW-DECL-CONCURRENCY-001" &&
                diagnostic.Path == "concurrency" &&
                diagnostic.Message.Contains("index 'by_version'", StringComparison.Ordinal)));
    }

    [Fact]
    public void Build_refuses_typed_regular_and_unique_indexes_over_json_before_the_index_is_used()
    {
        var regular = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<JsonCustomer>("bad_json_index")
                .Key(row => row.Id)
                .Index("by_payload", row => row.Payload)
                .Build());
        var unique = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<JsonCustomer>("bad_unique_json_index")
                .Key(row => row.Id)
                .UniqueIndex("by_payload", row => row.Payload)
                .Build());

        Assert.All(new[] { regular, unique }, error => Assert.Contains(
            error.Diagnostics,
            diagnostic => diagnostic.Code == "GW-DECL-INDEX-003" &&
                diagnostic.Path == "indexes.by_payload.columns[0]" &&
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
            RecordQueryOptions.UsingIndex("by_email"));

        Assert.Equal("by_email", store.Options?.SelectedIndex);
        var index = Assert.Single(store.Options!.Indexes);
        Assert.Equal("by_email", index.Name);
        Assert.Equal<string>(["email"], index.Columns);
    }

    [Fact]
    public void Records_storage_wrapper_exposes_sparse_index_authoring()
    {
        var definition = Groundwork.Records.StorageUnit
            .Declare("sparse", "sparse")
            .String("id", 32, column => column.Required())
            .String("email", 320)
            .Key("id")
            .UniqueIndex("by_email", index => index
                .Column("email")
                .ExcludeMissingValues())
            .Build();

        Assert.Equal(MissingValueBehavior.Excluded, definition.Indexes.Single().MissingValues);
    }

    [Fact]
    public void Records_storage_wrapper_refuses_path_like_physical_columns()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() => Groundwork.Records.StorageUnit
            .Declare("invalid", "invalid")
            .String("state.status", 200)
            .Key("state.status")
            .Build());

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-PORT-010" &&
            diagnostic.Message.Contains("ASCII letters", StringComparison.Ordinal));
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

        Assert.Equal("Storage unit '" + table.Definition.Name + "' does not declare version machinery. Declare .OptimisticConcurrency() before using RecordWriteOptions.IfVersion(...).", error.Message);
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
    public void Count_and_any_are_answered_provider_side_over_the_public_adapter()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://records-" + Guid.NewGuid().ToString("N"));
        var table = CustomerTable();
        Assert.True(connection.Schema.Apply(table.Definition).Applied);
        var records = table.Open(connection);
        Assert.Equal(RecordWriteStatus.Inserted, records.Insert(Customer.Create("Ada", "ada@example.test")).Status);
        Assert.Equal(RecordWriteStatus.Inserted, records.Insert(Customer.Create("Ada", "ada.two@example.test")).Status);
        Assert.Equal(RecordWriteStatus.Inserted, records.Insert(Customer.Create("Grace", "grace@example.test")).Status);

        Assert.Equal(2, records.Count(table.Query.Where(row => row.Name == "Ada")));
        Assert.Equal(0, records.Count(table.Query.Where(row => row.Name == "Missing")));
        Assert.True(records.Any(table.Query.Where(row => row.Name == "Grace")));
        Assert.False(records.Any(table.Query.Where(row => row.Name == "Missing")));
    }

    [Fact]
    public async Task Declared_profile_binding_materializes_typed_group_and_reducer_results()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://record-aggregation-" + Guid.NewGuid().ToString("N"));
        var table = AggregationTable();
        Assert.True(connection.Schema.Apply(table.Definition).Applied);
        var records = table.Open(connection);
        Assert.Equal(RecordWriteStatus.Inserted, records.Insert(new AggregatedCustomer(Guid.NewGuid(), "Ada", 7)).Status);
        Assert.Equal(RecordWriteStatus.Inserted, records.Insert(new AggregatedCustomer(Guid.NewGuid(), "Ada", 11)).Status);
        Assert.Equal(RecordWriteStatus.Inserted, records.Insert(new AggregatedCustomer(Guid.NewGuid(), "Grace", 5)).Status);

        var binding = table.Aggregate(
            "by-name",
            row => row.Get<string>("name"),
            row => new AggregatedSummary(row.Get<long>("count"), row.Get<long>("total")));

        var result = records.Aggregate(binding);
        Assert.Equal(
            [
                new RecordAggregationResult<string, AggregatedSummary>("Ada", new AggregatedSummary(2, 18)),
                new RecordAggregationResult<string, AggregatedSummary>("Grace", new AggregatedSummary(1, 5))
            ],
            result);

        var asyncResult = await records.AggregateAsync(binding);
        Assert.Equal(result, asyncResult);
    }

    [Fact]
    public void Declared_profile_binding_refuses_unknown_profiles_and_alias_mismatches()
    {
        var table = AggregationTable();
        var unknown = Assert.Throws<AggregationValidationException>(() => table.Aggregate(
            "missing",
            row => row.Get<string>("name"),
            row => row.Get<long>("count")));
        Assert.Contains(unknown.Errors, error => error.Code == "GW-AGG-QUERY-004");

        var wrongGroupAlias = Assert.Throws<ArgumentException>(() => table.Aggregate(
            "by-name",
            row => row.Get<long>("count"),
            row => row.Get<long>("total")));
        Assert.Contains("not declared by this profile's group output", wrongGroupAlias.Message, StringComparison.Ordinal);

        var wrongResultAlias = Assert.Throws<ArgumentException>(() => table.Aggregate(
            "by-name",
            row => row.Get<string>("name"),
            row => row.Get<long>("name")));
        Assert.Contains("not declared by this profile's result output", wrongResultAlias.Message, StringComparison.Ordinal);

        var wrongResultType = Assert.Throws<ArgumentException>(() => table.Aggregate(
            "by-name",
            row => row.Get<string>("name"),
            row => row.Get<int>("count")));
        Assert.Contains("declared as 'System.Int64'", wrongResultType.Message, StringComparison.Ordinal);

        var closedProfile = RecordTable.For<AggregatedCustomer>("record_aggregation_closed_" + Guid.NewGuid().ToString("N"))
            .Key(row => row.Id)
            .Aggregate("by-name-and-amount", aggregation => aggregation
                .GroupBy("name", "amount")
                .Count("count"))
            .Build();
        var missingGroup = Assert.Throws<ArgumentException>(() => closedProfile.Aggregate(
            "by-name-and-amount",
            row => row.Get<string>("name"),
            row => row.Get<long>("count")));
        Assert.Contains("must bind every declared alias", missingGroup.Message, StringComparison.Ordinal);

        var constantResult = Assert.Throws<ArgumentException>(() => table.Aggregate(
            "by-name",
            row => row.Get<string>("name"),
            row => 0));
        Assert.Contains("must bind at least one declared alias", constantResult.Message, StringComparison.Ordinal);

        var subsetResult = table.Aggregate(
            "by-name",
            row => row.Get<string>("name"),
            row => new { Count = row.Get<long>("count") });
        Assert.NotNull(subsetResult);
    }

    [Fact]
    public void Declared_profile_binding_preserves_the_profile_and_supports_a_single_group_alias()
    {
        var table = AggregationTable();
        var binding = table.Aggregate<string, long>("by-name", "name", row => row.Get<long>("count"));

        Assert.Equal("by-name", binding.ProfileName);
        var profile = Assert.Single(table.Definition.AggregationProfiles);
        Assert.Equal(["name"], profile.GroupByColumns);
        Assert.Equal(["count", "total"], profile.Aggregates.Select(aggregate => aggregate.Alias));
        Assert.Equal(1_000, profile.MaxGroups);
        Assert.Equal(100_000, profile.MaxInputRows);
    }

    [Fact]
    public void Count_executes_a_total_count_request_with_a_single_row_page()
    {
        var table = CustomerTable();
        var store = new CapturingRecordStore(totalCount: 7);
        var records = table.Open(store);

        Assert.Equal(7, records.Count(table.Query.Where(row => row.Name == "Ada")));
        Assert.True(store.Request!.Result.IncludesTotalCount);
        Assert.Equal(1, store.Request.Paging.Limit);
    }

    [Fact]
    public void Count_refuses_a_store_result_without_a_provider_total_count()
    {
        var table = CustomerTable();
        var records = table.Open(new CapturingRecordStore());

        var error = Assert.Throws<InvalidOperationException>(() =>
            records.Count(table.Query.Where(row => row.Name == "Ada")));

        Assert.Contains("provider-side total count", error.Message, StringComparison.Ordinal);
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
    public sealed record AggregatedCustomer(Guid Id, string Name, int Amount);
    public sealed record AggregatedSummary(long Count, long Total);

    private static RecordTable<AggregatedCustomer> AggregationTable() => RecordTable.For<AggregatedCustomer>(
            "record_aggregation_" + Guid.NewGuid().ToString("N"))
        .Key(row => row.Id)
        .Aggregate("by-name", aggregation => aggregation
            .GroupBy("name")
            .Count("count")
            .Sum("total", "amount"))
        .Build();

    private sealed class CapturingRecordStore(long? totalCount = null) : IRecordStore
    {
        public QueryRequest? Request { get; private set; }
        public QueryRenderOptions? Options { get; private set; }
        public RecordWriteResult Insert(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) => throw new NotSupportedException();
        public RecordWriteResult Update(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) => throw new NotSupportedException();
        public RecordWriteResult Upsert(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) => throw new NotSupportedException();
        public RecordWriteResult Delete(Groundwork.Kernel.StorageUnit unit, RowValues key, RecordWriteOptions? options = null) => throw new NotSupportedException();
        public RecordQueryResult Query(QueryRequest request, QueryRenderOptions? options = null)
        {
            Request = request;
            Options = options;
            return new RecordQueryResult([], totalCount);
        }
    }
}
