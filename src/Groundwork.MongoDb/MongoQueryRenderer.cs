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
        IReadOnlyList<BsonDocument>? sourcePrefix = null) =>
        RenderCore(request, options, physicalCollectionName, physicalTargetCollectionName: null, sourcePrefix);

    /// <summary>
    /// Renders a query when the provider has resolved the exact physical target collection. The
    /// target cannot be reconstructed from the logical table name: schema application may rename
    /// collections, and scoped collections carry a provider-generated suffix.
    /// </summary>
    internal MongoQueryCommand Render(
        QueryRequest request,
        QueryRenderOptions? options,
        string? physicalCollectionName,
        string physicalTargetCollectionName,
        IReadOnlyList<BsonDocument>? sourcePrefix = null) =>
        RenderCore(request, options, physicalCollectionName, physicalTargetCollectionName, sourcePrefix);

    internal MongoQueryCommand Render(
        QueryRequest request,
        string? physicalCollectionName,
        string physicalTargetCollectionName,
        IReadOnlyList<BsonDocument>? sourcePrefix = null) =>
        RenderCore(request, options: null, physicalCollectionName: physicalCollectionName,
            physicalTargetCollectionName: physicalTargetCollectionName, sourcePrefix: sourcePrefix);

    private MongoQueryCommand RenderCore(
        QueryRequest request,
        QueryRenderOptions? options,
        string? physicalCollectionName,
        string? physicalTargetCollectionName,
        IReadOnlyList<BsonDocument>? sourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Join is not null)
            return RenderJoined(request, options, physicalCollectionName, physicalTargetCollectionName, sourcePrefix);
        options ??= QueryRenderOptions.Default;
        request = QueryElementSearchKeyRewriter.Rewrite(
            QuerySearchKeyRewriter.Rewrite(request, options.SearchKeyColumns),
            options.ElementSearchKeyColumns);
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
                filter = And(filter, RenderContinuation(order, cursor, options));
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

    private MongoQueryCommand RenderJoined(
        QueryRequest request,
        QueryRenderOptions? options,
        string? physicalCollectionName,
        string? physicalTargetCollectionName,
        IReadOnlyList<BsonDocument>? sourcePrefix)
    {
        if (sourcePrefix is not null)
        {
            throw new QueryRenderException(
                "GW-ACCESS-003",
                "Privileged cross-scope queries cannot activate a declared reference join; joined queries must remain within one storage scope.");
        }

        options ??= QueryRenderOptions.Default;
        request = QueryElementSearchKeyRewriter.Rewrite(
            QuerySearchKeyRewriter.Rewrite(request, options.SearchKeyColumns),
            options.ElementSearchKeyColumns);
        if (options.InValueLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The In value limit must be positive.");

        var validation = PortableQuerySemantics.Validate(request);
        if (!validation.IsPortable)
        {
            var refusal = validation.Refusals[0];
            throw new QueryRenderException(refusal.Code, refusal.Message + " (" + refusal.Path + ").");
        }

        var join = request.Join!;
        var sourceTable = request.Table.Value;
        var targetTable = join.TargetTable.Value;
        var targetCollection = physicalTargetCollectionName ?? targetTable;
        if (string.IsNullOrWhiteSpace(targetCollection))
            throw new ArgumentException("A physical target collection is required for a MongoDB join.", nameof(physicalTargetCollectionName));

        // The source-only prefix is deliberately pushed before $lookup so a declared source
        // index remains useful. The complete predicate is applied again after unwind: this keeps
        // mixed AND/OR shapes exact while allowing target-only conjunctions into the lookup.
        var sourcePredicate = TryExtractSidePredicate(request.Where, join.SourceTable);
        var targetPredicate = TryExtractSidePredicate(request.Where, join.TargetTable);
        var sourceFilter = sourcePredicate is null
            ? new BsonDocument()
            : RenderPredicate(sourcePredicate, options, sourceTable);
        var joinedFieldPath = new Func<ColumnRef, string>(column =>
            column.Table == join.SourceTable ? column.Name : TargetField(column.Name));
        var joinedFilter = RenderPredicate(request.Where, options, sourceTable, joinedFieldPath);
        var targetFilter = targetPredicate is null
            ? null
            : RenderPredicate(targetPredicate, options, targetTable);

        var effectiveOrder = EffectiveOrder(request, options);
        var matchNone = request.Where is Predicate.AlwaysFalse;
        if (matchNone)
            sourceFilter = MatchNone();
        IReadOnlyList<QueryConstant>? cursor = null;
        if (request.Paging.ContinuationToken is not null)
        {
            if (effectiveOrder.Count == 0)
                throw new QueryRenderException("GW-QUERY-013", "Keyset continuation requires an explicit ordered query.");
            try
            {
                cursor = QueryContinuationToken.Decode(request.Paging.ContinuationToken, request, options);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                throw new QueryRenderException("GW-QUERY-013", "The keyset continuation token is invalid: " + exception.Message);
            }
        }

        var selectedIndex = options.FindPinnedIndex();
        var expectedIndex = options.FindSelectedIndex();
        if (selectedIndex is not null && !selectedIndex.IncludesNulls)
        {
            if (matchNone)
            {
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
                sourceFilter = And(sourceFilter, sparseEligibility);
            }
            else
            {
                var unproven = selectedIndex.Columns
                    .Where(column => selectedIndex.NullableColumns.Contains(column) &&
                        CanMatchNull(sourcePredicate ?? Predicate.AlwaysTrue.Instance, column))
                    .ToArray();
                if (unproven.Length != 0)
                    throw new QueryRenderException(
                        "GW-QUERY-009",
                        $"Query on '{sourceTable}' can match null values in sparse pinned index column(s) " +
                        $"{string.Join(", ", unproven)}; the declaration must include nulls or use an unpinned index.");
            }
        }

        var lookup = RenderLookup(join, targetCollection, targetFilter);
        var joinedPrefix = new List<BsonDocument>
        {
            new("$match", sourceFilter),
            lookup,
            new("$unwind", new BsonDocument
            {
                { "path", "$" + TargetOutputField },
                { "preserveNullAndEmptyArrays", false }
            }),
            new("$match", joinedFilter)
        };

        if (request.Result is ResultShape.Reduction reduction)
        {
            var reductionPipeline = RenderReductionPipeline(
                physicalCollectionName ?? sourceTable,
                new BsonDocument(),
                cursor,
                request.LatestPerKey,
                effectiveOrder,
                request,
                reduction,
                options,
                joinedPrefix,
                joinedFieldPath);
            return new MongoQueryCommand(
                joinedFilter,
                RenderSort(effectiveOrder, joinedFieldPath),
                new BsonDocument(reduction.Column.Name, 1),
                null,
                null,
                selectedIndex is null ? null : options.ResolvePhysicalIndexName(selectedIndex.Name),
                false,
                matchNone,
                effectiveOrder.Select(term => term.Column.Name).ToArray(),
                reductionPipeline,
                expectedIndex?.Name);
        }

        var projection = RenderProjection(request.Projection, joinedFieldPath);
        var pipeline = RenderPipeline(
            physicalCollectionName ?? sourceTable,
            new BsonDocument(),
            cursor,
            request.LatestPerKey,
            effectiveOrder,
            projection,
            request.Paging,
            request.Result.IncludesTotalCount,
            options,
            joinedPrefix,
            request.Distinct,
            joinedFieldPath);
        return new MongoQueryCommand(
            joinedFilter,
            RenderSort(effectiveOrder, joinedFieldPath),
            projection,
            request.Paging.Offset,
            request.Paging.Limit,
            selectedIndex is null ? null : options.ResolvePhysicalIndexName(selectedIndex.Name),
            request.Result.IncludesTotalCount,
            matchNone,
            effectiveOrder.Select(term => term.Column.Name).ToArray(),
            pipeline,
            expectedIndex?.Name);
    }

    internal const string TargetOutputField = "__groundwork_target";

    private static string TargetField(string column) => TargetOutputField + "." + column;

    private static BsonDocument RenderSort(
        IReadOnlyList<OrderTerm> order,
        Func<ColumnRef, string> fieldPath)
        => new(order.Select(term => new BsonElement(
            fieldPath(term.Column), term.Direction == OrderDirection.Ascending ? 1 : -1)));

    private static BsonDocument RenderProjection(
        Projection projection,
        Func<ColumnRef, string> fieldPath)
    {
        if (projection.AllColumns)
            return new BsonDocument();
        var output = new BsonDocument();
        foreach (var column in projection.Columns)
            output.Add(fieldPath(column), 1);
        return output;
    }

    private static BsonDocument RenderLookup(
        ReferenceJoin join,
        string targetCollection,
        BsonDocument? targetFilter)
    {
        var let = new BsonDocument();
        var equalities = new BsonArray();
        for (var index = 0; index < join.ColumnPairs.Length; index++)
        {
            var pair = join.ColumnPairs[index];
            var variable = "groundwork_join_source_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            let.Add(variable, "$" + pair.Source.Name);
            equalities.Add(new BsonDocument("$eq", new BsonArray
            {
                "$" + pair.Target.Name,
                "$$" + variable
            }));
        }

        var pipeline = new List<BsonDocument>
        {
            new("$match", new BsonDocument("$expr", new BsonDocument("$and", equalities)))
        };
        if (targetFilter is not null && targetFilter.ElementCount != 0)
            pipeline.Add(new BsonDocument("$match", targetFilter));

        return new BsonDocument("$lookup", new BsonDocument
        {
            { "from", targetCollection },
            { "let", let },
            { "pipeline", new BsonArray(pipeline) },
            { "as", TargetOutputField }
        });
    }

    private static Predicate? TryExtractSidePredicate(Predicate predicate, TableId table)
    {
        switch (predicate)
        {
            case Predicate.AlwaysTrue:
                return null;
            case Predicate.AlwaysFalse:
                // Unlike true, false is a useful source-side restriction: pushing it before the
                // lookup prevents an otherwise pointless scan of every target document.
                return predicate;
            case Predicate.Equal equal when equal.Column.Table == table:
                return equal;
            case Predicate.In membership when membership.Column.Table == table:
                return membership;
            case Predicate.Range range when range.Column.Table == table:
                return range;
            case Predicate.StartsWith starts when starts.Column.Table == table:
                return starts;
            case Predicate.Substring substring when substring.Column.Table == table:
                return substring;
            case Predicate.ColumnCompare compare when compare.Left.Table == table && compare.Right.Table == table:
                return compare;
            case Predicate.Not not:
            {
                // A NOT of a mixed-side expression cannot be reduced to a side-local predicate.
                // For example, pushing NOT(source AND target) as NOT(source) would discard rows
                // whose target happens to satisfy the inner predicate. Keep such expressions in
                // the post-lookup match instead.
                if (!IsSideLocal(not.Inner, table))
                    return null;
                return not.Inner switch
                {
                    Predicate.AlwaysTrue => Predicate.AlwaysFalse.Instance,
                    Predicate.AlwaysFalse => null,
                    _ => not
                };
            }
            case Predicate.And and:
            {
                var terms = and.Terms.Select(term => TryExtractSidePredicate(term, table))
                    .Where(term => term is not null)
                    .Select(term => term!)
                    .ToArray();
                return terms.Length switch
                {
                    0 => null,
                    1 => terms[0],
                    _ => new Predicate.And(terms)
                };
            }
            case Predicate.Or or:
            {
                // A disjunction is safe to push only when every branch is on this side. An
                // omitted branch would change the join cardinality, so fail closed to no push.
                if (!or.Terms.All(term => IsSideLocal(term, table)))
                    return null;
                return predicate;
            }
            default:
                return null;
        }
    }

    private static bool IsSideLocal(Predicate predicate, TableId table) => predicate switch
    {
        Predicate.AlwaysTrue or Predicate.AlwaysFalse => true,
        Predicate.Equal equal => equal.Column.Table == table,
        Predicate.In membership => membership.Column.Table == table,
        Predicate.Range range => range.Column.Table == table,
        Predicate.StartsWith starts => starts.Column.Table == table,
        Predicate.Substring substring => substring.Column.Table == table,
        Predicate.ColumnCompare compare => compare.Left.Table == table && compare.Right.Table == table,
        Predicate.Not not => IsSideLocal(not.Inner, table),
        Predicate.And and => and.Terms.All(term => IsSideLocal(term, table)),
        Predicate.Or or => or.Terms.All(term => IsSideLocal(term, table)),
        _ => false
    };

    private IReadOnlyList<BsonDocument> RenderReductionPipeline(
        string collectionName,
        BsonDocument baseFilter,
        IReadOnlyList<QueryConstant>? cursor,
        LatestPerKey? latest,
        IReadOnlyList<OrderTerm> order,
        QueryRequest request,
        ResultShape.Reduction reduction,
        QueryRenderOptions options,
        IReadOnlyList<BsonDocument>? sourcePrefix,
        Func<ColumnRef, string>? fieldPath = null)
    {
        var joinedFields = fieldPath is not null;
        fieldPath ??= static column => column.Name;
        // Render the source with all native filtering, latest-per-key, continuation, and ordering
        // stages first. A reduction is never a find followed by client-side row materialization.
        var pipeline = RenderPipeline(collectionName, baseFilter, request.Distinct ? null : cursor, latest, order,
            projection: new BsonDocument(),
            paging: request.Distinct ? Paging.None : request.Paging,
            includesTotalCount: false,
            options,
            sourcePrefix ?? Array.Empty<BsonDocument>(), distinct: false,
            fieldPath: joinedFields ? fieldPath : null).Select(stage => stage.DeepClone().AsBsonDocument).ToList();

        if (request.Distinct)
        {
            var reductionPath = fieldPath(reduction.Column);
            var group = new BsonDocument { { "_id", "$" + reductionPath } };
            for (var index = 0; index < order.Count; index++)
            {
                var field = "__groundwork_distinct_order_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                group.Add(field, new BsonDocument("$first", "$" + fieldPath(order[index].Column)));
            }
            pipeline.Add(new BsonDocument("$group", group));

            var projection = new BsonDocument { { "_id", 0 }, { reductionPath, "$_id" } };
            for (var index = 0; index < order.Count; index++)
            {
                var field = "__groundwork_distinct_order_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                projection.Add(field, 1);
                if (!string.Equals(fieldPath(order[index].Column), reductionPath, StringComparison.Ordinal))
                    projection.Add(fieldPath(order[index].Column), "$" + field);
            }
            pipeline.Add(new BsonDocument("$project", projection));
            AppendReductionOrder(pipeline, order, static (term, index) =>
                "$__groundwork_distinct_order_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (cursor is not null)
                pipeline.Add(new BsonDocument("$match", RenderContinuation(order, cursor, options, fieldPath)));

            var cleanup = new BsonDocument { { "_id", 0 }, { reductionPath, 1 } };
            pipeline.Add(new BsonDocument("$project", cleanup));
            if (request.Paging.Offset is int offset)
                pipeline.Add(new BsonDocument("$skip", offset));
            if (request.Paging.Limit is int limit)
                pipeline.Add(new BsonDocument("$limit", limit));
        }

        var reductionField = fieldPath(reduction.Column);
        var value = "$" + reductionField;
        var orderedReduction = reduction is (ResultShape.Min or ResultShape.Max) &&
            reduction.Column.Type is QueryType.String or QueryType.Guid;
        if (orderedReduction)
        {
            var orderField = "__groundwork_reduction_order";
            pipeline.Add(new BsonDocument("$match", new BsonDocument(reductionField,
                new BsonDocument("$ne", BsonNull.Value))));
            if (reduction.Column.Type == QueryType.String)
                pipeline.Add(new BsonDocument("$set", new BsonDocument(orderField, RenderOrdinalKey(value))));
            pipeline.Add(new BsonDocument("$sort", new BsonDocument(
                reduction.Column.Type == QueryType.String ? orderField : reductionField,
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
                    new BsonDocument("$match", new BsonDocument(reductionField,
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
        var rewritten = QueryElementSearchKeyRewriter.Rewrite(
            QuerySearchKeyRewriter.Rewrite(request, options.SearchKeyColumns),
            options.ElementSearchKeyColumns);
        return RenderPredicate(rewritten.Where, options, table);
    }

    private static IReadOnlyList<OrderTerm> EffectiveOrder(QueryRequest request, QueryRenderOptions options) =>
        options.GetEffectiveOrder(request);

    private static bool SameColumnIdentity(ColumnRef left, ColumnRef right) =>
        left.Table == right.Table && string.Equals(left.Name, right.Name, StringComparison.Ordinal);

    private BsonDocument RenderPredicate(
        Predicate predicate,
        QueryRenderOptions options,
        string table,
        Func<ColumnRef, string>? fieldPath = null)
    {
        fieldPath ??= static column => column.Name;
        switch (predicate)
        {
            case Predicate.AlwaysTrue:
                return new BsonDocument();
            case Predicate.AlwaysFalse:
                return MatchNone();
            case Predicate.Equal equal:
                return new BsonDocument(fieldPath(equal.Column), ToBson(equal.Value));
            case Predicate.In membership:
                if (membership.Values.Distinct().Count() > options.InValueLimit)
                    throw new QueryRenderException(
                        "GW-QUERY-015",
                        $"Query on '{table}' has an In predicate on '{membership.Column.Name}' with " +
                        $"{membership.Values.Distinct().Count()} distinct values, exceeding the configured maximum of {options.InValueLimit}.");
                return membership.Values.Length == 0
                    ? MatchNone()
                    : new BsonDocument(fieldPath(membership.Column), new BsonDocument("$in", new BsonArray(membership.Values.Select(ToBson))));
            case Predicate.Range range:
            {
                if (range.Column.Type == QueryType.String && !IsPhysicalSearchKeyRange(range, options))
                    return RenderStringRange(range, fieldPath);
                var operators = new BsonDocument();
                if (range.Lower is not null)
                    operators.Add(range.Lower.IsInclusive ? "$gte" : "$gt", ToBson(range.Lower.Value));
                if (range.Upper is not null)
                    operators.Add(range.Upper.IsInclusive ? "$lte" : "$lt", ToBson(range.Upper.Value));
                return new BsonDocument(fieldPath(range.Column), operators);
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
                    new BsonArray { "$" + fieldPath(compare.Left), "$" + fieldPath(compare.Right) });
                return new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
                {
                    new BsonDocument("$ne", new BsonArray { "$" + fieldPath(compare.Left), BsonNull.Value }),
                    new BsonDocument("$ne", new BsonArray { "$" + fieldPath(compare.Right), BsonNull.Value }),
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
            case Predicate.ElementSubstring elementSubstring:
            {
                if (elementSubstring.Set.Type != QueryType.String)
                    throw new QueryRenderException("GW-SEM-TYPE-005", "Element substring matching requires a typed string element set.");
                if (elementSubstring.Anchor is not (Anchor.Contains or Anchor.EndsWith))
                    throw new QueryRenderException("GW-SEM-TEXT-003", "The requested element substring anchor is not portable; use Contains or EndsWith.");
                if (elementSubstring.StringComparison is not (QueryStringComparisonPolicy.Ordinal or QueryStringComparisonPolicy.AsciiIgnoreCase))
                    throw new QueryRenderException("GW-SEM-TEXT-001", "Element substring matching requires an explicit Ordinal or AsciiIgnoreCase policy; UnicodeOrdinalIgnoreCase requires a persisted per-element search key.");

                var needle = new BsonDocument("$literal", elementSubstring.Needle);
                var foldedNeedle = elementSubstring.StringComparison == QueryStringComparisonPolicy.AsciiIgnoreCase
                    ? FoldAscii(needle)
                    : needle;
                var foldedElement = elementSubstring.StringComparison == QueryStringComparisonPolicy.AsciiIgnoreCase
                    ? FoldAscii(new BsonString("$$element"))
                    : new BsonString("$$element");
                var map = new BsonDocument("$map", new BsonDocument
                {
                    { "input", "$" + elementSubstring.Set.Name },
                    { "as", "element" },
                    { "in", new BsonDocument("$cond", new BsonArray
                        {
                            new BsonDocument("$eq", new BsonArray { new BsonDocument("$type", "$$element"), "string" }),
                            ElementMatch(elementSubstring.Anchor, foldedElement, foldedNeedle),
                            false
                        }) }
                });
                return new BsonDocument("$expr", new BsonDocument("$cond", new BsonArray
                {
                    new BsonDocument("$eq", new BsonArray
                    {
                        new BsonDocument("$type", "$" + elementSubstring.Set.Name), "array"
                    }),
                    new BsonDocument("$anyElementTrue", map),
                    false
                }));
            }
            case Predicate.Substring substring when substring.Anchor is Anchor.Contains or Anchor.EndsWith:
                return new BsonDocument(fieldPath(substring.Column),
                    new BsonRegularExpression(
                        Regex.Escape(substring.Needle) + (substring.Anchor == Anchor.EndsWith ? "\\z" : string.Empty),
                        string.Empty));
            case Predicate.Not not:
                return new BsonDocument("$nor", new BsonArray { RenderPredicate(not.Inner, options, table, fieldPath) });
            case Predicate.And and:
                return and.Terms.Length == 0
                    ? new BsonDocument()
                    : new BsonDocument("$and", new BsonArray(and.Terms.Select(term => RenderPredicate(term, options, table, fieldPath))));
            case Predicate.Or or:
                return or.Terms.Length == 0
                    ? MatchNone()
                    : new BsonDocument("$or", new BsonArray(or.Terms.Select(term => RenderPredicate(term, options, table, fieldPath))));
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

    private static bool UsesPersistedOrderKey(ColumnRef column, QueryRenderOptions options) =>
        column.Type == QueryType.String &&
        options.SearchKeyColumns.Values.Any(mapping =>
            mapping.OrderByPhysicalColumn &&
            string.Equals(mapping.PhysicalColumn, column.Name, StringComparison.Ordinal));

    private static bool SelectedIndexProvesNonNull(
        ColumnRef column,
        QueryRenderOptions options,
        bool joinedFields)
    {
        var selectedIndex = options.FindSelectedIndex();
        return !joinedFields && selectedIndex is not null &&
            selectedIndex.Columns.Contains(column.Name, StringComparer.Ordinal) &&
            !selectedIndex.NullableColumns.Contains(column.Name);
    }

    private BsonDocument RenderContinuation(
        IReadOnlyList<OrderTerm> order,
        IReadOnlyList<QueryConstant> cursor,
        QueryRenderOptions options,
        Func<ColumnRef, string>? fieldPath = null)
    {
        var alternatives = new List<BsonDocument>();
        for (var boundary = 0; boundary < order.Count; boundary++)
        {
            var conjunction = new List<BsonDocument>();
            for (var prefix = 0; prefix < boundary; prefix++)
                conjunction.Add(RenderCursorEquality(order[prefix], cursor[prefix], options, fieldPath));
            conjunction.Add(RenderAfter(order[boundary], cursor[boundary], options, fieldPath));
            alternatives.Add(conjunction.Count == 1
                ? conjunction[0]
                : new BsonDocument("$and", new BsonArray(conjunction)));
        }
        return alternatives.Count == 1
            ? alternatives[0]
            : new BsonDocument("$or", new BsonArray(alternatives));
    }

    private BsonDocument RenderCursorEquality(
        OrderTerm term,
        QueryConstant value,
        QueryRenderOptions options,
        Func<ColumnRef, string>? fieldPath = null)
    {
        fieldPath ??= static column => column.Name;
        var path = fieldPath(term.Column);
        if (term.Column.Type == QueryType.String &&
            value.Kind != QueryConstantKind.Null &&
            !UsesPersistedOrderKey(term.Column, options))
            return new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray
            {
                RenderOrdinalKey("$" + path), RenderOrdinalKey(value)
            }));
        return new BsonDocument(path, value.Kind == QueryConstantKind.Null
            ? BsonNull.Value
            : ToBson(value));
    }

    private BsonDocument RenderStringRange(Predicate.Range range, Func<ColumnRef, string>? fieldPath = null)
    {
        fieldPath ??= static column => column.Name;
        var path = fieldPath(range.Column);
        var clauses = new BsonArray
        {
            new BsonDocument("$ne", new BsonArray { "$" + path, BsonNull.Value })
        };
        if (range.Lower is { } lower)
            clauses.Add(new BsonDocument(lower.IsInclusive ? "$gte" : "$gt", new BsonArray
            {
                RenderOrdinalKey("$" + path), RenderOrdinalKey(lower.Value)
            }));
        if (range.Upper is { } upper)
            clauses.Add(new BsonDocument(upper.IsInclusive ? "$lte" : "$lt", new BsonArray
            {
                RenderOrdinalKey("$" + path), RenderOrdinalKey(upper.Value)
            }));
        return new BsonDocument("$expr", new BsonDocument("$and", clauses));
    }

    private BsonDocument RenderAfter(
        OrderTerm term,
        QueryConstant value,
        QueryRenderOptions options,
        Func<ColumnRef, string>? fieldPath = null)
    {
        var joinedFields = fieldPath is not null;
        fieldPath ??= static column => column.Name;
        var path = fieldPath(term.Column);
        if (term.NullOrder is not (NullOrder.First or NullOrder.Last))
            throw new QueryRenderException("GW-SEM-ORDER-004", "Continuation requires explicit null ordering.");
        if (value.Kind == QueryConstantKind.Null)
            return term.NullOrder == NullOrder.First
                ? new BsonDocument(path, new BsonDocument("$ne", BsonNull.Value))
                : MatchNone();

        BsonDocument strict;
        if (term.Column.Type == QueryType.String && !UsesPersistedOrderKey(term.Column, options))
        {
            var operation = term.Direction == OrderDirection.Ascending ? "$gt" : "$lt";
            strict = new BsonDocument("$expr", new BsonDocument("$and", new BsonArray
            {
                new BsonDocument("$ne", new BsonArray { "$" + path, BsonNull.Value }),
                new BsonDocument(operation, new BsonArray { RenderOrdinalKey("$" + path), RenderOrdinalKey(value) })
            }));
        }
        else
            strict = new BsonDocument(path, new BsonDocument(
                term.Direction == OrderDirection.Ascending ? "$gt" : "$lt", ToBson(value)));
        if (term.NullOrder == NullOrder.Last && !SelectedIndexProvesNonNull(term.Column, options, joinedFields))
            return new BsonDocument("$or", new BsonArray
            {
                strict,
                new BsonDocument(path, BsonNull.Value)
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
        bool distinct = false,
        Func<ColumnRef, string>? fieldPath = null)
    {
        var joinedFields = fieldPath is not null;
        fieldPath ??= static column => column.Name;
        if (order.Count == 0 && !includesTotalCount && latest is null && sourcePrefix is null && !distinct)
            return Array.Empty<BsonDocument>();

        var prefix = sourcePrefix?.Select(stage => stage.DeepClone().AsBsonDocument).ToList() ?? [];
        prefix.Add(new BsonDocument("$match", baseFilter.DeepClone()));
        var latestInternalFields = new List<string>();
        if (latest is not null)
        {
            var latestValueIndex = 0;
            var latestValuePaths = new Dictionary<(string Table, string Name), string>();
            string LatestValuePath(ColumnRef column)
            {
                var identity = (column.Table.Value, column.Name);
                if (latestValuePaths.TryGetValue(identity, out var existingPath))
                    return existingPath;
                var path = fieldPath(column);
                if (!joinedFields || string.Equals(path, column.Name, StringComparison.Ordinal))
                {
                    latestValuePaths.Add(identity, path);
                    return path;
                }
                var alias = "_groundwork_latest_value_" + latestValueIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                latestValueIndex++;
                prefix.Add(new BsonDocument("$set", new BsonDocument(alias, "$" + path)));
                latestInternalFields.Add(alias);
                latestValuePaths.Add(identity, alias);
                return alias;
            }

            var latestSort = new BsonDocument
            {
                { LatestValuePath(latest.Key), 1 },
                { LatestValuePath(latest.Timestamp), -1 }
            };
            var tieIndex = 0;
            foreach (var tieBreak in options.TieBreakColumns.Where(column =>
                         !SameColumnIdentity(column, latest.Key) &&
                         !SameColumnIdentity(column, latest.Timestamp)))
            {
                var tieName = tieBreak.Type == QueryType.String
                    ? "_groundwork_latest_tie_key_" + tieIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : LatestValuePath(tieBreak);
                if (tieBreak.Type == QueryType.String)
                {
                    prefix.Add(new BsonDocument("$set", new BsonDocument(tieName,
                        RenderOrdinalKey("$" + fieldPath(tieBreak)))));
                    latestInternalFields.Add(tieName);
                }
                latestSort.Add(tieName, 1);
                tieIndex++;
            }
            prefix.Add(new BsonDocument("$sort", latestSort));
            BsonValue latestGroup = options.LatestPartitionColumns.Length == 0
                ? "$" + LatestValuePath(latest.Key)
                : joinedFields
                    ? new BsonDocument(
                        new[] { latest.Key }.Concat(options.LatestPartitionColumns)
                            .GroupBy(column => (column.Table.Value, column.Name))
                            .Select((group, index) => new BsonElement(
                                "_groundwork_latest_partition_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                "$" + LatestValuePath(group.First()))))
                    : new BsonDocument(
                        new[] { latest.Key }.Concat(options.LatestPartitionColumns)
                            .GroupBy(column => column.Name, StringComparer.Ordinal)
                            .Select(group => new BsonElement(group.Key, "$" + LatestValuePath(group.First()))));
            prefix.Add(new BsonDocument("$group", new BsonDocument
            {
                { "_id", latestGroup },
                { "__groundwork_latest", new BsonDocument("$first", "$$ROOT") }
            }));
            prefix.Add(new BsonDocument("$replaceWith", "$__groundwork_latest"));
        }

        var data = new List<BsonDocument>();
        var orderInternalFields = new List<string>();
        BsonDocument? distinctContinuationMatch = null;
        if (cursor is not null && !distinct)
            data.Add(new BsonDocument("$match", RenderContinuation(order, cursor, options, fieldPath)));
        var sort = new BsonDocument();
        for (var index = 0; index < order.Count; index++)
        {
            var term = order[index];
            if (term.NullOrder is not (NullOrder.First or NullOrder.Last))
                throw new QueryRenderException("GW-SEM-ORDER-004", "Mongo aggregation ordering requires explicit null ordering.");

            var valuePath = fieldPath(term.Column);
            var provesNonNull = SelectedIndexProvesNonNull(term.Column, options, joinedFields);
            if (!provesNonNull)
            {
                var rankName = "_groundwork_null_rank_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var nullRank = term.NullOrder == NullOrder.First ? 0 : 1;
                var nonNullRank = term.NullOrder == NullOrder.First ? 1 : 0;
                data.Add(new BsonDocument("$set", new BsonDocument(rankName,
                    new BsonDocument("$cond", new BsonArray
                    {
                        new BsonDocument("$eq", new BsonArray { "$" + valuePath, BsonNull.Value }),
                        nullRank,
                        nonNullRank
                    }))));
                sort.Add(rankName, 1);
                orderInternalFields.Add(rankName);
            }

            var usesPersistedOrderKey = UsesPersistedOrderKey(term.Column, options);
            var orderName = term.Column.Type == QueryType.String && !usesPersistedOrderKey
                ? "_groundwork_ordinal_key_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : joinedFields && !string.Equals(valuePath, term.Column.Name, StringComparison.Ordinal)
                    ? "_groundwork_order_value_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : valuePath;
            if (term.Column.Type == QueryType.String && !usesPersistedOrderKey)
            {
                data.Add(new BsonDocument("$set", new BsonDocument(orderName,
                    RenderOrdinalKey("$" + valuePath))));
                orderInternalFields.Add(orderName);
            }
            else if (joinedFields && !string.Equals(orderName, valuePath, StringComparison.Ordinal))
                data.Add(new BsonDocument("$set", new BsonDocument(orderName, "$" + valuePath)));
            if (joinedFields)
            {
                var continuationName = QueryRequestExecution.ContinuationFieldName(index);
                data.Add(new BsonDocument("$set", new BsonDocument(continuationName, "$" + valuePath)));
            }
            if (term.Column.Type != QueryType.String && !string.Equals(orderName, valuePath, StringComparison.Ordinal))
                orderInternalFields.Add(orderName);
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
                var distinctNames = projection.Names
                    .Where(name => joinedFields
                        ? !name.StartsWith("__groundwork_continuation_", StringComparison.Ordinal)
                        : !name.StartsWith("__groundwork_", StringComparison.Ordinal))
                    .ToArray();
                distinctKey = joinedFields
                    ? new BsonDocument(distinctNames.Select((name, index) => new BsonElement(
                        "_groundwork_distinct_value_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), "$" + name)))
                    : new BsonDocument(distinctNames.Select(name => new BsonElement(name, "$" + name)));
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
                distinctContinuationMatch = new BsonDocument("$match", RenderContinuation(order, cursor, options, fieldPath));
                data.Add(distinctContinuationMatch);
            }
        }
        if (projection.ElementCount != 0)
        {
            var renderedProjection = projection.DeepClone().AsBsonDocument;
            if (distinct && !joinedFields)
            {
                foreach (var term in order)
                {
                    if (!renderedProjection.Contains(term.Column.Name))
                        renderedProjection.Add(term.Column.Name, 1);
                }
            }
            if (joinedFields)
            {
                for (var index = 0; index < order.Count; index++)
                {
                    var continuationName = QueryRequestExecution.ContinuationFieldName(index);
                    if (!renderedProjection.Contains(continuationName))
                        renderedProjection.Add(continuationName, 1);
                }
            }
            data.Add(new BsonDocument("$project", renderedProjection));
        }
        else if (order.Count != 0 || latestInternalFields.Count != 0)
        {
            var cleanup = new BsonDocument();
            foreach (var field in orderInternalFields.Concat(latestInternalFields).Distinct(StringComparer.Ordinal))
                cleanup.Add(field, 0);
            if (cleanup.ElementCount != 0)
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

    private static BsonValue FoldAscii(BsonValue input)
    {
        var expression = input;
        foreach (var character in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            expression = new BsonDocument("$replaceAll", new BsonDocument
            {
                { "input", expression },
                { "find", character.ToString() },
                { "replacement", char.ToLowerInvariant(character).ToString() }
            });
        }
        return expression;
    }

    private static BsonValue ElementMatch(Anchor anchor, BsonValue foldedElement, BsonValue foldedNeedle)
    {
        var needleLength = new BsonDocument("$strLenCP", foldedNeedle);
        var elementLength = new BsonDocument("$strLenCP", foldedElement);
        if (anchor == Anchor.Contains)
            return new BsonDocument("$gte", new BsonArray
            {
                new BsonDocument("$indexOfCP", new BsonArray { foldedElement, foldedNeedle }), 0
            });

        var suffix = new BsonDocument("$substrCP", new BsonArray
        {
            foldedElement,
            new BsonDocument("$subtract", new BsonArray { elementLength, needleLength }),
            needleLength
        });
        return new BsonDocument("$cond", new BsonArray
        {
            new BsonDocument("$eq", new BsonArray { needleLength, 0 }),
            true,
            new BsonDocument("$cond", new BsonArray
            {
                new BsonDocument("$gte", new BsonArray { elementLength, needleLength }),
                new BsonDocument("$eq", new BsonArray { suffix, foldedNeedle }),
                false
            })
        });
    }

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
