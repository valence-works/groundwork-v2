using Groundwork.Documents;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Query.Linq;
using Groundwork.Query.Planning;
using Groundwork.Records;
using Groundwork.Store;
using Groundwork.Sqlite;
using Groundwork.Testing;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

using Groundwork.PublicApi.Consumer;

PublicApiApprovalFixture.Touch();
PublicApiApprovalFixture.CompileCallableSurface();

using (var externalProvider = new InMemoryProviderFactory().Create("external-provider-author-proof"))
{
    Require(externalProvider.Capabilities.Any(capability => capability.Id == BatchWriteCapabilities.ExactAppendOutcomes), "Groundwork.Testing did not expose the reference provider capability contract.");
}

var databasePath = Path.Combine(Path.GetTempPath(), "groundwork-public-api-" + Guid.NewGuid().ToString("N") + ".db");
try
{
    using var connection = new SqliteProviderFactory().Create("Data Source=" + databasePath);
    RunRecordsJourney(connection);
    RunExactAppendJourney(connection);
    RunDocumentsJourney(connection);
    RunFailureJourneys(connection);
    Console.WriteLine("Groundwork public API clean-room journey passed.");
}
finally
{
    if (File.Exists(databasePath))
        File.Delete(databasePath);
}

static void RunExactAppendJourney(IStorageProviderConnection connection)
{
    var unit = new KernelStorageUnit
    {
        Id = new StorageUnitId("append_records"),
        Name = "append_records",
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "payload", Type = PortableType.String, IsNullable = false, MaxLength = 200 }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) }
    };
    Require(connection.Schema.Apply(unit).Applied, "The exact append schema did not apply.");

    var session = connection.OpenSession(unit, StorageAccess.Global);
    var operation = new OperationId(DateTimeOffset.UtcNow, "public-exact-append");
    var values = new StorageValues(new Dictionary<string, object?> { ["payload"] = "package-only" });
    var committed = session.AppendWithOutcomes(operation, values);
    Require(committed.Status == WriteOutcomeStatus.Inserted && committed.Outcomes.Count == 1, "The package-only exact append did not return one inserted outcome.");
    Require(committed.Outcomes[0].GeneratedValue<long>("sequence") == 1, "The package-only exact append did not return the generated sequence.");

    var replayed = session.AppendWithOutcomes(operation, values);
    Require(replayed.Status == WriteOutcomeStatus.Replayed, "The package-only exact append did not replay.");
    Require(replayed.Outcomes[0].GeneratedValue<long>("sequence") == 1, "The package-only replay did not preserve its generated sequence.");
}

static void RunRecordsJourney(IStorageProviderConnection connection)
{
    var table = RecordTable.For<Customer>("customers")
        .Key(customer => customer.Id)
        .OptimisticConcurrency()
        .Column(customer => customer.Email, column => column.MaxLength(320).Required())
        .Column(customer => customer.Name, column => column.MaxLength(200).Required())
        .Index("by-email", customer => customer.Email)
        .Build();

    var applied = connection.Schema.Apply(table.Definition);
    Require(applied.Applied, "The initial public schema application did not apply.");
    Require(connection.Schema.Diff(table.Definition).IsEmpty, "The applied public schema was not a no-op on verification.");

    var records = table.Open(connection);
    var first = new Customer(Guid.NewGuid(), "ada@example.test", "Ada");
    var inserted = records.Insert(first);
    Require(inserted.Status == RecordWriteStatus.Inserted && inserted.Version == 1, "The typed insert did not return version 1.");

    var changed = first with { Name = "Ada Lovelace" };
    var updated = records.Update(changed, RecordWriteOptions.IfVersion(inserted.Version!.Value));
    Require(updated.Status == RecordWriteStatus.Updated && updated.Version == 2, "The exact conditional update did not advance version 1 to version 2.");

    var upserted = records.Upsert(changed with { Name = "Ada Byron" }, RecordWriteOptions.IfVersion(updated.Version!.Value));
    Require(upserted.Status is RecordWriteStatus.Updated or RecordWriteStatus.Upserted && upserted.Version == 3, "The exact conditional upsert did not advance version 2 to version 3.");

    var conflict = records.Upsert(changed with { Name = "stale" }, RecordWriteOptions.IfVersion(inserted.Version.Value));
    Require(conflict.Status == RecordWriteStatus.ConcurrencyConflict, "The stale conditional upsert did not report a concurrency conflict.");

    using (var batch = table.BeginUnitOfWork(connection, BatchWriteOptions.Exact))
    {
        batch.Upsert(new Customer(Guid.NewGuid(), "grace@example.test", "Grace"));
        batch.Upsert(new Customer(Guid.NewGuid(), "mary@example.test", "Mary"));
        var report = batch.CommitWithOutcomes();
        Require(report.Summary.IsSuccessful && report.Outcomes.Count == 2, "The exact typed batch did not return two successful outcomes.");
    }

    var query = table.Query.Where(customer => customer.Email == "ada@example.test");
    var matches = records.Query(query, RecordQueryOptions.UsingIndex("by-email"));
    Require(matches.Count == 1 && matches[0].Name == "Ada Byron", "The covered typed query did not return the updated customer.");

    var uncovered = new RuntimeCoverageGate(
        [new CoverageIndex("by-email", [new CoverageIndexColumn("email")])],
        []);
    try
    {
        uncovered.EnsureCovered(query.ToQueryRequest(), DateTimeOffset.UtcNow);
        throw new InvalidOperationException("The uncovered query was accepted without a deployed index.");
    }
    catch (QueryCoverageException exception)
    {
        Require(exception.Code == "GW-COVER-006", "The coverage refusal did not identify the missing index coverage code.");
        Require(exception.Message.Contains("index", StringComparison.OrdinalIgnoreCase), "The coverage refusal did not explain the corrective index action.");
    }
}

static void RunDocumentsJourney(IStorageProviderConnection connection)
{
    var unit = DocumentUnit.For<Note>("note", "notes")
        .Id(note => note.Id)
        .Project(note => note.CustomerId)
        .OptimisticConcurrency()
        .Build();
    Require(connection.Schema.Apply(unit.StorageUnit).Applied, "The Documents schema did not apply.");

    var note = new Note(Guid.NewGuid(), "ada@example.test", "Welcome");
    var write = unit.Insert(note, WriteOptions.CreateOnly);
    var outcome = unit.Execute(connection, write);
    Require(outcome.Status == WriteOutcomeStatus.Inserted, "The Documents insert did not use the ordinary Store write path.");

    var persisted = connection.OpenSession(unit.StorageUnit, StorageAccess.Global).Read(new StorageKey(
        new Dictionary<string, object?> { [unit.IdColumn] = note.Id }));
    Require(persisted is not null, "The inserted document could not be read through the public Store session.");
    var materialized = unit.Read(new RowValues(persisted!.Values.Values), persisted.Version);
    Require(materialized.Value == note && materialized.Version == outcome.Version, "The Documents read did not preserve the typed value and version.");
}

static void RunFailureJourneys(IStorageProviderConnection connection)
{
    try
    {
        _ = RecordTable.For<JsonRecord>("json_failure")
            .Key(row => row.Id)
            .Index("by-payload", row => row.Payload)
            .Build();
        throw new InvalidOperationException("The declaration accepted an index over JSON.");
    }
    catch (StorageDeclarationException exception)
    {
        Require(exception.Diagnostics.Any(diagnostic => diagnostic.Code == "GW-DECL-INDEX-003"), "The declaration diagnostic did not identify the JSON index rule.");
        Require(exception.Message.Contains("Leave the JSON column unindexed", StringComparison.Ordinal), "The declaration diagnostic did not explain the corrective action.");
    }

    var plain = RecordTable.For<PlainCustomer>("plain_customers").Key(row => row.Id).Build();
    var plainRecords = plain.Open(connection);
    try
    {
        _ = plainRecords.Insert(new PlainCustomer(Guid.NewGuid(), "plain@example.test"), RecordWriteOptions.IfVersion(1));
        throw new InvalidOperationException("A version precondition was accepted without optimistic concurrency.");
    }
    catch (InvalidOperationException exception)
    {
        Require(exception.Message.Contains("Declare .OptimisticConcurrency() before using RecordWriteOptions.IfVersion(...).", StringComparison.Ordinal), "The concurrency diagnostic did not explain the exact declaration action.");
    }

    var appliedShape = RecordTable.For<FoldedCustomer>("folded_customers")
        .Key(row => row.Id)
        .Column(row => row.Email, column => column.MaxLength(320).Required())
        .Build();
    Require(connection.Schema.Apply(appliedShape.Definition).Applied, "The schema-drift baseline did not apply.");

    var folded = RecordTable.For<FoldedCustomer>("folded_customers")
        .Key(row => row.Id)
        .Column(row => row.Email, column => column.MaxLength(320).Required().Collation(PortableCollation.OrdinalIgnoreCase))
        .Index("by-email", row => row.Email)
        .Build();
    try
    {
        _ = folded.Open(connection);
        throw new InvalidOperationException("Schema drift was admitted to a public session.");
    }
    catch (InvalidOperationException exception)
    {
        Require(exception.Message.Contains("rebuild", StringComparison.OrdinalIgnoreCase), "The schema-drift diagnostic did not explain that the derived search key must be rebuilt.");
    }
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

public sealed record Customer(Guid Id, string Email, string Name);
public sealed record Note(Guid Id, string CustomerId, string Body);
public sealed record PlainCustomer(Guid Id, string Email);
public sealed record FoldedCustomer(Guid Id, string Email);
public sealed record JsonRecord(Guid Id, object Payload);
