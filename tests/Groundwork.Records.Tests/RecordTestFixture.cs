using Groundwork.Records;

namespace Groundwork.Records.Tests;

public sealed record Customer(Guid Id, string Name, string Email)
{
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
            .UniqueIndex("by-email", customer => customer.Email);
        return (optimistic ? builder.OptimisticConcurrency() : builder).Build();
    }
}
