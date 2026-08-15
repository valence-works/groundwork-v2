using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.MongoDb.TestingAdapter;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.StreamCapabilities.Tests;

public sealed class ProviderSequenceProofTests
{
    [Fact]
    public void InMemory_provider_sequence_allocation_is_monotonic_and_returned_in_exact_outcomes()
    {
        using var connection = new InMemoryProviderFactory().Create("stream-sequence-inmemory");
        AssertSequence(connection, "inmemory");
    }

    [Fact]
    public void SQLite_provider_sequence_allocation_is_monotonic_and_returned_in_exact_outcomes()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-stream-sequence-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            AssertSequence(connection, "sqlite");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public void PostgreSQL_provider_sequence_allocation_is_monotonic_and_returned_in_exact_outcomes()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL sequence proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertSequence(connection, "pg");
    }

    [SkippableFact]
    public void SQLServer_provider_sequence_allocation_is_monotonic_and_returned_in_exact_outcomes()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server sequence proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertSequence(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_provider_sequence_requires_transaction_capability_and_returns_values()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB sequence proof.");
        using var connection = new MongoDbTestingFactory().Create(connectionString!);
        var unit = SequenceUnit("stream-sequence-mongodb-" + Guid.NewGuid().ToString("N"));
        try
        {
            connection.Schema.Apply(unit);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("transaction", StringComparison.OrdinalIgnoreCase))
        {
            // A standalone MongoDB deployment is an honest capability refusal, before any row write.
            Assert.DoesNotContain(connection.Capabilities,
                descriptor => descriptor.Id == BatchWriteCapabilities.ProviderSequence);
            return;
        }

        Assert.Contains(connection.Capabilities,
            descriptor => descriptor.Id == BatchWriteCapabilities.ProviderSequence);
        AssertSequenceWrites(connection, unit);
        AssertSequenceOnlyInsert(connection, "mongodb");
    }

    [Fact]
    public void Provider_sequence_descriptor_is_stable_and_documents_its_cost()
    {
        Assert.Equal("groundwork.column.provider-sequence", BatchWriteCapabilities.ProviderSequence.Value);
        Assert.Equal(0, BatchWriteCapabilities.ProviderSequenceDescriptor.AdditionalProviderCommandsPerWrite);
        Assert.Equal(1, MongoCapabilities.ProviderSequenceDescriptor.AdditionalProviderCommandsPerWrite);
        Assert.Contains("commit order may differ", BatchWriteCapabilities.ProviderSequenceDescriptor.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("commit order may differ", MongoCapabilities.ProviderSequenceDescriptor.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("counter", MongoCapabilities.ProviderSequenceDescriptor.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSequence(IStorageProviderConnection connection, string provider)
    {
        var unit = SequenceUnit("stream-sequence-" + provider + "-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        AssertSequenceWrites(connection, unit);
        AssertSequenceOnlyInsert(connection, provider);
    }

    private static void AssertSequenceOnlyInsert(IStorageProviderConnection connection, string provider)
    {
        var name = "stream-sequence-only-" + provider + "-" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence }
            ],
            Key = new KeyDefinition { Columns = ["sequence"] }
        };
        connection.Schema.Apply(unit);

        var outcome = connection.OpenSession(unit, StorageAccess.Global)
            .Insert(new StorageValues(new Dictionary<string, object?>()));

        Assert.Equal(1L, outcome.GeneratedValue<long>("sequence"));
    }

    private static void AssertSequenceWrites(IStorageProviderConnection connection, StorageUnit unit)
    {
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var first = session.Insert(new StorageValues(new Dictionary<string, object?> { ["payload"] = "first" }));
        var second = session.Upsert(new StorageValues(new Dictionary<string, object?> { ["payload"] = "second" }));
        Assert.Equal(WriteOutcomeStatus.Inserted, first.Status);
        Assert.Equal(WriteOutcomeStatus.Upserted, second.Status);
        var firstValue = first.GeneratedValue<long>("sequence");
        var secondValue = second.GeneratedValue<long>("sequence");
        Assert.True(firstValue < secondValue);

        var updated = session.Update(new StorageValues(new Dictionary<string, object?>
        {
            ["sequence"] = firstValue,
            ["payload"] = "updated"
        }));
        var locatedUpsert = session.Upsert(new StorageValues(new Dictionary<string, object?>
        {
            ["sequence"] = firstValue,
            ["payload"] = "upserted-existing"
        }));
        var missingUpsert = session.Upsert(new StorageValues(new Dictionary<string, object?>
        {
            ["sequence"] = 99_999L,
            ["payload"] = "must-not-insert"
        }));
        Assert.Equal(WriteOutcomeStatus.Updated, updated.Status);
        Assert.Equal(WriteOutcomeStatus.Updated, locatedUpsert.Status);
        Assert.Empty(updated.GeneratedValues);
        Assert.Empty(locatedUpsert.GeneratedValues);
        Assert.Equal(WriteOutcomeStatus.NotFound, missingUpsert.Status);
        Assert.Equal("upserted-existing", session.Read(new StorageKey(
            new Dictionary<string, object?> { ["sequence"] = firstValue }))!.Values.Values["payload"]);
        Assert.Throws<ArgumentException>(() => session.Insert(new StorageValues(
            new Dictionary<string, object?> { ["sequence"] = 99_999L, ["payload"] = "assigned" })));

        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?> { ["payload"] = "third" })));
        work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?> { ["payload"] = "fourth" })));
        var report = work.CommitWithOutcomes();
        var generated = report.Outcomes.Select(outcome => outcome.Outcome.GeneratedValue<long>("sequence")).ToArray();
        Assert.Equal(2, report.Summary.Succeeded);
        Assert.Equal(2, generated.Length);
        Assert.True(generated[0] < generated[1]);
        Assert.True(secondValue < generated[0]);

        var concurrent = Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(() =>
        {
            var concurrentSession = connection.OpenSession(unit, StorageAccess.Global);
            return concurrentSession.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["payload"] = $"concurrent-{index}"
            })).GeneratedValue<long>("sequence");
        })));
        var concurrentValues = concurrent.GetAwaiter().GetResult();
        Assert.Equal(concurrentValues.Length, concurrentValues.Distinct().Count());
        Assert.All(concurrentValues, value => Assert.True(value > generated[^1]));
    }

    private static StorageUnit SequenceUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "payload", Type = PortableType.String }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] }
    };
}
