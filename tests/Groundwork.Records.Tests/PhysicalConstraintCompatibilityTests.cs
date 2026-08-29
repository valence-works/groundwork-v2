using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Records.Tests;

public sealed class PhysicalConstraintCompatibilityTests
{
    [Fact]
    public void Records_builder_forwards_physical_references_and_checks_to_the_kernel()
    {
        var customer = Groundwork.Records.StorageUnit.Declare("customer", "customers")
            .Guid("id", column => column.Required())
            .Key("id")
            .Build();
        var order = Groundwork.Records.StorageUnit.Declare("order", "orders")
            .Guid("id", column => column.Required())
            .Guid("customer_id", column => column.Required())
            .Int32("quantity", column => column.Required())
            .Key("id")
            .Index("by_customer", "customer_id")
            .PhysicalReference("fk_orders_customer", customer, "customer_id")
            .Check("ck_orders_quantity", "quantity", CheckConstraintOperator.GreaterThan, 0)
            .Build();

        Assert.Equal(ReferenceEnforcement.Physical, Assert.Single(order.References).Enforcement);
        Assert.Equal("ck_orders_quantity", Assert.Single(order.CheckConstraints).Name);
    }
}
