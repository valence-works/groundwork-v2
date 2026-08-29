using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Query.Linq.Tests;

public sealed class TypedNavigationTests
{
    private static int navigationReads;

    private static readonly GwTableModel<OrderRow> Orders = new("orders",
    [
        new(nameof(OrderRow.Id), "id", QueryType.Int32, false),
        new(nameof(OrderRow.CustomerId), "customer_id", QueryType.Int32, false),
        new(nameof(OrderRow.Status), "status", QueryType.String, false)
    ]);

    private static readonly GwTableModel<CustomerRow> Customers = new("customers",
    [
        new(nameof(CustomerRow.Id), "id", QueryType.Int32, false),
        new(nameof(CustomerRow.Name), "name", QueryType.String, false),
        new(nameof(CustomerRow.OrderCount), "order_count", QueryType.Int32, false),
        new(nameof(CustomerRow.OptionalScore), "optional_score", QueryType.Int32, true),
        new(nameof(CustomerRow.CreatedAt), "created_at", QueryType.DateTimeOffset, false)
    ]);

    private static readonly ReferenceJoin CustomerJoin = new(
        "customer",
        Customers.Table,
        [new JoinColumnPair(Orders.Columns[nameof(OrderRow.CustomerId)], Customers.Columns[nameof(CustomerRow.Id)])]);

    private static readonly GwReference<OrderRow, CustomerRow> CustomerReference =
        Orders.Reference(order => order.Customer, Customers, CustomerJoin);

    [Fact]
    public void Declared_navigation_lowers_source_and_target_members_to_one_join()
    {
        navigationReads = 0;
        var request = new GwQueryDatabase().Table(Orders)
            .Join(CustomerReference)
            .Where(order => order.Status == "open" && order.Customer.Name == "Ada")
            .OrderBy(order => order.Id)
            .ThenBy(order => order.Customer.Name)
            .Select(order => new { OrderId = order.Id, CustomerId = order.Customer.Id })
            .Take(10)
            .ToQueryRequest();

        Assert.Same(CustomerJoin, request.Join);
        Assert.Equal([Orders.Table, Customers.Table], request.Order.Select(term => term.Column.Table));
        Assert.Equal([Orders.Table, Customers.Table], request.Projection.Columns.Select(column => column.Table));
        Assert.All(request.Projection.Columns, column => Assert.Equal("id", column.Name));
        Assert.Contains(Columns(request.Where), column => column.Table == Orders.Table && column.Name == "status");
        Assert.Contains(Columns(request.Where), column => column.Table == Customers.Table && column.Name == "name");
        Assert.Equal(10, request.Paging.Limit);
        Assert.Equal(0, navigationReads);
    }

    [Fact]
    public void Join_can_follow_source_composition_and_survives_every_request_terminal()
    {
        var query = new GwQueryDatabase().Table(Orders)
            .Where(order => order.Status == "open")
            .Join(CustomerReference)
            .Where(order => order.Customer.Name == "Ada")
            .OrderBy(order => order.Id)
            .Distinct();

        var requests = new[]
        {
            query.ToQueryRequest(),
            query.ToList().Request,
            query.Count().Request,
            query.Any().Request,
            query.First().Request,
            query.FirstOrDefault().Request,
            query.Single().Request,
            query.SingleOrDefault().Request,
            query.Sum(order => order.Customer.OrderCount).Request,
            query.Min(order => order.Customer.Name).Request,
            query.Max(order => order.Customer.CreatedAt).Request
        };

        Assert.All(requests, request => Assert.Same(CustomerJoin, request.Join));
        Assert.All(requests, request => Assert.True(request.Distinct));
        Assert.Equal(Customers.Table, ((ResultShape.Sum)requests[^3].Result).Column.Table);
        Assert.Equal(Customers.Table, ((ResultShape.Min)requests[^2].Result).Column.Table);
        Assert.Equal(Customers.Table, ((ResultShape.Max)requests[^1].Result).Column.Table);
    }

    [Fact]
    public void Target_latest_per_and_paging_keep_the_declared_join()
    {
        var request = new GwQueryDatabase().Table(Orders)
            .Join(CustomerReference)
            .LatestPer(order => order.Customer.Id, order => order.Customer.CreatedAt)
            .OrderBy(order => order.Id)
            .Skip(2)
            .Take(5)
            .ToQueryRequest();

        Assert.Same(CustomerJoin, request.Join);
        Assert.Equal(Customers.Table, request.LatestPerKey!.Key.Table);
        Assert.Equal(Customers.Table, request.LatestPerKey.Timestamp.Table);
        Assert.Equal(2, request.Paging.Offset);
        Assert.Equal(5, request.Paging.Limit);
    }

    [Fact]
    public void Declared_target_nullable_and_date_wrappers_lower_to_target_columns()
    {
        QueryRequest Request(System.Linq.Expressions.Expression<Func<OrderRow, bool>> predicate) =>
            new GwQueryDatabase().Table(Orders)
                .Join(CustomerReference)
                .Where(predicate)
                .ToQueryRequest();

        var requests = new[]
        {
            Request(order => order.Customer.OptionalScore!.Value >= 3),
            Request(order => order.Customer.CreatedAt.Year == 2026),
            Request(order => order.Customer.CreatedAt.Date == new DateTime(2026, 1, 1))
        };

        Assert.Equal("optional_score", Assert.Single(Columns(requests[0].Where)).Name);
        Assert.All(requests, request =>
            Assert.All(Columns(request.Where), column => Assert.Equal(Customers.Table, column.Table)));
        Assert.All(requests[1..], request =>
            Assert.Equal("created_at", Assert.Single(Columns(request.Where)).Name));
    }

    [Fact]
    public void Typed_navigation_preserves_the_join_shape_fingerprint_contract()
    {
        QueryRequest Request(string name, GwReference<OrderRow, CustomerRow> reference) =>
            new GwQueryDatabase().Table(Orders)
                .Join(reference)
                .Where(order => order.Customer.Name == name)
                .ToQueryRequest();

        var renamedJoin = new ReferenceJoin("buyer", Customers.Table,
        [
            new JoinColumnPair(Orders.Columns[nameof(OrderRow.CustomerId)], Customers.Columns[nameof(CustomerRow.Id)])
        ]);
        var renamedReference = Orders.Reference(order => order.Customer, Customers, renamedJoin);

        Assert.Equal(Request("Ada", CustomerReference).ShapeFingerprint, Request("Grace", CustomerReference).ShapeFingerprint);
        Assert.NotEqual(Request("Ada", CustomerReference).ShapeFingerprint, Request("Ada", renamedReference).ShapeFingerprint);
    }

    [Fact]
    public void Navigation_without_its_declared_join_keeps_refusing()
    {
        var exception = Assert.Throws<LinqTranslationException>(() => new GwQueryDatabase().Table(Orders)
            .Where(order => order.Customer.Name == "Ada"));

        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal("GW-LINQ-104", diagnostic.Code);
        Assert.Contains(".Join(reference)", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Joined_query_refuses_an_undeclared_or_deeper_navigation()
    {
        var query = new GwQueryDatabase().Table(Orders).Join(CustomerReference);

        var other = Assert.Throws<LinqTranslationException>(() => query.Where(order => order.OtherCustomer.Name == "Ada"));
        var deeper = Assert.Throws<LinqTranslationException>(() => query.Where(order => order.Customer.Profile.Code == "vip"));

        Assert.Equal("GW-LINQ-104", Assert.Single(other.Diagnostics).Code);
        Assert.Equal("GW-LINQ-104", Assert.Single(deeper.Diagnostics).Code);
    }

    [Fact]
    public void Joined_query_refuses_comparing_the_reference_object_itself()
    {
        var exception = Assert.Throws<LinqTranslationException>(() => new GwQueryDatabase().Table(Orders)
            .Join(CustomerReference)
            .Where(order => order.Customer == null!));

        Assert.Equal("GW-LINQ-104", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public void A_query_cannot_activate_an_arbitrary_second_reference()
    {
        var second = Orders.Reference(order => order.OtherCustomer, Customers,
            new ReferenceJoin("other_customer", Customers.Table,
            [new JoinColumnPair(Orders.Columns[nameof(OrderRow.CustomerId)], Customers.Columns[nameof(CustomerRow.Id)])]));

        var exception = Assert.Throws<LinqTranslationException>(() => new GwQueryDatabase().Table(Orders)
            .Join(CustomerReference)
            .Join(second));

        Assert.Equal("GW-LINQ-104", Assert.Single(exception.Diagnostics).Code);
    }

    [Fact]
    public void Reference_binding_rejects_mismatched_source_or_target_metadata()
    {
        var wrongSource = new GwTableModel<OrderRow>("other_orders",
        [
            new(nameof(OrderRow.Id), "id", QueryType.Int32, false),
            new(nameof(OrderRow.CustomerId), "customer_id", QueryType.Int32, false)
        ]);
        var wrongTarget = new GwTableModel<CustomerRow>("other_customers",
        [
            new(nameof(CustomerRow.Id), "id", QueryType.Int32, false),
            new(nameof(CustomerRow.Name), "name", QueryType.String, false)
        ]);

        Assert.Throws<ArgumentException>(() => wrongSource.Reference(order => order.Customer, Customers, CustomerJoin));
        Assert.Throws<ArgumentException>(() => Orders.Reference(order => order.Customer, wrongTarget, CustomerJoin));

        var objectTarget = new GwTableModel<object>("customers",
        [
            new(nameof(CustomerRow.Id), "id", QueryType.Int32, false),
            new(nameof(CustomerRow.Name), "name", QueryType.String, false)
        ]);
        var objectJoin = new ReferenceJoin("customer", objectTarget.Table,
        [
            new JoinColumnPair(Orders.Columns[nameof(OrderRow.CustomerId)], objectTarget.Columns[nameof(CustomerRow.Id)])
        ]);
        Assert.Throws<ArgumentException>(() => Orders.Reference<object>(order => order.Customer, objectTarget, objectJoin));
    }

    private static IEnumerable<ColumnRef> Columns(Predicate predicate) => predicate switch
    {
        Predicate.Equal equal => [equal.Column],
        Predicate.In membership => [membership.Column],
        Predicate.Range range => [range.Column],
        Predicate.ColumnCompare comparison => [comparison.Left, comparison.Right],
        Predicate.Not not => Columns(not.Inner),
        Predicate.And and => and.Terms.SelectMany(Columns),
        Predicate.Or or => or.Terms.SelectMany(Columns),
        _ => []
    };

    private sealed class OrderRow
    {
        private CustomerRow customer = new();

        public int Id { get; init; }
        public int CustomerId { get; init; }
        public string Status { get; init; } = string.Empty;
        public CustomerRow Customer
        {
            get
            {
                Interlocked.Increment(ref navigationReads);
                return customer;
            }
            init => customer = value;
        }
        public CustomerRow OtherCustomer { get; init; } = new();
    }

    private sealed class CustomerRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public int OrderCount { get; init; }
        public int? OptionalScore { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public CustomerProfile Profile { get; init; } = new();
    }

    private sealed class CustomerProfile
    {
        public string Code { get; init; } = string.Empty;
    }
}
