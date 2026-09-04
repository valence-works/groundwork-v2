using Groundwork.LiveDatabases;
using Groundwork.Kernel;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Store;
using Xunit;

namespace Groundwork.StreamCapabilities.Tests;

public sealed class GeneratedSequenceAppendBatchTests
{
    private const int RowCount = 1_000;

    [Fact]
    public void SQLite_exact_generated_sequence_append_is_set_based_and_correlated()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-generated-sequence-batch-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            AssertBatchAppend(connection, "sqlite");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public void PostgreSQL_exact_generated_sequence_append_is_set_based_and_correlated()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL generated-sequence batch proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertBatchAppend(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_exact_generated_sequence_append_is_set_based_and_correlated()
    {
        var connectionString = LiveSqlServer.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server generated-sequence batch proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertBatchAppend(connection, "sqlserver");
    }

    private static void AssertBatchAppend(IStorageProviderConnection connection, string provider)
    {
        var unit = GeneratedUnit($"gsb_{provider}_{Guid.NewGuid():N}"[..30]);
        Assert.True(connection.Schema.Apply(unit).Applied);

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        var exact = Assert.IsAssignableFrom<IExactAppendStorageSession>(session);
        var values = Enumerable.Range(0, RowCount)
            .Select(index => Values($"payload-{index}", DateTimeOffset.UnixEpoch.AddSeconds(index)))
            .ToArray();
        var operation = new OperationId(DateTimeOffset.UtcNow, "generated-sequence-batch");

        var beforeAppend = observer.Commands.Count;
        var committed = exact.AppendWithOutcomes(operation, values);
        var appendCommands = observer.Commands.Skip(beforeAppend).ToArray();
        var generatedCommands = appendCommands
            .Where(command => command.Operation.Contains("generated-sequence-", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(WriteOutcomeStatus.Inserted, committed.Status);
        Assert.Equal(RowCount, committed.Outcomes.Count);
        var generated = committed.Outcomes.Select(outcome => outcome.GeneratedValue<long>("sequence")).ToArray();
        Assert.Equal(RowCount, generated.Distinct().Count());
        Assert.All(generated, sequence => Assert.True(sequence > 0));
        foreach (var (sequence, index) in generated.Select((sequence, index) => (sequence, index)))
        {
            var stored = session.Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = sequence }));
            Assert.Equal($"payload-{index}", stored!.Values.Values["payload"]);
        }
        Assert.InRange(generatedCommands.Length, 1, 32);
        Assert.Single(appendCommands.Where(command => command.Operation.EndsWith("generated-sequence-high-water", StringComparison.Ordinal)));
        Assert.DoesNotContain(appendCommands, command => command.Operation == provider + ".insert");

        var beforeReplay = observer.Commands.Count;
        var replayed = exact.AppendWithOutcomes(operation, values);
        Assert.Equal(WriteOutcomeStatus.Replayed, replayed.Status);
        Assert.Equal(
            committed.Outcomes.Select(outcome => outcome.GeneratedValues),
            replayed.Outcomes.Select(outcome => outcome.GeneratedValues));
        Assert.DoesNotContain(
            observer.Commands.Skip(beforeReplay),
            command => command.Operation.Contains("generated-sequence-batch", StringComparison.Ordinal));

        Assert.Throws<AppendIdempotencyConflictException>(() => exact.AppendWithOutcomes(
            operation,
            [Values("changed", DateTimeOffset.UnixEpoch)]));

        var next = exact.AppendWithOutcomes(
            new OperationId(DateTimeOffset.UtcNow, "generated-sequence-batch-next"),
            [Values("payload-next", DateTimeOffset.UnixEpoch)]);
        Assert.Equal(WriteOutcomeStatus.Inserted, next.Status);
        Assert.DoesNotContain(next.Outcomes[0].GeneratedValue<long>("sequence"), generated);
    }

    private static StorageValues Values(string payload, DateTimeOffset occurredAt) => new(new Dictionary<string, object?>
    {
        ["payload"] = payload,
        ["occurredAt"] = occurredAt
    });

    private static StorageUnit GeneratedUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "payload", Type = PortableType.String, MaxLength = 450, IsNullable = false },
            new() { Name = "occurredAt", Type = PortableType.DateTimeOffset, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) }
    };
}
