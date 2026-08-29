using Groundwork.Query.Model;
using MongoDB.Bson;
using System.Text.RegularExpressions;

namespace Groundwork.MongoDb;

/// <summary>MongoDB's one native renderer for the normalized v2 query contract.</summary>
public sealed class MongoQueryRenderer
{
    private const string MatchNoneField = "_groundwork_match_none";

    public MongoQueryCommand Render(
        QueryRequest request,
        QueryRenderOptions? options = null,
        string? physicalCollectionName = null,
        IReadOnlyList<BsonDocument>? sourcePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Join is not null)
        {
            throw new QueryRenderException(
                "GW-QUERY-032",
                $"Declared reference join '{request.Join.ReferenceName}' is modelled but this provider does not yet render the q3 join node.");
        }
        options ??= QueryRenderOptions.Default;
        request = QuerySearchKeyRewriter.Rewrite(request, options.SearchKeyColumns);
        if (options.InValueLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The In value limit must be positive.");

        var validation = PortableQuerySemantics.Validate(request);
        if (!validation.IsPortable)
        {
            var refusal = validation.Refusals[0];
            throw new QueryRenderException(refusal.Code, refusal.Message + " (" + refusal.Path + ").");
        }
        var order = EffectiveOrder(request, options);
        var baseFilter = RenderPredicate(request.Where, options, request.Table.Value);
        var filter = baseFilter;
        var matchNone = request.Where is Predicate.AlwaysFalse;
        IReadOnlyList<QueryConstant>? cursor = null;
        if (request.Paging.ContinuationToken is not null)
        {
            if (order.Count == 0)
                throw new QueryRenderException("GW-QUERY-013", "Keyset continuation requires an explicit ordered query.");
            try
            {
                cursor = QueryContinuationToken.Decode(request.Paging.ContinuationToken, request, options);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                throw new QueryRenderException("GW-QUERY-013", "The keyset continuation token is invalid: " + exception.Message);
            }
            if (!request.Result.IncludesTotalCount && request.LatestPerKey is null && !request.Distinct)
                filter = And(filter, RenderContinuation(order, cursor));
        }

        var selectedIndex = options.FindPinnedIndex();
        var expectedIndex = options.FindSelectedIndex();
        if (selectedIndex is not null && !selectedIndex.IncludesNulls && matchNone)
        {
            // A contradiction matches no document, but MongoDB still needs the partial-index
            // eligibility predicate present when a pinned partial index is hinted.
            var untyped = selectedIndex.Columns
                .Where(column => !selectedIndex.ColumnTypes.ContainsKey(column))
                .ToArray();
            if (untyped.Length != 0)
                throw new QueryRenderException(
                    "GW-QUERY-009",
                    $"Pinned MongoDB partial index '{selectedIndex.Name}' requires exact QueryIndexColumn types for its excluded columns: {string.Join(", ", untyped)}.");
            var sparseEligibility = new BsonDocument("$and", new BsonArray(
                selectedIndex.Columns.Select(column =>
                    new BsonDocument(column, selectedIndex.ColumnTypes.TryGetValue(column, out var type) && type is QueryType knownType
                        ? new BsonDocument("$type", MongoTypeName(knownType))
                        : new BsonDocument("$exists", true)))));
            baseFilter = And(baseFilter, sparseEligibility);
            filter = And(filter, sparseEligibility);
        }
        if (selectedIndex is not null && !matchNone && !selectedIndex.IncludesNulls)
        {
            var unproven = selectedIndex.Columns
                .Where(column => selectedIndex.NullableColumns.Contains(column) && CanMatchNull(request.Where, column))
                .ToArray();
            if (unproven.Length != 0)
                throw new QueryRenderException(
                    "GW-QUERY-009",
                    $"Query on '{request.Table.Value}' can match null values in sparse pinned index column(s) " +
                    $"{string.Join(", ", unproven)}; the declaration must include nulls or use an unpinned index.");
        }

        if (request.Result is ResultShape.Reduction reduction)
        {
            var reductionProjection = new BsonDocument(reduction.Column.Name, 1);
            var reductionPipeline = RenderReductionPipeline(
                physicalCollectionName ?? request.Table.Value,
                baseFilter,
                cursor,
                request.LatestPerKey,
                order,
                request,
                reduction,
                options,
                sourcePrefix);
            return new MongoQueryCommand(
                filter,
                new BsonDocument(order.Select(term =>
                    new BsonElement(term.Column.Name, term.Direction == OrderDirection.Ascending ? 1 : -1))),
                reductionProjection,
                null,
                null,
                selectedIndex is null ? null : options.ResolvePhysicalIndexName(selectedIndex.Name),
                false,
                matchNone,
                order.Select(term => term.Column.Name).ToArray(),
                reductionPipeline,
                expectedIndex?.Name);
        }

        var projection = request.Projection.AllColumns
            ? new BsonDocument()
            : new BsonDocument(request.Projection.Columns.ToDictionary(column => column.Name, _ => (BsonValue)1));
        var sort = new BsonDocument(order.Select(term =>
            new BsonElement(term.Column.Name, term.Direction == OrderDirection.Ascending ? 1 : -1)));
        var pipeline = RenderPipeline(physicalCollectionName ?? request.Table.Value, baseFilter, cursor,
            request.LatestPerKey, order, projection, request.Paging, request.Result.IncludesTotalCount,
            options, sourcePrefix, request.Distinct);
        return new MongoQueryCommand(
            filter,
            sort,
            projection,
            request.Paging.Offset,
            request.Paging.Limit,
            selectedIndex is null ? null : options.ResolvePhysicalIndexName(selectedIndex.Name),
            request.Result.IncludesTotalCount,
            matchNone,
            order.Select(term => term.Column.Name).ToArray(),
            pipeline,
            expectedIndex?.Name);
    }

    private IReadOnlyList<BsonDocument> RenderReductionPipeline(
        string collectionName,
        BsonDocument baseFilter,
        IReadOnlyList<QueryConstant>? cursor,
        LatestPerKey? latest,
        IReadOnlyList<OrderTerm> order,
        QueryRequest request,
        ResultShape.Reduction reduction,
        QueryRenderOptions options,
        IReadOnlyList<BsonDocument>? sourcePrefix)
    {
        // Render the source with all native filtering, latest-per-key, continuation, and ordering
        // stages first. A reduction is never a find followed by client-side row materialization.
        var pipeline = RenderPipeline(collectionName, baseFilter, request.Distinct ? null : cursor, latest, order,
            projection: new BsonDocument(),
            paging: request.Distinct ? Paging.None : request.Paging,
            includesTotalCount: false,
            options,
            sourcePrefix ?? Array.Empty<BsonDocument>(), distinct: false).Select(stage => stage.DeepClone().AsBsonDocument).ToList();

        if (request.Distinct)
        {
            var group = new BsonDocument { { "_id", "$" + reduction.Column.Name } };
            for (var index = 0; index < order.Count; index++)
            {
                var field = "__groundwork_distinct_order_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                group.Add(field, new BsonDocument("$first", "$" + order[index].Column.Name));
            }
            pipeline.Add(new BsonDocument("$group", group));

            var projection = new BsonDocument { { "_id", 0 }, { reduction.Column.Name, "$_id" } };
            for (var index = 0; index < order.Count; index++)
            {
                var field = "__groundwork_distinct_order_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                projection.Add(field, 1);
                if (!string.Equals(order[index].Column.Name, reduction.Column.Name, StringComparison.Ordinal))
                    projection.Add(order[index].Column.Name, "$" + field);
            }
            pipeline.Add(new BsonDocument("$project", projection));
            AppendReductionOrder(pipeline, order, static (term, index) =>
                "$__groundwork_distinct_order_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (cursor is not null)
                pipeline.Add(new BsonDocument("$match", RenderContinuation(order, cursor)));

            var cleanup = new BsonDocument { { "_id", 0 }, { reduction.Column.Name, 1 } };
            pipeline.Add(new BsonDocument("$project", cleanup));
            if (request.Paging.Offset is int offset)
                pipeline.Add(new BsonDocument("$skip", offset));
            if (request.Paging.Limit is int limit)
                pipeline.Add(new BsonDocument("$limit", limit));
        }

        var value = "$" + reduction.Column.Name;
        var orderedReduction = reduction is (ResultShape.Min or ResultShape.Max) &&
            reduction.Column.Type is QueryType.String or QueryType.Guid;
        if (orderedReduction)
        {
            var orderField = "__groundwork_reduction_order";
            pipeline.Add(new BsonDocument("$match", new BsonDocument(reduction.Column.Name,
                new BsonDocument("$ne", BsonNull.Value))));
            if (reduction.Column.Type == QueryType.String)
                pipeline.Add(new BsonDocument("$set", new BsonDocument(orderField, RenderOrdinalKey(value))));
            pipeline.Add(new BsonDocument("$sort", new BsonDocument(
                reduction.Column.Type == QueryType.String ? orderField : reduction.Column.Name,
                reduction is ResultShape.Min ? 1 : -1)));
            pipeline.Add(new BsonDocument("$limit", 1));
        }
        BsonValue reducedValue = reduction switch
        {
            ResultShape.Sum when reduction.Column.Type == QueryType.Decimal => new BsonDocument("$toDecimal", value),
            ResultShape.Sum when reduction.Column.Type is QueryType.Int32 or QueryType.Int64 => new BsonDocument("$toLong", value),
            _ => value
        };
        var accumulator = reduction switch
        {
            ResultShape.Sum => "$sum",
            ResultShape.Min or ResultShape.Max when orderedReduction => "$first",
            ResultShape.Min => "$min",
            ResultShape.Max => "$max",
            _ => throw new ArgumentOutOfRangeException(nameof(reduction), reduction, null)
        };
        pipeline.Add(new BsonDocument("$facet", new BsonDocument
        {
            {
                "__groundwork_reduction", new BsonArray
                {
                    new BsonDocument("$match", new BsonDocument(reduction.Column.Name,
                        new BsonDocument("$ne", BsonNull.Value))),
                    new BsonDocument("$group", new BsonDocument
                    {
                        { "_id", BsonNull.Value },
                        { "__groundwork_value", new BsonDocument(accumulator, reducedValue) }
                    })
                }
            }
        }));
        pipeline.Add(new BsonDocument("$project", new BsonDocument
        {
            { "_id", 0 },
            { reduction.Column.Name, new BsonDocument("$ifNull", new BsonArray
                {
                    new BsonDocument("$arrayElemAt", new BsonArray { "$__groundwork_reduction.__groundwork_value", 0 }),
                    BsonNull.Value
                }) }
        }));
        return pipeline;
    }

    private static void AppendReductionOrder(
        ICollection<BsonDocument> pipeline,
        IReadOnlyList<OrderTerm> order,
        Func<OrderTerm, int, string> field)
    {
        if (order.Count == 0)
            return;
        var sort = new BsonDocument();
        for (var index = 0; index < order.Count; index++)
        {
            var term = order[index];
            var expression = field(term, index);
            var rankName = "__groundwork_reduction_null_rank_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var nullRank = term.NullOrder == NullOrder.First ? 0 : 1;
            var nonNullRank = term.NullOrder == NullOrder.First ? 1 : 0;
            pipeline.Add(new BsonDocument("$set", new BsonDocument(rankName,
                new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { expression, BsonNull.Value }),
                    nullRank,
                    nonNullRank
                }))));
            sort.Add(rankName, 1);
            var orderName = term.Column.Type == QueryType.String
                ? "__groundwork_reduction_ordinal_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : expression.TrimStart('$');
            if (term.Column.Type == QueryType.String)
                pipeline.Add(new BsonDocument("$set", new BsonDocument(orderName, RenderOrdinalKey(expression))));
            sort.Add(orderName, term.Direction == OrderDirection.Ascending ? 1 : -1);
        }
        pipeline.Add(new BsonDocument("$sort", sort));
    }

    internal BsonDocument RenderAggregationSourcePredicate(Predicate predicate, string table, int inValueLimit = 1_000)
        => RenderAggregationSourcePredicate(predicate, table, new QueryRenderOptions { InValueLimit = inValueLimit });

    /// <summary>
    /// Renders a provider-bound source predicate using the same search-key mappings as a normal
    /// query. Set-based mutation passes its admitted physical predicate here; retaining the map is
    /// important because the renderer uses it to recognize hidden-key ranges as direct bounds.
    /// </summary>
    internal BsonDocument RenderAggregationSourcePredicate(
        Predicate predicate,
        string table,
        QueryRenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(options);
        if (options.InValueLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The In value limit must be positive.");
        var request = new QueryRequest(
            new TableId(table),
            predicate,
            [],
            Projection.All,
            Paging.None);
        var rewritten = QuerySearchKeyRewriter.Rewrite(request, options.SearchKeyColumns);
        return RenderPredicate(rewritten.Where, options, table);
    }

    private static IReadOnlyList<OrderTerm> EffectiveOrder(QueryRequest request, QueryRenderOptions options) =>
        options.GetEffectiveOrder(request);

    private BsonDocument RenderPredicate(Predicate predicate, QueryRenderOptions options, string table)
    {
        switch (predicate)
        {
            case Predicate.AlwaysTrue:
                return new BsonDocument();
            case Predicate.AlwaysFalse:
                return MatchNone();
            case Predicate.Equal equal:
                return new BsonDocument(equal.Column.Name, ToBson(equal.Value));
            case Predicate.In membership:
                if (membership.Values.Distinct().Count() > options.InValueLimit)
                    throw new QueryRenderException(
                        "GW-QUERY-015",
                        $"Query on '{table}' has an In predicate on '{membership.Column.Name}' with " +
                        $"{membership.Values.Distinct().Count()} distinct values, exceeding the configured maximum of {options.InValueLimit}.");
                return membership.Values.Length == 0
                    ? MatchNone()
                    : new BsonDocument(membership.Column.Name, new BsonDocument("$in", new BsonArray(membership.Values.Select(ToBson))));
            case Predicate.Range range:
            {
                if (range.Column.Type == QueryType.String && !IsPhysicalSearchKeyRange(range, options))
                    return RenderStringRange(range);
                var operators = new BsonDocument();
                if (range.Lower is not null)
                    operators.Add(range.Lower.IsInclusive ? "$gte" : "$gt", ToBson(range.Lower.Value));
                if (range.Upper is not null)
                    operators.Add(range.Upper.IsInclusive ? "$lte" : "$lt", ToBson(range.Upper.Value));
                return new BsonDocument(range.Column.Name, operators);
            }
            case Predicate.ColumnCompare compare:
            {
                var operation = new BsonDocument(
                    compare.Op switch
                    {
                        CompareOp.Equal => "$eq",
                        CompareOp.NotEqual => "$ne",
                        CompareOp.LessThan => "$lt",
                        CompareOp.LessThanOrEqual => "$lte",
                        CompareOp.GreaterThan => "$gt",
                        CompareOp.GreaterThanOrEqual => "$gte",
                        _ => throw new ArgumentOutOfRangeException(nameof(compare.Op), compare.Op, null)
                    },
                    new BsonArray { "$" + compare.Left.Name, "$" + compare.Right.Name });
                return new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$ne", new BsonArray { "$" + compare.Left.Name, BsonNull.Value }),
                    new BsonDocument("$ne", new BsonArray { "$" + compare.Right.Name, BsonNull.Value }),
                    operation
                }));
            }
            case Predicate.ElementOf elementOf:
                if (elementOf.Set.Type is null)
                    throw new QueryRenderException("GW-SEM-TYPE-007", "An element set must declare its exact element type before rendering.");
                if (elementOf.Values.Length == 0)
                    return elementOf.Quantifier == SetQuantifier.Any
                        ? MatchNone()
                        : new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray
                        {
                            new BsonDocument("$type", "$" + elementOf.Set.Name), "array"
                        }));
                var values = new BsonArray(elementOf.Values.Select(ToBson));
                return elementOf.Quantifier == SetQuantifier.Any
                    ? new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$" + elementOf.Set.Name), "array" }),
                        new BsonDocument("$gt", new BsonArray
                        {
                            new BsonDocument("$size", new BsonDocument("$setIntersection", new BsonArray { "$" + elementOf.Set.Name, values })), 0
                        })
                    }))
                    : new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$" + elementOf.Set.Name), "array" }),
                        new BsonDocument("$setIsSubset", new BsonArray { values, "$" + elementOf.Set.Name })
                    }));
            case Predicate.Substring substring when substring.Anchor is Anchor.Contains or Anchor.EndsWith:
                return new BsonDocument(substring.Column.Name,
                    new BsonRegularExpression(
                        Regex.Escape(substring.Needle) + (substring.Anchor == Anchor.EndsWith ? "\\z" : string.Empty),
                        string.Empty));
            case Predicate.Not not:
                return new BsonDocument("$nor", new BsonArray { RenderPredicate(not.Inner, options, table) });
            case Predicate.And and:
                return and.Terms.Length == 0
                    ? new BsonDocument()
                    : new BsonDocument("$and", new BsonArray(and.Terms.Select(term => RenderPredicate(term, options, table))));
            case Predicate.Or or:
                return or.Terms.Length == 0
                    ? MatchNone()
                    : new BsonDocument("$or", new BsonArray(or.Terms.Select(term => RenderPredicate(term, options, table))));
            case Predicate.StartsWith:
                throw new QueryRenderException("GW-QUERY-030", "This normalized predicate requires a provider-independent persisted projection and cannot be rendered directly.");
            default:
                throw new QueryRenderException("GW-QUERY-030", "The predicate node is outside the closed native query surface.");
        }
    }

    private static bool IsPhysicalSearchKeyRange(Predicate.Range range, QueryRenderOptions options) =>
        options.SearchKeyColumns.Values.Any(mapping =>
            mapping.PhysicalColumn != mapping.SourceColumn &&
            string.Equals(mapping.PhysicalColumn, range.Column.Name, StringComparison.Ordinal));

    private BsonDocument RenderContinuation(IReadOnlyList<OrderTerm> order, IReadOnlyList<QueryConstant> cursor)
    {
        var alternatives = new List<BsonDocument>();
        for (var boundary = 0; boundary < order.Count; boundary++)
        {
            var conjunction = new List<BsonDocument>();
            for (var prefix = 0; prefix < boundary; prefix++)
                conjunction.Add(RenderCursorEquality(order[prefix], cursor[prefix]));
            conjunction.Add(RenderAfter(order[boundary], cursor[boundary]));
            alternatives.Add(conjunction.Count == 1
                ? conjunction[0]
                : new BsonDocument("$and", new BsonArray(conjunction)));
        }
        return alternatives.Count == 1
            ? alternatives[0]
            : new BsonDocument("$or", new BsonArray(alternatives));
    }

    private BsonDocument RenderCursorEquality(OrderTerm term, QueryConstant value)
    {
        if (term.Column.Type == QueryType.String && value.Kind != QueryConstantKind.Null)
            return new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray
            {
                RenderOrdinalKey("$" + term.Column.Name), RenderOrdinalKey(value)
            }));
        return new BsonDocument(term.Column.Name, value.Kind == QueryConstantKind.Null
            ? BsonNull.Value
            : ToBson(value));
    }

    private BsonDocument RenderStringRange(Predicate.Range range)
    {
        var clauses = new BsonArray
        {
            new BsonDocument("$ne", new BsonArray { "$" + range.Column.Name, BsonNull.Value })
        };
        if (range.Lower is { } lower)
            clauses.Add(new BsonDocument(lower.IsInclusive ? "$gte" : "$gt", new BsonArray
            {
                RenderOrdinalKey("$" + range.Column.Name), RenderOrdinalKey(lower.Value)
            }));
        if (range.Upper is { } upper)
            clauses.Add(new BsonDocument(upper.IsInclusive ? "$lte" : "$lt", new BsonArray
            {
                RenderOrdinalKey("$" + range.Column.Name), RenderOrdinalKey(upper.Value)
            }));
        return new BsonDocument("$expr", new BsonDocument("$and", clauses));
    }

    private BsonDocument RenderAfter(OrderTerm term, QueryConstant value)
    {
        if (term.NullOrder is not (NullOrder.First or NullOrder.Last))
            throw new QueryRenderException("GW-SEM-ORDER-004", "Continuation requires explicit null ordering.");
        if (value.Kind == QueryConstantKind.Null)
            return term.NullOrder == NullOrder.First
                ? new BsonDocument(term.Column.Name, new BsonDocument("$ne", BsonNull.Value))
                : MatchNone();

        BsonDocument strict;
        if (term.Column.Type == QueryType.String)
        {
            var operation = term.Direction == OrderDirection.Ascending ? "$gt" : "$lt";
            strict = new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$ne", new BsonArray { "$" + term.Column.Name, BsonNull.Value }),
                new BsonDocument(operation, new BsonArray { RenderOrdinalKey("$" + term.Column.Name), RenderOrdinalKey(value) })
            }));
        }
        else
            strict = new BsonDocument(term.Column.Name, new BsonDocument(
                term.Direction == OrderDirection.Ascending ? "$gt" : "$lt", ToBson(value)));
        if (term.NullOrder == NullOrder.Last)
            return new BsonDocument("$or", new BsonArray
            {
                strict,
                new BsonDocument(term.Column.Name, BsonNull.Value)
            });
        return strict;
    }

    private static BsonValue ToBson(QueryConstant value)
    {
        if (value.Kind == QueryConstantKind.Null)
            return BsonNull.Value;
        return value.Type switch
        {
            QueryType.Boolean => new BsonBoolean((bool)value.Value!),
            QueryType.Int32 => new BsonInt32((int)value.Value!),
            QueryType.Int64 => new BsonInt64((long)value.Value!),
            QueryType.Decimal => new BsonDecimal128((decimal)value.Value!),
            QueryType.Double => new BsonDouble((double)value.Value!),
            QueryType.String => new BsonString((string)value.Value!),
            QueryType.DateTimeOffset => new BsonInt64(((DateTimeOffset)value.Value!).UtcTicks),
            QueryType.Guid => new BsonBinaryData((Guid)value.Value!, GuidRepresentation.Standard),
            QueryType.Binary => new BsonBinaryData((byte[])value.Value!),
            _ => throw new ArgumentOutOfRangeException(nameof(value), value.Type, null)
        };
    }

    private static BsonDocument MatchNone() => new(MatchNoneField, true);

    private static string MongoTypeName(QueryType type) => type switch
    {
        QueryType.String => "string",
        QueryType.Int32 => "int",
        QueryType.Int64 => "long",
        QueryType.Decimal => "decimal",
        QueryType.Boolean => "bool",
        QueryType.DateTimeOffset => "long",
        QueryType.Guid or QueryType.Binary => "binData",
        QueryType.Double => "double",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    private IReadOnlyList<BsonDocument> RenderPipeline(
        string collectionName,
        BsonDocument baseFilter,
        IReadOnlyList<QueryConstant>? cursor,
        LatestPerKey? latest,
        IReadOnlyList<OrderTerm> order,
        BsonDocument projection,
        Paging paging,
        bool includesTotalCount,
        QueryRenderOptions options,
        IReadOnlyList<BsonDocument>? sourcePrefix,
        bool distinct = false)
    {
        if (order.Count == 0 && !includesTotalCount && latest is null && sourcePrefix is null && !distinct)
            return Array.Empty<BsonDocument>();

        var prefix = sourcePrefix?.Select(stage => stage.DeepClone().AsBsonDocument).ToList() ?? [];
        prefix.Add(new BsonDocument("$match", baseFilter.DeepClone()));
        if (latest is not null)
        {
            var latestSort = new BsonDocument
            {
                { latest.Key.Name, 1 },
                { latest.Timestamp.Name, -1 }
            };
            var tieIndex = 0;
            foreach (var tieBreak in options.TieBreakColumns.Where(column =>
                         !string.Equals(column.Name, latest.Key.Name, StringComparison.Ordinal) &&
                         !string.Equals(column.Name, latest.Timestamp.Name, StringComparison.Ordinal)))
            {
                var tieName = tieBreak.Type == QueryType.String
                    ? "_groundwork_latest_tie_key_" + tieIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : tieBreak.Name;
                if (tieBreak.Type == QueryType.String)
                {
                    prefix.Add(new BsonDocument("$set", new BsonDocument(tieName,
                        RenderOrdinalKey("$" + tieBreak.Name))));
                }
                latestSort.Add(tieName, 1);
                tieIndex++;
            }
            prefix.Add(new BsonDocument("$sort", latestSort));
            BsonValue latestGroup = options.LatestPartitionColumns.Length == 0
                ? "$" + latest.Key.Name
                : new BsonDocument(
                    new[] { latest.Key }.Concat(options.LatestPartitionColumns)
                        .GroupBy(column => column.Name, StringComparer.Ordinal)
                        .Select(group => new BsonElement(group.Key, "$" + group.Key)));
            prefix.Add(new BsonDocument("$group", new BsonDocument
            {
                { "_id", latestGroup },
                { "__groundwork_latest", new BsonDocument("$first", "$$ROOT") }
            }));
            prefix.Add(new BsonDocument("$replaceWith", "$__groundwork_latest"));
        }

        var data = new List<BsonDocument>();
        BsonDocument? distinctContinuationMatch = null;
        if (cursor is not null && !distinct)
            data.Add(new BsonDocument("$match", RenderContinuation(order, cursor)));
        var sort = new BsonDocument();
        for (var index = 0; index < order.Count; index++)
        {
            var term = order[index];
            if (term.NullOrder is not (NullOrder.First or NullOrder.Last))
                throw new QueryRenderException("GW-SEM-ORDER-004", "Mongo aggregation ordering requires explicit null ordering.");

            var rankName = "_groundwork_null_rank_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var nullRank = term.NullOrder == NullOrder.First ? 0 : 1;
            var nonNullRank = term.NullOrder == NullOrder.First ? 1 : 0;
            data.Add(new BsonDocument("$set", new BsonDocument(rankName,
                new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray { "$" + term.Column.Name, BsonNull.Value }),
                    nullRank,
                    nonNullRank
                }))));
            sort.Add(rankName, 1);
            var orderName = term.Column.Type == QueryType.String
                ? "_groundwork_ordinal_key_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : term.Column.Name;
            if (term.Column.Type == QueryType.String)
                data.Add(new BsonDocument("$set", new BsonDocument(orderName,
                    RenderOrdinalKey("$" + term.Column.Name))));
            sort.Add(orderName, term.Direction == OrderDirection.Ascending ? 1 : -1);
        }
        if (sort.ElementCount != 0)
            data.Add(new BsonDocument("$sort", sort));

        if (distinct)
        {
            BsonValue distinctKey;
            if (projection.ElementCount == 0)
            {
                // For an all-column projection the complete document is the public value. The
                // internal sort fields are derived from those same values and therefore remain
                // stable for equal documents.
                distinctKey = "$$ROOT";
            }
            else
            {
                distinctKey = new BsonDocument(projection.Names
                    .Where(name => !name.StartsWith("__groundwork_", StringComparison.Ordinal))
                    .Select(name => new BsonElement(name, "$" + name)));
            }
            data.Add(new BsonDocument("$group", new BsonDocument
            {
                { "_id", distinctKey },
                { "__groundwork_distinct_first", new BsonDocument("$first", "$$ROOT") }
            }));
            data.Add(new BsonDocument("$replaceWith", "$__groundwork_distinct_first"));
            // $group does not preserve the order of its input. Restore the same portable ordinal
            // ordering on the de-duplicated rows before applying continuation or paging.
            if (sort.ElementCount != 0)
                data.Add(new BsonDocument("$sort", sort.DeepClone().AsBsonDocument));
            if (cursor is not null)
            {
                distinctContinuationMatch = new BsonDocument("$match", RenderContinuation(order, cursor));
                data.Add(distinctContinuationMatch);
            }
        }
        if (projection.ElementCount != 0)
        {
            var renderedProjection = projection.DeepClone().AsBsonDocument;
            if (distinct)
            {
                foreach (var term in order)
                {
                    if (!renderedProjection.Contains(term.Column.Name))
                        renderedProjection.Add(term.Column.Name, 1);
                }
            }
            data.Add(new BsonDocument("$project", renderedProjection));
        }
        else if (order.Count != 0)
        {
            var cleanup = new BsonDocument();
            for (var index = 0; index < order.Count; index++)
            {
                cleanup.Add("_groundwork_null_rank_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
                if (order[index].Column.Type == QueryType.String)
                    cleanup.Add("_groundwork_ordinal_key_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
            }
            data.Add(new BsonDocument("$project", cleanup));
        }
        if (paging.Offset is int offset)
            data.Add(new BsonDocument("$skip", offset));
        if (paging.Limit is int limit)
            data.Add(new BsonDocument("$limit", limit));

        if (includesTotalCount)
        {
            if (cursor is null && paging.Offset is null && paging.Limit is null)
            {
                // Keep an unpaged count streaming. A facet would collect every document
                // into one BSON array and can exceed MongoDB's 16 MB document limit.
                data.Add(new BsonDocument("$setWindowFields", new BsonDocument
                {
                    { "output", new BsonDocument("__groundwork_total_count", new BsonDocument("$count", new BsonDocument())) }
                }));
                prefix.AddRange(data);
                return prefix;
            }
            var countPipeline = prefix.Select(stage => stage.DeepClone()).ToList();
            if (distinct)
            {
                countPipeline.AddRange(data
                    .Where(stage => !ReferenceEquals(stage, distinctContinuationMatch) &&
                                    !stage.Contains("$skip") && !stage.Contains("$limit"))
                    .Select(stage => stage.DeepClone()));
            }
            countPipeline.Add(new BsonDocument("$count", "__groundwork_total_count"));
            countPipeline.Add(new BsonDocument("$set", new BsonDocument("__groundwork_count_only", 1)));
            prefix.AddRange(data);
            prefix.Add(new BsonDocument("$unionWith", new BsonDocument
            {
                { "coll", collectionName },
                { "pipeline", new BsonArray(countPipeline) }
            }));
            return prefix;
        }
        prefix.AddRange(data);
        return prefix;
    }

    internal static BsonDocument RenderOrdinalKey(string field)
        => RenderOrdinalKey(new BsonString(field));

    private static BsonDocument RenderOrdinalKey(QueryConstant value)
        => RenderOrdinalKey(ToBson(value));

    private static BsonDocument RenderOrdinalKey(BsonValue value)
        => new("$function", new BsonDocument
        {
            { "body", "function(value) { if (value === null || value === undefined) return null; var key = ''; for (var i = 0; i < value.length; i++) { var unit = value.charCodeAt(i).toString(16); key += ('0000' + unit).slice(-4); } return key; }" },
            { "args", new BsonArray { value } },
            { "lang", "js" }
        });

    private static BsonDocument And(BsonDocument left, BsonDocument right) =>
        new("$and", new BsonArray { left, right });

    private static bool CanMatchNull(Predicate predicate, string column)
    {
        switch (predicate)
        {
            case Predicate.AlwaysFalse:
                return false;
            case Predicate.AlwaysTrue:
                return true;
            case Predicate.Equal equal when equal.Column.Name == column:
                return equal.Value.Kind == QueryConstantKind.Null;
            case Predicate.In membership when membership.Column.Name == column:
                return membership.Values.Any(value => value.Kind == QueryConstantKind.Null);
            case Predicate.Range range when range.Column.Name == column:
                return false;
            case Predicate.ColumnCompare compare when compare.Left.Name == column || compare.Right.Name == column:
                return false;
            case Predicate.Not not:
                return !CanMatchNull(not.Inner, column);
            case Predicate.And and:
                return and.Terms.All(term => CanMatchNull(term, column));
            case Predicate.Or or:
                return or.Terms.Any(term => CanMatchNull(term, column));
            default:
                return true;
        }
    }
}
