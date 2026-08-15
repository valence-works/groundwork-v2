using System.Collections.Immutable;
using MongoDB.Bson;

namespace Groundwork.MongoDb;

/// <summary>Native Mongo filter, projection, order, and paging emitted for one query.</summary>
public sealed class MongoQueryCommand
{
    public MongoQueryCommand(
        BsonDocument filter,
        BsonDocument sort,
        BsonDocument projection,
        int? skip,
        int? limit,
        string? hint,
        bool includesTotalCount,
        bool isMatchNone,
        IReadOnlyList<string> appliedOrder,
        IReadOnlyList<BsonDocument>? pipeline = null,
        string? expectedIndex = null)
    {
        Filter = (filter ?? throw new ArgumentNullException(nameof(filter))).DeepClone().AsBsonDocument;
        Sort = (sort ?? throw new ArgumentNullException(nameof(sort))).DeepClone().AsBsonDocument;
        Projection = (projection ?? throw new ArgumentNullException(nameof(projection))).DeepClone().AsBsonDocument;
        Skip = skip;
        Limit = limit;
        Hint = hint;
        IncludesTotalCount = includesTotalCount;
        IsMatchNone = isMatchNone;
        AppliedOrder = (appliedOrder ?? throw new ArgumentNullException(nameof(appliedOrder))).ToImmutableArray();
        Pipeline = (pipeline ?? Array.Empty<BsonDocument>())
            .Select(stage => (stage ?? throw new ArgumentException("Mongo pipeline stages cannot be null.", nameof(pipeline))).DeepClone().AsBsonDocument)
            .ToImmutableArray();
        ExpectedIndex = expectedIndex;
    }

    public BsonDocument Filter { get; }
    public BsonDocument Sort { get; }
    public BsonDocument Projection { get; }
    public int? Skip { get; }
    public int? Limit { get; }
    public string? Hint { get; }
    public bool IncludesTotalCount { get; }
    public bool IsMatchNone { get; }
    public ImmutableArray<string> AppliedOrder { get; }
    /// <summary>When non-empty, execute this aggregation pipeline instead of a find command.</summary>
    public ImmutableArray<BsonDocument> Pipeline { get; }

    /// <summary>Logical index expected from the optimizer, without implying a native hint.</summary>
    public string? ExpectedIndex { get; }
}
