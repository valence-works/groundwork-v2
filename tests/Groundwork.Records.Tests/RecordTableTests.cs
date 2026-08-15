using Groundwork.Kernel;
using Groundwork.Records;
using Groundwork.Records.TestingAdapter;
using Groundwork.Query.Model;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.Records.Tests;

public sealed class RecordTableTests
{
    [Fact]
    public void Mapping_compiles_accessors_once_and_round_trips_a_constructor_record()
    {
        var before = RecordTable<Customer>.AccessorCompilationCount;
        var table = CustomerTable();
        var afterBuild = RecordTable<Customer>.AccessorCompilationCount;
        var value = Customer.Create("Ada", "ada@example.test");

        var first = table.ToRowValues(value);
        var second = table.Map(value);
        var roundTrip = table.FromRowValues(first);

        Assert.True(afterBuild >= before);
        Assert.Equal(afterBuild, RecordTable<Customer>.AccessorCompilationCount);
        Assert.Equal(first.Values, second.Values);
        Assert.Equal(value, roundTrip);
        Assert.DoesNotContain("version", first.Values.Keys);
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
}
