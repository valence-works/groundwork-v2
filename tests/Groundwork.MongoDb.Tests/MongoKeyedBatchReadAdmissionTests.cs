using Groundwork.MongoDb;
using Groundwork.Query.Model;
using Groundwork.Store;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoKeyedBatchReadAdmissionTests
{
    [Fact]
    public void Mongo_advertises_an_effectively_unbounded_key_count_and_conservative_payload_budget()
    {
        using var connection = new MongoProviderFactory().Create("mongodb://localhost:27017/groundwork");

        var profile = connection.GetQueryAdmission();

        Assert.Equal(int.MaxValue, profile.MaximumBatchReadKeys);
        Assert.Equal(QueryRenderOptions.Default.InValueLimit, profile.MaximumInValues);
        Assert.Equal(int.MaxValue, profile.MaximumParameters);
        Assert.Equal(15L * 1024 * 1024, profile.MaximumBatchReadPayloadBytes);
    }
}
