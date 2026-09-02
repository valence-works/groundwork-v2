using Groundwork.Query.Linq;
using Groundwork.Records;
using Groundwork.Sqlite;

var table = RecordTable.For<Visit>("visits")
    .Key(visit => visit.Id)
    .Column(visit => visit.Customer, column => column.MaxLength(64).Required())
    .Index("by_customer", visit => visit.Customer)
    .Aggregate("by-customer", aggregation => aggregation
        .GroupBy("customer")
        .Count("count"))
    .Build();

using var connection = new SqliteProviderFactory().Create("Data Source=newcomer.db");
var applied = connection.Schema.Apply(table.Definition);
Require(applied.Applied, "The declared schema did not apply.");

var records = table.Open(connection);
foreach (var visit in new[]
{
    new Visit("visit-1", "ada", 10),
    new Visit("visit-2", "ada", 20),
    new Visit("visit-3", "grace", 30)
})
    Require(records.Insert(visit).Status == RecordWriteStatus.Inserted, "A declared row did not insert.");

var query = table.Query.Where(visit => visit.Customer == "ada");
var matches = records.Query(query, RecordQueryOptions.UsingIndex("by_customer"));
Require(matches.Count == 2, "The index-covered query did not return both matching rows.");

var summaries = records.Aggregate(table.Aggregate(
    "by-customer",
    row => row.Get<string>("customer"),
    row => row.Get<long>("count")));
Require(summaries.Count == 2 && summaries.Any(summary => summary.Group == "ada" && summary.Result == 2),
    "The declared aggregation did not return the expected group.");

Console.WriteLine("schema=applied");
Console.WriteLine("rows_inserted=3");
Console.WriteLine("covered_query=ada:2");
Console.WriteLine("declared_aggregation=ada:2");
Console.WriteLine("newcomer_sqlite=passed");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

public sealed record Visit(string Id, string Customer, int Amount);
