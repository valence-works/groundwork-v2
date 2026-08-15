using Groundwork.Documents;
using Groundwork.Kernel;

var unit = DocumentUnit.For<ExternalOrder>("order", "external_orders")
    .Id(order => order.Id)
    .Project(order => order.CustomerId, column => column.MaxLength(64))
    .OptimisticConcurrency()
    .Build();

var row = unit.ToRowValues(new ExternalOrder(Guid.NewGuid(), "customer-1"));
if (unit.StorageUnit.Columns.Single(column => column.Name == "document").Type != PortableType.Json ||
    !Equals(row["customerId"], "customer-1"))
    throw new InvalidOperationException("The external Documents package proof did not observe the public mapping.");

Console.WriteLine("Groundwork.Documents external package proof passed.");

public sealed record ExternalOrder(Guid Id, string CustomerId);
