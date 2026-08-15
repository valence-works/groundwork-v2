using Groundwork.Kernel;
using Groundwork.Records;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class BuilderTests
{
    [Fact]
    public void Fluent_customer_declaration_builds_a_plain_storage_unit()
    {
        var customer = Groundwork.Records.StorageUnit
            .Declare("customer", "customers")
            .Guid("id", column => column.Required())
            .String("name", 200, column => column.Required())
            .String("email", 320, column => column.Required())
            .Timestamp("createdAt", column => column.Required())
            .Boolean("isActive", column => column.Required().Default(true))
            .Decimal("balance", 19, 4, column => column.Required())
            .Key("id")
            .UniqueIndex("by-email", "email")
            .Index("by-created", index => index.Descending("createdAt"))
            .Build();

        Assert.Equal("customer", customer.Id.Value);
        Assert.Equal("customers", customer.Name);
        Assert.Equal(["id"], customer.Key.Columns);
        Assert.Equal(6, customer.Columns.Count);
        Assert.Equal(2, customer.Indexes.Count);
        Assert.Equal(PortableType.Guid, customer.Columns[0].Type);
        Assert.False(customer.Columns[0].IsNullable);
        Assert.Equal(true, customer.Columns[4].Default!.Value);
        Assert.Equal(SortDirection.Descending, customer.Indexes[1].Columns.Single().Direction);
    }

    [Fact]
    public void Fluent_build_reports_all_portability_failures_together()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() =>
            Groundwork.Records.StorageUnit
                .Declare("invalid", "invalid")
                .String("name", column => column.Required())
                .Decimal("amount")
                .Key("name")
                .Index("by-name", "name")
                .Build());

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-PORT-002");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-PORT-003");
        Assert.Equal(2, exception.Diagnostics.Count);
    }

    [Fact]
    public void Typed_record_table_infers_columns_and_exposes_plain_definition()
    {
        var table = RecordTable.For<Customer>("customers")
            .Key(customer => customer.Id)
            .Column(customer => customer.Name, column => column.MaxLength(200))
            .Column(customer => customer.Email, column => column.MaxLength(320))
            .Column(customer => customer.Balance, column => column.Precision(19, 4))
            .UniqueIndex("by-email", customer => customer.Email)
            .Index("by-created", customer => customer.CreatedAt, SortDirection.Descending)
            .Build();

        KernelStorageUnit definition = table.Definition;
        Assert.Equal("customers", definition.Id.Value);
        Assert.Equal("customers", definition.Name);
        Assert.Equal(["id"], definition.Key.Columns);
        Assert.Equal(
            ["id", "name", "email", "createdAt", "isActive", "balance"],
            definition.Columns.Select(column => column.Name));
        Assert.Equal(PortableType.Guid, definition.Columns[0].Type);
        Assert.False(definition.Columns[0].IsNullable);
        Assert.Equal(PortableType.Decimal, definition.Columns[5].Type);
        Assert.Equal(19, definition.Columns[5].Precision);
        Assert.Equal(4, definition.Columns[5].Scale);
        Assert.DoesNotContain("RecordTable", definition.GetType().AssemblyQualifiedName, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_build_reports_portability_failures()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() =>
            RecordTable.For<InvalidCustomer>("invalid")
                .Key(customer => customer.Id)
                .Column(customer => customer.Name)
                .UniqueIndex("by-name", customer => customer.Name)
                .Build());

        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-PORT-003");
        Assert.Contains(exception.Diagnostics, diagnostic => diagnostic.Code == "GW-PORT-001");
    }

    [Fact]
    public void Builder_preserves_invalid_decimal_precision_for_K2_to_report()
    {
        var exception = Assert.Throws<StorageDeclarationException>(() =>
            Groundwork.Records.StorageUnit
                .Declare("invalid", "invalid")
                .Decimal("amount", 39, 0)
                .Key("amount")
                .Index("by-amount", "amount")
                .Build());

        Assert.Contains(exception.Diagnostics, diagnostic =>
            diagnostic.Code == "GW-PORT-004" && diagnostic.Message.Contains("precision 39", StringComparison.Ordinal));
    }

    [Fact]
    public void Built_definition_snapshots_are_not_backed_by_builder_mutation()
    {
        var builder = Groundwork.Records.StorageUnit
            .Declare("values", "values")
            .String("value", 10)
            .Key("value");

        var first = builder.Build();
        builder.String("other", 10);

        Assert.Single(first.Columns);
        Assert.Equal("value", first.Columns[0].Name);
    }

    [Fact]
    public void Committed_customer_sample_stays_within_the_issue_line_targets()
    {
        Assert.Equal("customers", BuilderCustomerSample.Customer.Name);
        Assert.Equal("customers", BuilderCustomerSample.Customers.Definition.Name);
        Assert.True(BuilderCustomerSample.FluentDeclarationLineCount <= 12);
        Assert.True(BuilderCustomerSample.TypedDeclarationLineCount <= 9);
    }

    public sealed record Customer(
        Guid Id,
        string Name,
        string Email,
        DateTimeOffset CreatedAt,
        bool IsActive,
        decimal Balance);

    public sealed record InvalidCustomer(Guid Id, string? Name);
}
