using Groundwork.Kernel;
using Groundwork.Records;
using BuilderStorageUnit = Groundwork.Records.StorageUnit;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Kernel.Tests;

public sealed record BuilderCustomer(
    Guid Id,
    string Name,
    string Email,
    DateTimeOffset CreatedAt,
    bool IsActive,
    decimal Balance);

public static class BuilderCustomerSample
{
    public const int FluentDeclarationLineCount = 11;
    public const int TypedDeclarationLineCount = 8;

    public static readonly KernelStorageUnit Customer = BuilderStorageUnit
        .Declare("customer", "customers")
        .Guid("id", c => c.Required())
        .String("name", 200, c => c.Required())
        .String("email", 320, c => c.Required())
        .Timestamp("createdAt", c => c.Required())
        .Boolean("isActive", c => c.Required().Default(true))
        .Decimal("balance", 19, 4, c => c.Required())
        .Key("id")
        .UniqueIndex("by-email", "email")
        .Index("by-created", x => x.Descending("createdAt"))
        .Build();

    public static readonly RecordTable<BuilderCustomer> Customers = RecordTable.For<BuilderCustomer>("customers")
        .Key(x => x.Id)
        .Column(x => x.Name, c => c.MaxLength(200))
        .Column(x => x.Email, c => c.MaxLength(320))
        .Column(x => x.Balance, c => c.Precision(19, 4))
        .UniqueIndex("by-email", x => x.Email)
        .Index("by-created", x => x.CreatedAt, SortDirection.Descending)
        .Build();
}
