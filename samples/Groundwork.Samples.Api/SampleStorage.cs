using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Records;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Store;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Samples.Api;

/// <summary>One order. Ordinary CLR properties; the declaration below says what they mean.</summary>
public sealed class Order
{
    public string Id { get; set; } = string.Empty;

    public string Customer { get; set; } = string.Empty;

    public decimal Total { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    /// <summary>System-owned optimistic-concurrency token. Never assigned by the application.</summary>
    public long Version { get; set; }
}

/// <summary>
/// The whole declaration for this application, in one place, with no provider knowledge in it.
/// The same objects are handed to SQLite, PostgreSQL, SQL Server, and MongoDB unchanged.
/// </summary>
public static class SampleStorage
{
    /// <summary>A global typed table with optimistic concurrency and one declared index.</summary>
    public static RecordTable<Order> Orders { get; } = RecordTable.For<Order>("orders")
        .Key(order => order.Id)
        .Column(order => order.Id, column => column.MaxLength(64).Required())
        .Column(order => order.Customer, column => column.MaxLength(64).Required())
        .Column(order => order.Total, column => column.Precision(18, 4))
        .Index("by_customer", order => order.Customer)
        .OptimisticConcurrency()
        .Build();

    /// <summary>
    /// A tenant-scoped unit. Every ordinary session over it must name exactly one scope; there is no
    /// ambient tenant and no way to forget it.
    /// </summary>
    public static KernelStorageUnit Notes { get; } = KernelStorageUnit.Declare("notes", "notes")
        .String("id", 64, column => column.Required())
        .String("body", 500, column => column.Required())
        .DateTimeOffset("createdAt", column => column.Required())
        .Key("id")
        .Scoped()
        .Build();

    /// <summary>
    /// Switching providers is one line, because <see cref="IStorageProviderFactory"/> is the only
    /// seam a provider has to implement. This lives in the application, not in the dependency
    /// injection package — which is why referencing that package does not drag four drivers in.
    /// </summary>
    public static IStorageProviderFactory ProviderFactory(string alias) => alias switch
    {
        "sqlite" => new SqliteProviderFactory(),
        "postgresql" => new PostgreSqlProviderFactory(),
        "sqlserver" => new SqlServerProviderFactory(),
        "mongodb" => new MongoProviderFactory(),
        _ => throw new ArgumentOutOfRangeException(
            nameof(alias), alias, "Groundwork:Provider must be sqlite, postgresql, sqlserver, or mongodb.")
    };
}
