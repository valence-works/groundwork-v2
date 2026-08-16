using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class PublicBuilderTests
{
    [Fact]
    public void Kernel_builder_declares_the_second_family_capabilities_without_records()
    {
        var unit = Groundwork.Kernel.StorageUnit
            .Declare("event-log", "event_logs")
            .Column("id", PortableType.Int64, column => column.Required().ProviderSequence())
            .String("trace", 64, column => column.Required())
            .String("level", 16, column => column.Required())
            .Timestamp("occurred", column => column.Required())
            .String("message", 128)
            .Key("id")
            .Index("by_trace", index => index.Column("trace").Descending("id"))
            .Scoped()
            .AppendIdempotency(TimeSpan.FromMinutes(1))
            .KeepNewest(100, "id", RetentionTrigger.OnAppend)
            .Aggregate("summary", aggregate => aggregate
                .GroupBy("trace")
                .Min("first", "occurred")
                .Max("last", "occurred")
                .SetUnion("levels", "level", 4)
                .FirstBy("message", "message", "id"))
            .Build();

        Assert.Equal(ScopePolicy.Scoped, unit.Scope);
        Assert.Equal(ColumnGeneration.ProviderSequence, unit.Columns.Single(column => column.Name == "id").Generation);
        Assert.Equal(["trace", "id"], unit.Indexes.Single().Columns.Select(column => column.Column));
        Assert.Equal(TimeSpan.FromMinutes(1), unit.AppendIdempotency!.Window);
        Assert.Equal(100, unit.Retention!.KeepNewest);
        Assert.Equal(["trace"], unit.AggregationProfiles.Single().GroupByColumns);
        Assert.Equal(4, Assert.IsType<Aggregate.SetUnion>(unit.AggregationProfiles.Single().Aggregates[2]).MaxValues);
    }

    [Fact]
    public void Kernel_builder_declares_closed_time_bucket_groups()
    {
        var unit = Groundwork.Kernel.StorageUnit
            .Declare("time-bucket", "time_buckets")
            .String("id", 32, column => column.Required())
            .Timestamp("createdAt")
            .Key("id")
            .Aggregate("hourly", aggregate => aggregate
                .FixedUtcBucket("bucket", "createdAt", TimeSpan.FromHours(1))
                .Count("count"))
            .Build();

        var group = Assert.IsType<AggregationGroup.TimeBucket>(unit.AggregationProfiles.Single().GroupByExpressions.Single());
        Assert.Equal("bucket", group.Alias);
        Assert.Equal("createdAt", group.SourceColumn);
        Assert.Equal(AggregationTimeBucketKind.FixedUtc, group.Kind);
        Assert.Equal(TimeSpan.FromHours(1), group.Width);
    }
}
