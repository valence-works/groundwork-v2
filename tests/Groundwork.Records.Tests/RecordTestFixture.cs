using Groundwork.Records;
using Groundwork.Schema;

namespace Groundwork.Records.Tests;

[GwTable("generated_customers")]
public sealed record Customer
{
    public Customer(Guid id, string name, string email) => (Id, Name, Email) = (id, name, email);

    [GwKey, GwColumn(Name = "id", Required = true)] public Guid Id { get; init; }
    [GwColumn(Name = "name", Length = 200, Required = true)] public string Name { get; init; }
    [GwColumn(Name = "email", Length = 320, Required = true)] public string Email { get; init; }

    public static Customer Create(string name, string email) =>
        new(Guid.NewGuid(), name, email);
}

internal static class RecordTestFixture
{
    public static RecordTable<Customer> CustomerTable(string? name = null, bool optimistic = true) =>
        BuildCustomerTable(name ?? ("records_customers_" + Guid.NewGuid().ToString("N")), optimistic);

    private static RecordTable<Customer> BuildCustomerTable(string name, bool optimistic)
    {
        var builder = RecordTable.For<Customer>(name)
            .Key(customer => customer.Id)
            .Column(customer => customer.Name, column => column.MaxLength(200).Required())
            .Column(customer => customer.Email, column => column.MaxLength(320).Required())
            .UniqueIndex("by_email", customer => customer.Email);
        return (optimistic ? builder.OptimisticConcurrency() : builder).Build();
    }
}
