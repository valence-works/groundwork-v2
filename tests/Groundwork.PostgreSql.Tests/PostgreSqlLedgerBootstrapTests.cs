using System.Collections.Concurrent;
using Groundwork.Kernel;
using Groundwork.PostgreSql;
using Groundwork.Store;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

/// <summary>
/// Proves that the lazy, write-path bootstrap of the append idempotency ledger survives
/// concurrent first appends. PostgreSQL's <c>IF NOT EXISTS</c> DDL is check-then-act, so
/// without a guard the losing writer fails with a raw <c>23505</c> on a shared catalog
/// index instead of returning a Groundwork status.
/// </summary>
public sealed class PostgreSqlLedgerBootstrapTests
{
    private const int Writers = 8;

    [SkippableFact]
    public void Concurrent_first_appends_bootstrap_the_append_ledger_once()
    {
        using var fixture = PostgreSqlFixture.OpenOrSkip();
        var unit = LedgerUnit("ledger_bootstrap_");
        using (var owner = new PostgreSqlProviderFactory().Create(fixture.ConnectionString))
            Assert.True(owner.Schema.Apply(unit).Applied);

        var (statuses, failures) = AppendConcurrently(fixture, unit, index =>
            new StorageValues(new Dictionary<string, object?> { ["id"] = "row-" + index, ["payload"] = "row-" + index }));

        AssertEveryWriterInserted(statuses, failures);
    }

    [SkippableFact]
    public void Concurrent_first_appends_bootstrap_the_sequence_high_water_table_once()
    {
        using var fixture = PostgreSqlFixture.OpenOrSkip();
        var unit = SequenceUnit("high_water_bootstrap_");
        using (var owner = new PostgreSqlProviderFactory().Create(fixture.ConnectionString))
            Assert.True(owner.Schema.Apply(unit).Applied);

        var (statuses, failures) = AppendConcurrently(fixture, unit, index =>
            new StorageValues(new Dictionary<string, object?> { ["payload"] = "row-" + index }));

        AssertEveryWriterInserted(statuses, failures);
    }

    private static void AssertEveryWriterInserted(
        IReadOnlyList<WriteOutcomeStatus> statuses,
        IReadOnlyList<Exception> failures)
    {
        Assert.True(failures.Count == 0,
            $"{failures.Count} of {Writers} concurrent first appends failed instead of returning a status: " +
            string.Join(" | ", failures.Select(failure => failure.GetType().Name + ": " + failure.Message)));
        Assert.Equal(Writers, statuses.Count(status => status == WriteOutcomeStatus.Inserted));
    }

    private static (IReadOnlyList<WriteOutcomeStatus> Statuses, IReadOnlyList<Exception> Failures) AppendConcurrently(
        PostgreSqlFixture fixture,
        StorageUnit unit,
        Func<int, StorageValues> row)
    {
        var statuses = new ConcurrentBag<WriteOutcomeStatus>();
        var failures = new ConcurrentBag<Exception>();
        using var ready = new Barrier(Writers);
        var threads = Enumerable.Range(0, Writers).Select(index => new Thread(() =>
        {
            try
            {
                using var connection = new PostgreSqlProviderFactory().Create(fixture.ConnectionString);
                var session = connection.OpenSession(unit, StorageAccess.Global);
                // Every writer reaches its very first append with an open connection, so all of
                // them observe the ledger as absent and race to create it.
                ready.SignalAndWait(TimeSpan.FromSeconds(30));
                statuses.Add(session.Append(new OperationId(DateTimeOffset.UnixEpoch, "writer-" + index), [row(index)]).Status);
            }
            catch (Exception failure)
            {
                failures.Add(failure);
                ready.RemoveParticipant();
            }
        })).ToArray();

        foreach (var thread in threads)
            thread.Start();
        foreach (var thread in threads)
            Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "A concurrent first append did not finish.");
        return (statuses.ToArray(), failures.ToArray());
    }

    private static StorageUnit LedgerUnit(string prefix) => new()
    {
        Id = new StorageUnitId(prefix + Guid.NewGuid().ToString("N")),
        Name = prefix + Guid.NewGuid().ToString("N"),
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
            new() { Name = "payload", Type = PortableType.String, MaxLength = 450, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AppendIdempotency = new AppendIdempotencyDeclaration
        {
            Window = TimeSpan.FromMinutes(10),
            LedgerName = "ledger_" + Guid.NewGuid().ToString("N")
        }
    };

    private static StorageUnit SequenceUnit(string prefix) => new()
    {
        Id = new StorageUnitId(prefix + Guid.NewGuid().ToString("N")),
        Name = prefix + Guid.NewGuid().ToString("N"),
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "payload", Type = PortableType.String, MaxLength = 450, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] },
        AppendIdempotency = new AppendIdempotencyDeclaration
        {
            Window = TimeSpan.FromMinutes(10),
            LedgerName = "ledger_" + Guid.NewGuid().ToString("N")
        }
    };
}
