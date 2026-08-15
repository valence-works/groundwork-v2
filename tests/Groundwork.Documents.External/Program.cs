using Groundwork.Documents;
using Groundwork.Kernel;
using Groundwork.Store;

var unit = DocumentUnit.For<ExternalOrder>("order", "external_orders")
    .Id(order => order.Id)
    .Project(order => order.CustomerId, column => column.MaxLength(64))
    .OptimisticConcurrency()
    .Build();

var row = unit.ToRowValues(new ExternalOrder(Guid.NewGuid(), "customer-1"));
var write = unit.Insert(new ExternalOrder(Guid.NewGuid(), "customer-2"), WriteOptions.CreateOnly);
if (unit.StorageUnit.Columns.Single(column => column.Name == "document").Type != PortableType.Json ||
    !Equals(row["customerId"], "customer-1") ||
    write.Mode != RowWriteMode.Insert ||
    !Equals(write.Values!.Values["customerId"], "customer-2") ||
    write.Options.Precondition.Kind != WritePreconditionKind.CreateOnly ||
    write.Values.Values.ContainsKey("version"))
    throw new InvalidOperationException("The external Documents package proof did not observe the public mapping.");

Console.WriteLine("Groundwork.Documents external package proof passed.");

public sealed record ExternalOrder(Guid Id, string CustomerId);
