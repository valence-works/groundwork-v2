using Groundwork.Documents;
using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Documents.Tests;

public sealed class PublicApiRedProofTests
{
    [Fact]
    public void A_document_unit_composes_a_plain_storage_unit_and_maps_a_write()
    {
        var unit = DocumentUnit.For<Order>("order", "orders")
            .Id(order => order.Id)
            .Project(order => order.CustomerId, column => column.MaxLength(64))
            .Project(order => order.Status, column => column.MaxLength(32))
            .Index("by_customer", order => order.CustomerId)
            .OptimisticConcurrency()
            .Build();

        Assert.Equal("orders", unit.StorageUnit.Name);
        Assert.Equal(PortableType.Json, unit.StorageUnit.Columns.Single(column => column.Name == "document").Type);
        Assert.Equal("c-1", unit.ToRowValues(new Order(Guid.NewGuid(), "c-1", "paid"))["customerId"]);
    }

    private sealed record Order(Guid Id, string CustomerId, string Status);
}
