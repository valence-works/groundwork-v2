using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Groundwork.Query.Model;

public enum OrderDirection
{
    Ascending,
    Descending
}

public enum NullOrder
{
    ProviderDefault,
    First,
    Last
}

public sealed record OrderTerm
{
    public OrderTerm(ColumnRef column, OrderDirection direction = OrderDirection.Ascending, NullOrder nullOrder = NullOrder.ProviderDefault)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
        Direction = direction;
        NullOrder = nullOrder;
    }

    public ColumnRef Column { get; }
    public OrderDirection Direction { get; }
    public NullOrder NullOrder { get; }
}

public sealed record Projection
{
    private Projection(ImmutableArray<ColumnRef> columns, bool allColumns)
    {
        Columns = columns;
        AllColumns = allColumns;
    }

    public ImmutableArray<ColumnRef> Columns { get; }
    public bool AllColumns { get; }

    public static Projection All { get; } = new(ImmutableArray<ColumnRef>.Empty, true);

    public static Projection ColumnsOnly(params ColumnRef[] columns) =>
        new((columns ?? throw new ArgumentNullException(nameof(columns))).ToImmutableArray(), false);

    public static Projection ColumnsOnly(IEnumerable<ColumnRef> columns) =>
        new((columns ?? throw new ArgumentNullException(nameof(columns))).ToImmutableArray(), false);
}

public sealed record Paging
{
    private Paging(int? offset, int? limit, string? continuationToken)
    {
        if (offset is < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit));
        Offset = offset;
        Limit = limit;
        ContinuationToken = continuationToken;
    }

    public int? Offset { get; }
    public int? Limit { get; }
    public string? ContinuationToken { get; }

    public static Paging None { get; } = new(null, null, null);

    public static Paging OffsetLimit(int offset, int limit) => new(offset, limit, null);

    /// <summary>Starts a keyset page without an offset; a continuation can be supplied for later pages.</summary>
    public static Paging Keyset(int limit) => new(null, limit, null);

    public static Paging Continuation(string token, int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A continuation token cannot be blank.", nameof(token));
        return new(null, limit, token);
    }
}

public abstract record ResultShape
{
    public abstract bool IncludesTotalCount { get; }

    /// <summary>The maximum number of rows required to answer this result shape.</summary>
    public virtual int? MaxRows => null;

    /// <summary>Whether this result needs an explicit deterministic order.</summary>
    public virtual bool RequiresDeterministicOrder => false;

    public sealed record Rows : ResultShape
    {
        public static Rows Instance { get; } = new();
        public static Rows Default => Instance;
        public override bool IncludesTotalCount => false;
    }

    public sealed record TotalCount : ResultShape
    {
        public static TotalCount Instance { get; } = new();
        public static TotalCount Default => Instance;
        public override bool IncludesTotalCount => true;
    }

    public sealed record First : ResultShape
    {
        public static First Instance { get; } = new();
        public override bool IncludesTotalCount => false;
        public override int? MaxRows => 1;
        public override bool RequiresDeterministicOrder => true;
    }

    public sealed record FirstOrDefault : ResultShape
    {
        public static FirstOrDefault Instance { get; } = new();
        public override bool IncludesTotalCount => false;
        public override int? MaxRows => 1;
        public override bool RequiresDeterministicOrder => true;
    }

    public sealed record Single : ResultShape
    {
        public static Single Instance { get; } = new();
        public override bool IncludesTotalCount => false;
        public override int? MaxRows => 2;
        public override bool RequiresDeterministicOrder => false;
    }

    public sealed record SingleOrDefault : ResultShape
    {
        public static SingleOrDefault Instance { get; } = new();
        public override bool IncludesTotalCount => false;
        public override int? MaxRows => 2;
        public override bool RequiresDeterministicOrder => false;
    }
}

public sealed record LatestPerKey
{
    public LatestPerKey(ColumnRef key, ColumnRef timestamp)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        Timestamp = timestamp ?? throw new ArgumentNullException(nameof(timestamp));
        if (key.Table != timestamp.Table)
            throw new ArgumentException("Latest-per-key columns must belong to the same table.", nameof(timestamp));
    }

    public ColumnRef Key { get; }
    public ColumnRef Timestamp { get; }
}

public sealed record QueryRequest
{
    public QueryRequest(
        TableId table,
        Predicate where,
        ImmutableArray<OrderTerm> order,
        Projection projection,
        Paging paging,
        LatestPerKey? latestPerKey = null,
        ScanAcceptance? acceptedScan = null,
        bool distinct = false)
        : this(table, where, order, projection, paging, ResultShape.Rows.Instance, latestPerKey, acceptedScan, distinct)
    {
    }

    public QueryRequest(
        TableId table,
        Predicate where,
        ImmutableArray<OrderTerm> order,
        Projection projection,
        Paging paging,
        ResultShape result,
        LatestPerKey? latestPerKey = null,
        ScanAcceptance? acceptedScan = null,
        bool distinct = false)
    {
        Table = table ?? throw new ArgumentNullException(nameof(table));
        Where = PredicateNormalizer.Normalize(where ?? throw new ArgumentNullException(nameof(where)));
        Order = order.IsDefault ? ImmutableArray<OrderTerm>.Empty : order;
        if (Order.Any(term => term is null))
            throw new ArgumentException("Order terms cannot contain null references.", nameof(order));
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        Paging = paging ?? throw new ArgumentNullException(nameof(paging));
        Result = result ?? throw new ArgumentNullException(nameof(result));
        LatestPerKey = latestPerKey;
        AcceptedScan = acceptedScan;
        Distinct = distinct;
        CanonicalPredicate = PredicateCanonicalizer.ToCanonicalString(Where);
        ShapeFingerprint = QueryFingerprint.Create(this, includeResultShape: true);
        ContinuationFingerprint = QueryFingerprint.Create(this, includeResultShape: false, includePaging: false);
    }

    public TableId Table { get; }
    public Predicate Where { get; }
    public ImmutableArray<OrderTerm> Order { get; }
    public Projection Projection { get; }
    public Paging Paging { get; }
    public ResultShape Result { get; }
    public LatestPerKey? LatestPerKey { get; }
    public ScanAcceptance? AcceptedScan { get; }
    /// <summary>Whether duplicate projected values are removed before paging or cardinality checks.</summary>
    public bool Distinct { get; }
    public string CanonicalPredicate { get; internal init; }
    public string ShapeFingerprint { get; }
    public string ContinuationFingerprint { get; internal init; }

    internal string? ContinuationBindingDiscriminator { get; init; }
}

public sealed record QueryResult<T>
{
    public QueryResult(IReadOnlyList<T> rows, long? totalCount)
    {
        Rows = Array.AsReadOnly((rows ?? throw new ArgumentNullException(nameof(rows))).ToArray());
        TotalCount = totalCount;
    }

    public IReadOnlyList<T> Rows { get; }
    public long? TotalCount { get; }
}

public static class QueryFingerprint
{
    public static string Create(QueryRequest request, bool includeResultShape = true, bool includePaging = true)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        var builder = new StringBuilder();
        builder.Append("q1|table=").Append(PredicateCanonicalizer.Escape(request.Table.Value));
        builder.Append("|where=").Append(PredicateCanonicalizer.ToShapeString(request.Where));
        builder.Append("|order=");
        foreach (var term in request.Order)
            builder.Append(PredicateCanonicalizer.Column(term.Column)).Append(':').Append(term.Direction).Append(':').Append(term.NullOrder).Append(';');
        builder.Append("|projection=").Append(request.Projection.AllColumns ? "all" : string.Join(";", request.Projection.Columns.Select(PredicateCanonicalizer.Column)));
        builder.Append("|distinct=").Append(request.Distinct ? "true" : "false");
        if (includePaging)
            builder.Append("|paging=").Append(request.Paging.Offset?.ToString(CultureInfo.InvariantCulture) ?? "none").Append(':').Append(request.Paging.Limit?.ToString(CultureInfo.InvariantCulture) ?? "none").Append(':').Append(request.Paging.ContinuationToken is null ? "token" : "continuation");
        builder.Append("|latest=").Append(request.LatestPerKey is null ? "none" : PredicateCanonicalizer.Column(request.LatestPerKey.Key) + ":" + PredicateCanonicalizer.Column(request.LatestPerKey.Timestamp));
        builder.Append("|scan=").Append(request.AcceptedScan?.Allowed == true ? "allow" : "refuse");
        if (includeResultShape)
            builder.Append("|result=").Append(request.Result.GetType().Name);
        return Sha256(builder.ToString());
    }

    public static string CreateShapeFingerprint(QueryRequest request) => Create(request, includeResultShape: true);
    public static string CreateContinuationFingerprint(QueryRequest request) => Create(request, includeResultShape: false, includePaging: false);

    private static string Sha256(string value)
    {
        using var hash = SHA256.Create();
        return ToLowerHex(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (var item in bytes)
            builder.Append(item.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
