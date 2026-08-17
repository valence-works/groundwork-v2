using Groundwork.Query.Linq;
using Groundwork.Records;
using Groundwork.Sqlite;

namespace Groundwork.Samples.Quickstart;

public static class Program
{
    public static int Main()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"groundwork-quickstart-{Guid.NewGuid():N}.db");

        try
        {
            var customer = Run(databasePath);
            Console.WriteLine($"Found {customer.Name} <{customer.Email}>");
            return 0;
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }

    public static Customer Run(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var customers = RecordTable.For<Customer>("customers")
            .Key(customer => customer.Id)
            .Column(customer => customer.Email, column => column.Required().MaxLength(320))
            .Column(customer => customer.Name, column => column.Required().MaxLength(200))
            .UniqueIndex("by_email", customer => customer.Email)
            .Build();

        using var connection = new SqliteProviderFactory().Create($"Data Source={databasePath}");
        connection.Schema.Apply(customers.Definition);

        var store = customers.Open(connection);
        var ada = new Customer(Guid.NewGuid(), "ada@example.test", "Ada Lovelace");
        var outcome = store.Insert(ada);
        if (outcome.Status != RecordWriteStatus.Inserted)
            throw new InvalidOperationException($"Insert failed with status {outcome.Status}.");

        var email = ada.Email;
        return store.Query(
                customers.Query.Where(customer => customer.Email == email),
                RecordQueryOptions.UsingIndex("by_email"))
            .Single();
    }
}

public sealed record Customer(Guid Id, string Email, string Name);
