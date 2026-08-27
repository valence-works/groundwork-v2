using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;
using Groundwork.Testing;

namespace Groundwork.Testing.SelfTests;

/// <summary>
/// The in-memory provider does not implement set-based mutation, which makes it the honest witness
/// for the capability refusal: a caller who never checks the advertised capability is refused by
/// name rather than reaching a provider that cannot serve the request.
/// </summary>
public sealed class SetMutationCapabilityTests
{
    [Fact]
    public void A_provider_that_does_not_advertise_set_based_mutation_refuses_it_by_name()
    {
        var unit = Unit();
        using var connection = new InMemoryProviderFactory().Create("memory://set-mutation-capability");
        connection.Schema.Apply(unit);
        Assert.DoesNotContain(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.SetMutation);

        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "a",
            ["status"] = "open"
        }));

        var delete = Assert.Throws<NotSupportedException>(() => session.DeleteWhere(Status(unit, "open")));
        Assert.Contains("GW-SET-001", delete.Message, StringComparison.Ordinal);

        var update = Assert.Throws<NotSupportedException>(() => session.UpdateWhere(
            Status(unit, "open"),
            new Dictionary<string, object?> { ["status"] = "closed" }));
        Assert.Contains("GW-SET-001", update.Message, StringComparison.Ordinal);

        Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = "a" })));
    }

    private static Predicate Status(StorageUnit unit, string value)
    {
        var column = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, isNullable: false, maxLength: 32);
        return new Predicate.Equal(column, QueryConstant.Of(column, value));
    }

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("set_mutation_capability"),
        Name = "set_mutation_capability",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
            new() { Name = "status", Type = PortableType.String, IsNullable = false, MaxLength = 32 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = [new IndexDefinition { Name = "by_status", Columns = [new IndexColumn("status")] }]
    };
}
