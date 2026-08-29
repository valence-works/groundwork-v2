using System.Collections.Immutable;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Query.Model.Tests;

public sealed class JoinQueryModelTests
{
    private static readonly TableId Orders = new("orders");
    private static readonly TableId Customers = new("customers");
    private static readonly ColumnRef OrderId = new(Orders, "id", QueryType.Guid, isNullable: false);
    private static readonly ColumnRef OrderSequence = new(Orders, "order_id", QueryType.Int64, isNullable: false);
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
        Assert.Throws<ArgumentException>(() => new ReferenceJoin("customer", Customers,
            new JoinColumnPair[] { null! }));
        Assert.Throws<ArgumentException>(() => new ReferenceJoin("customer", Customers,
            [new JoinColumnPair(OrderCustomerId, CustomerId), null!]));
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
    public void Joined_alias_rules_do_not_change_single_table_duplicate_order_compatibility()
    {
        var id = new ColumnRef("id", QueryType.Int64, isNullable: false);
        var firstRequest = new QueryRequest(
            Orders,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(id, nullOrder: NullOrder.Last), new OrderTerm(id, nullOrder: NullOrder.Last)],
            Projection.ColumnsOnly(id),
            Paging.Keyset(1));
        IReadOnlyDictionary<string, object?>[] source =
        [
            new Dictionary<string, object?> { [id.Name] = 1L },
            new Dictionary<string, object?> { [id.Name] = 2L }
        ];

        var first = QueryResultMaterializer.Materialize(
            firstRequest,
            QueryRenderOptions.Default,
            source,
            sourceIncludesContinuation: false);
        var second = QueryResultMaterializer.Materialize(
            new QueryRequest(
                Orders,
                Predicate.AlwaysTrue.Instance,
                firstRequest.Order,
                firstRequest.Projection,
                Paging.Continuation(first.NextContinuationToken!, 1)),
            QueryRenderOptions.Default,
            source,
            sourceIncludesContinuation: false);

        Assert.Equal(1L, first.Rows.Single()[id.Name]);
        Assert.Equal(2L, second.Rows.Single()[id.Name]);
    }

    [Fact]
    public void Provider_identity_resolution_preserves_single_table_name_deduplication()
    {
        var unqualifiedId = new ColumnRef("id", QueryType.Guid, isNullable: false);
        var supplied = new QueryRenderOptions(tieBreakColumns: [unqualifiedId]);

        var resolved = supplied.WithIdentityTieBreaks([OrderId]);

        Assert.Same(unqualifiedId, Assert.Single(resolved.TieBreakColumns));
        Assert.Same(OrderId, Assert.Single(resolved.DrivingIdentityColumns));
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
        Assert.Throws<ArgumentException>(() => new QueryRequest(
            Orders,
            join,
            new Predicate.ElementOf(
                new ElementSetRef("tags", QueryType.String),
                [QueryConstant.Of("important")],
                SetQuantifier.Any),
            [],
            Projection.All,
            Paging.None));
    }

    [Fact]
    public void Portable_semantics_validate_both_join_key_sides()
    {
        var source = new ColumnRef(Orders, "score", QueryType.Double, isNullable: false);
        var target = new ColumnRef(Customers, "score", QueryType.Double, isNullable: false);
        var request = new QueryRequest(
            Orders,
            new ReferenceJoin("customer-score", Customers, [new JoinColumnPair(source, target)]),
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.None);

        var result = PortableQuerySemantics.Validate(request);

        Assert.False(result.IsPortable);
        Assert.Equal(2, result.Refusals.Count(refusal => refusal.Code == "GW-SEM-TYPE-006"));
        Assert.Contains(result.Refusals, refusal => refusal.Path == "join.columnPairs[0].source");
        Assert.Contains(result.Refusals, refusal => refusal.Path == "join.columnPairs[0].target");
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

    [Fact]
    public void Joined_effective_order_appends_both_qualified_identities_in_declared_order()
    {
        var request = Request(Join());
        var options = new QueryRenderOptions(tieBreakColumns: [OrderId]);

        var order = options.GetEffectiveOrder(request);

        Assert.Equal(
            [CustomerName, OrderId, CustomerId, CustomerRegion],
            order.Select(term => term.Column));
    }

    [Fact]
    public void Joined_effective_order_canonicalizes_supplied_target_keys_after_the_source_identity()
    {
        var request = Request(Join());
        var options = new QueryRenderOptions(
            tieBreakColumns: [CustomerRegion, OrderSequence, CustomerId]);

        var order = options.GetEffectiveOrder(request);

        Assert.Equal(
            [CustomerName, OrderSequence, CustomerId, CustomerRegion],
            order.Select(term => term.Column));
    }

    [Fact]
    public void Joined_effective_order_refuses_ambiguous_or_foreign_identity_tie_breaks()
    {
        var request = Request(Join());
        var unqualified = new ColumnRef("id", QueryType.Guid, isNullable: false);
        var foreign = new ColumnRef(new TableId("accounts"), "id", QueryType.Guid, isNullable: false);

        Assert.Throws<ArgumentException>(() =>
            new QueryRenderOptions(tieBreakColumns: [unqualified]).GetEffectiveOrder(request));
        Assert.Throws<ArgumentException>(() =>
            new QueryRenderOptions(tieBreakColumns: [foreign]).GetEffectiveOrder(request));
    }

    [Fact]
    public void Joined_composite_continuation_pages_deterministically_across_both_identities()
    {
        var options = CompositeOptions();
        var customerA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var customerB = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var source = new[]
        {
            Row(1, customerA, "eu", "Alice"),
            Row(2, customerA, "eu", "Alice"),
            Row(3, customerB, "us", "Alice"),
            Row(4, customerB, "us", "Bob")
        };
        var firstRequest = ContinuationPage(Paging.Keyset(2));

        var first = QueryResultMaterializer.Materialize(
            firstRequest,
            options,
            source,
            sourceIncludesContinuation: false);
        var cursor = QueryContinuationToken.Decode(first.NextContinuationToken!, firstRequest, options);
        var secondRequest = ContinuationPage(Paging.Continuation(first.NextContinuationToken!, 2));
        var second = QueryResultMaterializer.Materialize(
            secondRequest,
            options,
            source,
            sourceIncludesContinuation: false);

        Assert.Equal([1L, 2L], first.Rows.Select(row => row[OrderSequence.Name]));
        Assert.Equal([3L, 4L], second.Rows.Select(row => row[OrderSequence.Name]));
        Assert.Null(second.NextContinuationToken);
        Assert.Equal(
            ["string:416C696365", "int64:2", "guid:00000000-0000-0000-0000-000000000001", "string:6575"],
            cursor.Select(value => value.ToCanonicalString()));

    }

    [Fact]
    public void Joined_composite_continuation_keeps_same_named_identity_values_distinct()
    {
        var sourceIdA = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var sourceIdB = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var targetId = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var options = new QueryRenderOptions().WithIdentityTieBreaks([OrderId]);
        var firstRequest = ContinuationPage(Paging.Keyset(1));
        var source = new[]
        {
            SameNamedIdentityRow(1, sourceIdA, targetId),
            SameNamedIdentityRow(2, sourceIdB, targetId)
        };

        var first = QueryResultMaterializer.Materialize(
            firstRequest,
            options,
            source,
            sourceIncludesContinuation: false);
        var cursor = QueryContinuationToken.Decode(first.NextContinuationToken!, firstRequest, options);
        var second = QueryResultMaterializer.Materialize(
            ContinuationPage(Paging.Continuation(first.NextContinuationToken!, 1)),
            options,
            source,
            sourceIncludesContinuation: false);

        Assert.Equal(sourceIdA, cursor[1].Value);
        Assert.Equal(targetId, cursor[2].Value);
        Assert.Equal(1L, first.Rows.Single()[OrderSequence.Name]);
        Assert.Equal(2L, second.Rows.Single()[OrderSequence.Name]);
    }

    [Fact]
    public void Joined_composite_continuation_refuses_a_value_outside_its_typed_tuple_position()
    {
        var request = Request(Join());
        var options = CompositeOptions();

        var failure = Assert.Throws<ArgumentException>(() => QueryContinuationToken.Encode(
            request,
            options,
            CompositeValues(QueryConstant.Of("not-a-guid"))));

        Assert.Contains("effective order term", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Joined_composite_continuation_requires_the_driving_declared_identity()
    {
        var failure = Assert.Throws<ArgumentException>(() => QueryContinuationToken.Encode(
            Request(Join()),
            QueryRenderOptions.Default,
            [
                QueryConstant.Of(CustomerName, "Alice"),
                QueryConstant.Of(CustomerId, Guid.Empty),
                QueryConstant.Of(CustomerRegion, "eu")
            ]));

        Assert.Contains("driving identity", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Joined_composite_continuation_rejects_tie_breaks_without_a_complete_declaration()
    {
        var request = Request(Join());
        var options = new QueryRenderOptions(tieBreakColumns: [OrderCustomerId]);
        var order = options.GetEffectiveOrder(request);

        var failure = Assert.Throws<ArgumentException>(() => QueryContinuationToken.Encode(
            request,
            options,
            order.Select(term => QueryConstant.Of(term.Column, term.Column == OrderCustomerId
                ? Guid.Empty
                : term.Column == CustomerName ? "Alice"
                : term.Column == CustomerId ? Guid.Empty
                : "eu"))));

        Assert.Contains("complete declared driving identity", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Joined_composite_continuation_includes_every_declared_source_identity_component()
    {
        var request = Request(Join());
        var options = new QueryRenderOptions(tieBreakColumns: [OrderCustomerId])
            .WithIdentityTieBreaks([OrderCustomerId, OrderRegion]) with
            {
                TieBreakColumns = [OrderCustomerId]
            };
        var order = options.GetEffectiveOrder(request);

        Assert.Equal(
            [CustomerName, OrderCustomerId, OrderRegion, CustomerId, CustomerRegion],
            order.Select(term => term.Column));

        var token = QueryContinuationToken.Encode(
            request,
            options,
            order.Select(term => ContinuationValue(term.Column)));

        Assert.Equal(order.Length, QueryContinuationToken.Decode(token, request, options).Count);
    }

    [Fact]
    public void Joined_composite_continuation_rejects_a_partial_identity_against_the_resolved_source_key()
    {
        var request = Request(Join());
        var options = new QueryRenderOptions(drivingIdentityColumns: [OrderCustomerId])
            .WithIdentityTieBreaks([OrderCustomerId, OrderRegion]);
        var order = options.GetEffectiveOrder(request);

        var failure = Assert.Throws<ArgumentException>(() => QueryContinuationToken.Encode(
            request,
            options,
            order.Select(term => ContinuationValue(term.Column))));

        Assert.Contains("match the source schema identity", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Joined_composite_continuation_rejects_requested_identity_terms_out_of_declared_order()
    {
        var request = new QueryRequest(
            Orders,
            Join(),
            Predicate.AlwaysTrue.Instance,
            [
                new OrderTerm(OrderRegion, nullOrder: NullOrder.Last),
                new OrderTerm(OrderCustomerId, nullOrder: NullOrder.Last)
            ],
            Projection.ColumnsOnly(OrderRegion, OrderCustomerId),
            Paging.Keyset(25));
        var options = new QueryRenderOptions().WithIdentityTieBreaks([OrderCustomerId, OrderRegion]);

        var failure = Assert.Throws<ArgumentException>(() => QueryContinuationToken.Encode(
            request,
            options,
            options.GetEffectiveOrder(request).Select(term => ContinuationValue(term.Column))));

        Assert.Contains("declaration order", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Joined_composite_continuation_keeps_the_privileged_invocation_binding()
    {
        var options = CompositeOptions();
        var request = QueryRequestExecution.WithProviderPredicate(
            Request(Join()),
            Predicate.AlwaysTrue.Instance,
            "privileged-audit-binding-a");
        var token = QueryContinuationToken.Encode(
            request,
            options,
            CompositeValues());
        var otherInvocation = QueryRequestExecution.WithProviderPredicate(
            Request(Join()),
            Predicate.AlwaysTrue.Instance,
            "privileged-audit-binding-b");

        Assert.Equal(4, QueryContinuationToken.Decode(token, request, options).Count);
        Assert.Throws<FormatException>(() =>
            QueryContinuationToken.Decode(token, otherInvocation, options));
    }

    [Fact]
    public void Joined_composite_continuation_rejects_a_changed_reference_mapping()
    {
        var options = CompositeOptions();
        var request = Request(Join());
        var token = QueryContinuationToken.Encode(
            request,
            options,
            CompositeValues());
        var changed = Request(new ReferenceJoin(
            "customer",
            Customers,
            [
                new JoinColumnPair(OrderRegion, CustomerRegion),
                new JoinColumnPair(OrderCustomerId, CustomerId)
            ]));

        Assert.Throws<FormatException>(() => QueryContinuationToken.Decode(token, changed, options));
    }

    [Fact]
    public void Joined_provider_page_projects_same_named_tie_breaks_from_both_tables()
    {
        var request = Request(Join(), projection: Projection.ColumnsOnly(OrderId, CustomerName));
        var options = new QueryRenderOptions(tieBreakColumns: [OrderId]);

        var execution = QueryRequestExecution.ForProviderPage(request, options);

        Assert.Equal(
            new[] { OrderId, CustomerName, CustomerId, CustomerRegion },
            execution.Projection.Columns.ToArray());
    }

    private static ReferenceJoin Join() => new(
        "customer",
        Customers,
        [new JoinColumnPair(OrderCustomerId, CustomerId), new JoinColumnPair(OrderRegion, CustomerRegion)]);

    private static QueryRenderOptions CompositeOptions() =>
        new QueryRenderOptions(tieBreakColumns: [OrderSequence]).WithIdentityTieBreaks([OrderSequence]);

    private static QueryConstant[] CompositeValues(QueryConstant? targetId = null) =>
    [
        QueryConstant.Of(CustomerName, "Alice"),
        QueryConstant.Of(OrderSequence, 2L),
        targetId ?? QueryConstant.Of(CustomerId, Guid.Empty),
        QueryConstant.Of(CustomerRegion, "eu")
    ];

    private static QueryConstant ContinuationValue(ColumnRef column) => column.Type switch
    {
        QueryType.Guid => QueryConstant.Of(column, Guid.Empty),
        QueryType.Int64 => QueryConstant.Of(column, 1L),
        _ => QueryConstant.Of(column, column == CustomerName ? "Alice" : "eu")
    };

    private static QueryRequest ContinuationPage(Paging paging) => new(
        Orders,
        Join(),
        Predicate.AlwaysTrue.Instance,
        [new OrderTerm(CustomerName, nullOrder: NullOrder.Last)],
        Projection.ColumnsOnly(OrderSequence, CustomerName),
        paging);

    private static IReadOnlyDictionary<string, object?> SameNamedIdentityRow(
        long sequence,
        Guid sourceId,
        Guid targetId) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        [OrderSequence.Name] = sequence,
        [CustomerName.Name] = "Alice",
        [CustomerRegion.Name] = "eu",
        [OrderId.Name] = targetId,
        [QueryRequestExecution.ContinuationFieldName(0)] = "Alice",
        [QueryRequestExecution.ContinuationFieldName(1)] = sourceId,
        [QueryRequestExecution.ContinuationFieldName(2)] = targetId,
        [QueryRequestExecution.ContinuationFieldName(3)] = "eu"
    };

    private static IReadOnlyDictionary<string, object?> Row(
        long order,
        Guid customer,
        string region,
        string name) => new Dictionary<string, object?>(StringComparer.Ordinal)
    {
        [OrderSequence.Name] = order,
        [CustomerId.Name] = customer,
        [CustomerRegion.Name] = region,
        [CustomerName.Name] = name
    };

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
