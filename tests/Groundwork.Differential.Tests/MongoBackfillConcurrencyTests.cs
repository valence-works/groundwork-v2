using Groundwork.Kernel;
using Groundwork.MongoDb;
using MongoDB.Bson;
using Xunit;

namespace Groundwork.Differential.Tests;

public sealed class MongoBackfillConcurrencyTests
{
    private static readonly DerivedColumnDefinition SearchKey = new()
    {
        Name = "__groundwork_search_status",
        SourceColumn = "status",
        Projection = PortableProjection.BoundarySearchKey,
        AlgorithmId = PortableStringComparison.AsciiIgnoreCaseAlgorithmId
    };

    [Fact]
    public void Backfill_filter_requires_the_source_snapshot_that_produced_the_key()
    {
        Assert.Equal(
            BsonDocument.Parse("{ _id: 1, status: 'Open' }"),
            MongoSchemaCoordinator.BuildBackfillFilter(new BsonDocument { ["_id"] = 1, ["status"] = "Open" }, [SearchKey]));
        Assert.Equal(
            BsonDocument.Parse("{ _id: 2, status: { $type: 10 } }"),
            MongoSchemaCoordinator.BuildBackfillFilter(new BsonDocument { ["_id"] = 2, ["status"] = BsonNull.Value }, [SearchKey]));
        Assert.Equal(
            BsonDocument.Parse("{ _id: 3, status: { $exists: false } }"),
            MongoSchemaCoordinator.BuildBackfillFilter(new BsonDocument { ["_id"] = 3 }, [SearchKey]));
    }
}
