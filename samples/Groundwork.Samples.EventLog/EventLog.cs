using Groundwork.Kernel;

namespace Groundwork.Samples.EventLog;

/// <summary>A diagnostics-shaped stream declaration authored only against the public kernel API.</summary>
public static class EventLogDeclaration
{
    public const int DeclarationLineCount = 20;

    public static readonly StorageUnit LogRecords = StorageUnit.Declare("log-record", "log_records")
        .Int64("seq", c => c.Required().ProviderSequence())
        .String("traceId", 64, c => c.Required())
        .String("level", 16, c => c.Required())
        .Timestamp("occurredAt", c => c.Required())
        .String("message", 4000)
        .Json("attributes")
        .Key("seq")
        .Index("by_trace", x => x.Column("traceId").Column("seq"))
        .Index("by_time", x => x.Descending("occurredAt").Descending("seq"))
        .Scoped()
        .AppendIdempotency(window: TimeSpan.FromMinutes(10))
        .KeepNewest(1_000_000, orderBy: "seq", trigger: RetentionTrigger.OnAppend)
        .Aggregate("by-trace-summary", a => a
            .GroupBy("traceId")
            .Min("firstSeen", "occurredAt")
            .Max("lastSeen", "occurredAt")
            .SetUnion("levels", "level", maxValues: 8)
            .FirstBy("firstMessage", "message", orderBy: "seq"))
        .Build();
}
