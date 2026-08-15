namespace Groundwork.Query.Model;

/// <summary>Pure, provider-neutral prefix-key encoding used by native renderers.</summary>
public static class QuerySearchKeys
{
    public static string Encode(string value, QuerySearchKeyPolicy policy)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        return policy == QuerySearchKeyPolicy.Ordinal
            ? ValidateOrdinal(value)
            : Groundwork.Kernel.PortableSearchKeyEncoding.Create(value, ToPortablePolicy(policy));
    }

    public static string? Successor(string encoded, QuerySearchKeyPolicy policy)
    {
        if (encoded == null) throw new ArgumentNullException(nameof(encoded));
        return policy == QuerySearchKeyPolicy.Ordinal
            ? Groundwork.Kernel.PortableSearchKeyEncoding.CreateOrdinalSuccessor(encoded)
            : Groundwork.Kernel.PortableSearchKeyEncoding.CreateSuccessor(encoded, ToPortablePolicy(policy));
    }

    private static string ValidateOrdinal(string value)
    {
        Groundwork.Kernel.PortableSearchKeyEncoding.ValidateWellFormed(
            value, "Ordinal strings must be well-formed UTF-16.");
        return value;
    }

    private static Groundwork.Kernel.PortableSearchKeyPolicy ToPortablePolicy(QuerySearchKeyPolicy policy) => policy switch
    {
        QuerySearchKeyPolicy.Ordinal => Groundwork.Kernel.PortableSearchKeyPolicy.Ordinal,
        QuerySearchKeyPolicy.AsciiIgnoreCase => Groundwork.Kernel.PortableSearchKeyPolicy.AsciiIgnoreCase,
        QuerySearchKeyPolicy.UnicodeOrdinalIgnoreCase => Groundwork.Kernel.PortableSearchKeyPolicy.UnicodeOrdinalIgnoreCase,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
    };
}

/// <summary>Rewrites logical prefix predicates to exact physical ranges before native validation.</summary>
public static class QuerySearchKeyRewriter
{
    public static QueryRequest Rewrite(QueryRequest request, IReadOnlyDictionary<string, QuerySearchKeyColumn> mappings)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (mappings == null) throw new ArgumentNullException(nameof(mappings));
        var where = RewritePredicate(request.Where, request.Table, mappings);
        return ReferenceEquals(where, request.Where)
            ? request
            : new QueryRequest(request.Table, where, request.Order, request.Projection, request.Paging,
                request.Result, request.LatestPerKey, request.AcceptedScan);
    }

    private static Predicate RewritePredicate(
        Predicate predicate,
        TableId table,
        IReadOnlyDictionary<string, QuerySearchKeyColumn> mappings)
    {
        switch (predicate)
        {
            case Predicate.StartsWith starts:
                if (!mappings.TryGetValue(starts.Column.Name, out var mapping))
                    return starts;
                var source = new ColumnRef(
                    starts.Column.Table,
                    starts.Column.Name,
                    starts.Column.Type,
                    starts.Column.IsNullable,
                    starts.Column.MaxLength,
                    starts.Column.DecimalPrecision,
                    starts.Column.DecimalScale,
                    QueryStringComparisonPolicy.Ordinal);
                if (starts.Prefix.Length == 0)
                    return new Predicate.Not(new Predicate.Equal(
                        source,
                        QueryConstant.Of(source, null)));
                var physical = new ColumnRef(
                    table,
                    mapping.PhysicalColumn,
                    QueryType.String,
                    isNullable: true,
                    maxLength: mapping.MaxLength);
                var low = QueryConstant.Of(physical, QuerySearchKeys.Encode(starts.Prefix, mapping.Policy));
                var high = QuerySearchKeys.Successor((string)low.Value!, mapping.Policy);
                return new Predicate.Range(
                    physical,
                    Bound.Inclusive(low),
                    high is null ? null : Bound.Exclusive(QueryConstant.Of(physical, high)));
            case Predicate.And and:
                return new Predicate.And(and.Terms.Select(term => RewritePredicate(term, table, mappings)));
            case Predicate.Or or:
                return new Predicate.Or(or.Terms.Select(term => RewritePredicate(term, table, mappings)));
            case Predicate.Not not:
                return new Predicate.Not(RewritePredicate(not.Inner, table, mappings));
            default:
                return predicate;
        }
    }
}
