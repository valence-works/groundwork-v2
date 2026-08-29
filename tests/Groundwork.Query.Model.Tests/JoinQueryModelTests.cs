using System.Collections.Immutable;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Query.Model.Tests;

public sealed class JoinQueryModelTests
{
    private static readonly TableId Orders = new("orders");
    private static readonly TableId Customers = new("customers");
    private static readonly ColumnRef OrderCustomerId = new(Orders, "customer_id", QueryType.Guid, isNullable: false);
    private static readonly ColumnRef OrderRegion = new(Orders, "customer_region", QueryType.String, isNullable: false);
    private static readonly ColumnRef CustomerId = new(Customers, "id", QueryType.Guid, isNullable: false);
    private static readonly ColumnRef CustomerRegion = new(Customers, "region", QueryType.String, isNullable: false);
    private static readonly ColumnRef CustomerName = new(Customers, "name", QueryType.String, isNullable: false);

    [Fact]
    public void Reference_join_snapshots_an_ordered_declared_key_mapping()
    {
        var pairs = new List<JoinColumnPair>
        {
            new(OrderCustomerId, CustomerId),
            new(OrderRegion, CustomerRegion)
        };

        var join = new ReferenceJoin("customer", Customers, pairs);
        pairs.Clear();

        Assert.Equal("customer", join.ReferenceName);
        Assert.Equal(Orders, join.SourceTable);
        Assert.Equal(Customers, join.TargetTable);
        Assert.Equal([OrderCustomerId, OrderRegion], join.ColumnPairs.Select(pair => pair.Source));
        Assert.Equal([CustomerId, CustomerRegion], join.ColumnPairs.Select(pair => pair.Target));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<JoinColumnPair>)join.ColumnPairs)[0] = new JoinColumnPair(OrderCustomerId, CustomerId));
    }

    [Fact]
    public void Reference_join_refuses_any_shape_other_than_one_qualified_compatible_mapping()
    {
        var mismatchedType = new ColumnRef(Customers, "id", QueryType.Int64, isNullable: false);
        var otherTarget = new ColumnRef(new TableId("accounts"), "id", QueryType.Guid, isNullable: false);
        var unqualifiedSource = new ColumnRef("customer_id", QueryType.Guid, isNullable: false);

        Assert.Throws<ArgumentException>(() => new ReferenceJoin(" ", Customers,
            [new JoinColumnPair(OrderCustomerId, CustomerId)]));
        Assert.Throws<ArgumentException>(() => new ReferenceJoin("customer", TableId.Empty,
            [new JoinColumnPair(OrderCustomerId, CustomerId)]));
        Assert.Throws<ArgumentException>(() => new ReferenceJoin("customer", Customers, []));
        Assert.Throws<ArgumentException>(() => new ReferenceJoin("customer", Customers,
            [new JoinColumnPair(OrderCustomerId, otherTarget)]));
        Assert.Throws<ArgumentException>(() => new ReferenceJoin("customer", Customers,
            [new JoinColumnPair(unqualifiedSource, CustomerId)]));
        Assert.Throws<ArgumentException>(() => new JoinColumnPair(OrderCustomerId, mismatchedType));
        Assert.Throws<ArgumentException>(() => new ReferenceJoin("parent-order", Orders,
            [new JoinColumnPair(OrderCustomerId, new ColumnRef(Orders, "parent_id", QueryType.Guid, isNullable: false))]));
        Assert.Throws<ArgumentException>(() => new ReferenceJoin("customer", Customers,
        [
            new JoinColumnPair(OrderCustomerId, CustomerId),
            new JoinColumnPair(OrderCustomerId, CustomerRegion)
        ]));
    }

    [Fact]
    public void Joined_requests_require_every_query_column_to_name_one_join_side()
    {
        var join = Join();
        var unqualifiedName = new ColumnRef("name", QueryType.String, isNullable: false);
        var thirdTableName = new ColumnRef(new TableId("accounts"), "name", QueryType.String, isNullable: false);

        Assert.Throws<ArgumentException>(() => Request(join, projection: Projection.ColumnsOnly(unqualifiedName)));
        Assert.Throws<ArgumentException>(() => Request(join, projection: Projection.ColumnsOnly(thirdTableName)));
        Assert.Throws<ArgumentException>(() => new QueryRequest(
            Customers,
            join,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.None));

        var request = Request(join, projection: Projection.ColumnsOnly(OrderCustomerId, CustomerName));

        Assert.Same(join, request.Join);
        Assert.Equal(new[] { OrderCustomerId, CustomerName }, request.Projection.Columns.ToArray());
    }

    [Fact]
    public void Existing_single_table_constructors_remain_unqualified_and_join_free()
    {
        var legacyColumn = new ColumnRef("customer_id", QueryType.Guid, isNullable: false);

        var request = new QueryRequest(
            Orders,
            new Predicate.Equal(legacyColumn, QueryConstant.Of(legacyColumn, Guid.Empty)),
            [],
            Projection.ColumnsOnly(legacyColumn),
            Paging.None);

        Assert.Null(request.Join);
        Assert.Equal(TableId.Empty, request.Projection.Columns[0].Table);
    }

    [Fact]
    public void Joined_request_qualification_covers_predicates_order_reductions_and_latest_per_key()
    {
        var join = Join();
        var accounts = new TableId("accounts");
        var foreignName = new ColumnRef(accounts, "name", QueryType.String, isNullable: false);
        var foreignTimestamp = new ColumnRef(accounts, "created", QueryType.DateTimeOffset, isNullable: false);

        Assert.Throws<ArgumentException>(() => new QueryRequest(
            Orders,
            join,
            new Predicate.Equal(foreignName, QueryConstant.Of(foreignName, "Alice")),
            [],
            Projection.All,
            Paging.None));
        Assert.Throws<ArgumentException>(() => new QueryRequest(
            Orders,
            join,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(foreignName, nullOrder: NullOrder.Last)],
            Projection.All,
            Paging.None));
        Assert.Throws<ArgumentException>(() => new QueryRequest(
            Orders,
            join,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.None,
            new ResultShape.Min(foreignName)));
        Assert.Throws<ArgumentException>(() => new QueryRequest(
            Orders,
            join,
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.None,
            latestPerKey: new LatestPerKey(foreignName, foreignTimestamp)));
    }

    [Fact]
    public void Join_shape_fingerprints_are_qualified_value_free_and_mapping_order_sensitive()
    {
        var first = Request(Join(), "Alice");
        var otherValue = Request(Join(), "Bob");
        var otherReference = Request(new ReferenceJoin(
            "billing-customer",
            Customers,
            [new JoinColumnPair(OrderCustomerId, CustomerId), new JoinColumnPair(OrderRegion, CustomerRegion)]), "Alice");
        var reversedMapping = Request(new ReferenceJoin(
            "customer",
            Customers,
            [new JoinColumnPair(OrderRegion, CustomerRegion), new JoinColumnPair(OrderCustomerId, CustomerId)]), "Alice");
        var sourceProjection = Request(Join(), "Alice", Projection.ColumnsOnly(OrderRegion));
        var targetProjection = Request(Join(), "Alice", Projection.ColumnsOnly(CustomerRegion));

        Assert.Equal("q3", QueryFingerprint.QueryShapeVersion);
        Assert.Equal(first.ShapeFingerprint, otherValue.ShapeFingerprint);
        Assert.NotEqual(first.CanonicalPredicate, otherValue.CanonicalPredicate);
        Assert.NotEqual(first.ShapeFingerprint, otherReference.ShapeFingerprint);
        Assert.NotEqual(first.ShapeFingerprint, reversedMapping.ShapeFingerprint);
        Assert.NotEqual(sourceProjection.ShapeFingerprint, targetProjection.ShapeFingerprint);
        Assert.NotEqual(sourceProjection.ContinuationFingerprint, targetProjection.ContinuationFingerprint);
        Assert.Contains("column(0063007500730074006F006D006500720073,", first.CanonicalPredicate, StringComparison.Ordinal);
        Assert.Equal("ec0b3527a4766e7ed6b800f55730d8a122c2a43f3d942347a4a3e34e845689ce", first.ShapeFingerprint);
        Assert.Equal("91703516fc6683ca7f8a29aabf0ac2968d88f9c85ce70957415108954860c867", first.ContinuationFingerprint);
    }

    [Fact]
    public void Execution_request_transformations_preserve_the_join_binding()
    {
        var request = new QueryRequest(
            Orders,
            Join(),
            new Predicate.Equal(CustomerName, QueryConstant.Of(CustomerName, "Alice")),
            [new OrderTerm(CustomerName, nullOrder: NullOrder.Last)],
            Projection.ColumnsOnly(CustomerName),
            Paging.OffsetLimit(5, 20),
            ResultShape.First.Instance);

        var transformed = new[]
        {
            QueryRequestExecution.ForResultShape(request),
            QueryRequestExecution.WithProviderPredicate(request, Predicate.AlwaysTrue.Instance),
            QueryRequestExecution.ForProviderCount(request),
            QueryRequestExecution.ForExistenceProbe(request),
            QueryRequestExecution.WithProjection(request, Projection.ColumnsOnly(CustomerId)),
            QueryRequestExecution.ForPage(request, QueryRenderOptions.Default),
            QuerySearchKeyRewriter.Rewrite(request, new Dictionary<string, QuerySearchKeyColumn>())
        };

        Assert.All(transformed, item => Assert.Same(request.Join, item.Join));
        Assert.All(transformed, item => Assert.Equal(request.ContinuationFingerprint, item.ContinuationFingerprint));
    }

    [Fact]
    public void Driving_table_search_key_rewrites_do_not_capture_same_named_target_columns()
    {
        var sourceName = new ColumnRef(Orders, "name", QueryType.String, isNullable: false);
        var mappings = new Dictionary<string, QuerySearchKeyColumn>
        {
            ["name"] = new("name", "name_sort", QuerySearchKeyPolicy.Ordinal, maxLength: 100,
                orderByPhysicalColumn: true)
        };
        var targetOnly = new QueryRequest(
            Orders,
            Join(),
            new Predicate.StartsWith(CustomerName, "A"),
            [new OrderTerm(CustomerName, nullOrder: NullOrder.Last)],
            Projection.ColumnsOnly(CustomerName),
            Paging.None);
        var request = new QueryRequest(
            Orders,
            Join(),
            Predicate.AlwaysTrue.Instance,
            [
                new OrderTerm(sourceName, nullOrder: NullOrder.Last),
                new OrderTerm(CustomerName, nullOrder: NullOrder.Last)
            ],
            Projection.ColumnsOnly(sourceName, CustomerName),
            Paging.None);

        var untouched = QuerySearchKeyRewriter.Rewrite(targetOnly, mappings);
        var rewritten = QuerySearchKeyRewriter.Rewrite(request, mappings);

        Assert.Same(targetOnly, untouched);
        Assert.Equal(new[] { "name_sort", "name" }, rewritten.Order.Select(term => term.Column.Name));
        Assert.Equal(new[] { Orders, Customers }, rewritten.Order.Select(term => term.Column.Table));
    }

    private static ReferenceJoin Join() => new(
        "customer",
        Customers,
        [new JoinColumnPair(OrderCustomerId, CustomerId), new JoinColumnPair(OrderRegion, CustomerRegion)]);

    private static QueryRequest Request(
        ReferenceJoin join,
        string name = "Alice",
        Projection? projection = null) => new(
        Orders,
        join,
        new Predicate.Equal(CustomerName, QueryConstant.Of(CustomerName, name)),
        [new OrderTerm(CustomerName, nullOrder: NullOrder.Last)],
        projection ?? Projection.ColumnsOnly(OrderCustomerId, CustomerName),
        Paging.Keyset(25));
}
