using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Query.Model.Tests;

public sealed class QueryModelContractTests
{
    private static readonly TableId Table = new("orders");
    private static readonly ColumnRef Name = new(Table, "name", QueryType.String, isNullable: true);
    private static readonly ColumnRef Amount = new(Table, "amount", QueryType.Decimal, isNullable: true, decimalPrecision: 18, decimalScale: 4);
    private static readonly ColumnRef Created = new(Table, "created", QueryType.DateTimeOffset, isNullable: false);
    private static readonly ColumnRef Id = new(Table, "id", QueryType.Guid, isNullable: false);
    private static readonly ColumnRef Tags = new(Table, "tags", QueryType.String, isNullable: true);
    private static readonly ColumnRef Binary = new(Table, "payload", QueryType.Binary, isNullable: false);

    [Fact]
    public void Query_constants_validate_types_scale_ranges_and_unicode()
    {
        Assert.Equal(QueryConstantKind.Null, QueryConstant.Of(Name, null).Kind);
        Assert.Equal("Alice", QueryConstant.Of(Name, "Alice").Value);
        Assert.Throws<ArgumentException>(() => QueryConstant.Of(Name, "\uD800"));
        Assert.Throws<ArgumentException>(() => QueryConstant.Of(Amount, 1.23456m));
        Assert.Throws<ArgumentException>(() => QueryConstant.Of(Amount, decimal.MaxValue));
        Assert.Throws<ArgumentException>(() => QueryConstant.Of(Name, 42));
        Assert.Throws<ArgumentException>(() => QueryConstant.Of(Created, DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Local)));
        Assert.Throws<ArgumentException>(() => QueryConstant.Of(Id, "not-a-guid"));
    }

    [Fact]
    public void All_issue_230_leaf_shapes_construct_and_normalize()
    {
        var elementSet = new ElementSetRef("order-tags", QueryType.String);
        var predicate = new Predicate.And(ImmutableArray.Create<Predicate>(
            new Predicate.Equal(Name, QueryConstant.Of(Name, "Straße")),
            new Predicate.In(Name, ImmutableArray.Create(
                QueryConstant.Of(Name, "I"),
                QueryConstant.Of(Name, "i"),
                QueryConstant.Of(Name, null))),
            new Predicate.Range(
                Amount,
                Bound.Inclusive(QueryConstant.Of(Amount, 1.2344m)),
                Bound.Exclusive(QueryConstant.Of(Amount, 999.9999m))),
            new Predicate.StartsWith(Name, "Al"),
            new Predicate.Substring(Name, "ice", Anchor.Contains),
            new Predicate.ElementOf(elementSet, ImmutableArray.Create(QueryConstant.Of(Name, "blue")), SetQuantifier.Any),
            new Predicate.ColumnCompare(Amount, CompareOp.GreaterThan, Amount),
            new Predicate.Not(new Predicate.Equal(Name, QueryConstant.Of(Name, "never"))),
            new Predicate.Or(ImmutableArray.Create<Predicate>(
                new Predicate.AlwaysTrue(),
                new Predicate.AlwaysFalse()))));

        var normalized = PredicateNormalizer.Normalize(predicate);

        Assert.NotNull(normalized);
        Assert.NotEmpty(PredicateCanonicalizer.ToCanonicalString(normalized));
    }

    [Fact]
    public void Normalization_folds_constants_fuses_membership_and_ranges_and_is_spelling_independent()
    {
        var a = QueryConstant.Of(Name, "a");
        var b = QueryConstant.Of(Name, "b");
        var equalA = new Predicate.Equal(Name, a);
        var equalB = new Predicate.Equal(Name, b);

        var membership = PredicateNormalizer.Normalize(new Predicate.Or(ImmutableArray.Create<Predicate>(equalB, equalA)));
        var expectedMembership = PredicateNormalizer.Normalize(new Predicate.In(Name, ImmutableArray.Create(a, b)));
        Assert.Equal(
            PredicateCanonicalizer.ToCanonicalString(expectedMembership),
            PredicateCanonicalizer.ToCanonicalString(membership));

        var fusedRange = PredicateNormalizer.Normalize(new Predicate.And(ImmutableArray.Create<Predicate>(
            new Predicate.Range(Amount, Bound.Inclusive(QueryConstant.Of(Amount, 1m)), null),
            new Predicate.Range(Amount, null, Bound.Exclusive(QueryConstant.Of(Amount, 2m))))));
        Assert.Contains("range", PredicateCanonicalizer.ToCanonicalString(fusedRange), StringComparison.Ordinal);

        Assert.IsType<Predicate.AlwaysFalse>(PredicateNormalizer.Normalize(new Predicate.In(Name, ImmutableArray<QueryConstant>.Empty)));
        Assert.IsType<Predicate.AlwaysTrue>(PredicateNormalizer.Normalize(new Predicate.Not(new Predicate.In(Name, ImmutableArray<QueryConstant>.Empty))));
        Assert.IsType<Predicate.AlwaysFalse>(PredicateNormalizer.Normalize(new Predicate.And(ImmutableArray.Create<Predicate>(equalA, equalB))));
    }

    [Fact]
    public void Cnf_budget_reports_the_exact_nested_offending_expression()
    {
        var nested = new Predicate.Or(Enumerable.Range(0, 5)
            .Select(_ => new Predicate.And(ImmutableArray.Create<Predicate>(
                new Predicate.Equal(Name, QueryConstant.Of(Name, "a")),
                new Predicate.Equal(Name, QueryConstant.Of(Name, "b")),
                new Predicate.Equal(Name, QueryConstant.Of(Name, "c")),
                new Predicate.Equal(Name, QueryConstant.Of(Name, "d")))))
            .Cast<Predicate>()
            .ToImmutableArray());
        var root = new Predicate.And(ImmutableArray.Create<Predicate>(
            new Predicate.Equal(Name, QueryConstant.Of(Name, "outside")),
            nested));

        var exception = Assert.Throws<QueryNormalizationException>(() => PredicateNormalizer.Normalize(root));

        Assert.Equal("GW-QUERY-020", exception.Code);
        Assert.Equal(PredicateCanonicalizer.ToCanonicalString(nested), exception.Subexpression);
        Assert.Equal("GW-QUERY-020: CNF budget exceeded (65 conjuncts, 4 disjuncts per conjunct; limits are 64/16). Offending sub-expression: " + exception.Subexpression, exception.Message);
    }

    [Fact]
    public void Element_set_names_are_escaped_without_delimiter_aliases()
    {
        var commaInName = new Predicate.ElementOf(
            new ElementSetRef("a,b", QueryType.String),
            ImmutableArray.Create(QueryConstant.Of(Name, "c")),
            SetQuantifier.Any);
        var commaInValue = new Predicate.ElementOf(
            new ElementSetRef("a", QueryType.String),
            ImmutableArray.Create(QueryConstant.Of(Name, "b,c")),
            SetQuantifier.Any);

        Assert.NotEqual(
            PredicateCanonicalizer.ToCanonicalString(commaInName),
            PredicateCanonicalizer.ToCanonicalString(commaInValue));
    }

    [Fact]
    public void Canonical_ordering_and_paging_fingerprints_are_culture_independent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var english = PredicateNormalizer.Normalize(new Predicate.ElementOf(
                new ElementSetRef("tags", QueryType.String),
                new[] { QueryConstant.Of(Name, "I"), QueryConstant.Of(Name, "i"), QueryConstant.Of(Name, "İ"), QueryConstant.Of(Name, "ı") },
                SetQuantifier.Any));
            var englishRequest = new QueryRequest(
                Table,
                english,
                ImmutableArray<OrderTerm>.Empty,
                Projection.All,
                Paging.OffsetLimit(12, 25));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = PredicateNormalizer.Normalize(new Predicate.ElementOf(
                new ElementSetRef("tags", QueryType.String),
                new[] { QueryConstant.Of(Name, "I"), QueryConstant.Of(Name, "i"), QueryConstant.Of(Name, "İ"), QueryConstant.Of(Name, "ı") },
                SetQuantifier.Any));
            var turkishRequest = new QueryRequest(
                Table,
                turkish,
                ImmutableArray<OrderTerm>.Empty,
                Projection.All,
                Paging.OffsetLimit(12, 25));

            Assert.Equal(PredicateCanonicalizer.ToCanonicalString(english), PredicateCanonicalizer.ToCanonicalString(turkish));
            Assert.Equal(englishRequest.ShapeFingerprint, turkishRequest.ShapeFingerprint);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Query_result_snapshots_rows_and_validates_input()
    {
        var source = new List<int> { 1 };
        var result = new QueryResult<int>(source, null);
        source[0] = 2;

        Assert.Equal(1, result.Rows[0]);
        Assert.Throws<ArgumentNullException>(() => new QueryResult<int>(null!, null));
    }

    [Fact]
    public void Binary_constants_and_results_remain_immutable_through_public_values()
    {
        var source = new byte[] { 1, 2, 3 };
        var constant = QueryConstant.Of(Binary, source);
        var request = new QueryRequest(
            Table,
            new Predicate.Equal(Binary, constant),
            ImmutableArray<OrderTerm>.Empty,
            Projection.All,
            Paging.None);
        var canonical = request.CanonicalPredicate;
        var fingerprint = request.ShapeFingerprint;

        source[0] = 9;
        ((byte[])constant.Value!)[0] = 8;

        Assert.Equal(canonical, request.CanonicalPredicate);
        Assert.Equal(fingerprint, request.ShapeFingerprint);
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])constant.Value!);

        var rows = new QueryResult<int>(new[] { 1 }, null);
        Assert.Throws<NotSupportedException>(() => ((IList<int>)rows.Rows)[0] = 9);
        Assert.Equal(1, rows.Rows[0]);
    }

    [Fact]
    public void Pinned_g2_corpus_has_exactly_300_deterministic_q1_shape_decisions()
    {
        var shapes = G2Q1Corpus.Shapes;

        Assert.Equal(G2Q1Corpus.ExpectedShapeCount, shapes.Count);
        Assert.Equal(Enumerable.Range(1, G2Q1Corpus.ExpectedShapeCount), shapes.Select(shape => shape.Number));
        var duplicateKeys = shapes.GroupBy(shape => shape.CanonicalInput, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key + " [" + string.Join(",", group.Select(shape => shape.Number)) + "]");
        Assert.True(shapes.Count == shapes.Select(shape => shape.CanonicalInput).Distinct(StringComparer.Ordinal).Count(), string.Join("; ", duplicateKeys));
        Assert.Equal(251, shapes.Count(shape => shape.Decision == Q1CorpusDecision.Normalize));
        Assert.Equal(49, shapes.Count(shape => shape.Decision == Q1CorpusDecision.Refuse));
        Assert.Equal(9, shapes.Count(shape => shape.PublicConstructionRejects));

        foreach (var shape in shapes)
        {
            if (shape.PublicConstructionRejects)
            {
                Assert.ThrowsAny<ArgumentException>(() => shape.Exercise());
                continue;
            }

            var first = shape.Exercise();
            var second = shape.Exercise();

            Assert.NotEmpty(first.Request.CanonicalPredicate);
            Assert.NotEmpty(first.Request.ShapeFingerprint);
            Assert.Equal(first.Request.CanonicalPredicate, second.Request.CanonicalPredicate);
            Assert.Equal(first.Request.ShapeFingerprint, second.Request.ShapeFingerprint);
            if (shape.Decision == Q1CorpusDecision.Refuse)
                Assert.Equal(shape.DecisionId, first.PlanningDecisionId);
            else
                Assert.Null(first.PlanningDecisionId);
        }
    }

    [Fact]
    public void Fingerprints_have_constant_holes_and_count_choice_is_not_continuation_binding()
    {
        static QueryRequest Request(string value, ResultShape result) => new(
            Table,
            new Predicate.Equal(Name, QueryConstant.Of(Name, value)),
            ImmutableArray.Create(new OrderTerm(Name, OrderDirection.Ascending)),
            Projection.ColumnsOnly(Name, Amount),
            Paging.OffsetLimit(0, 25),
            result);

        var rowsAlice = Request("Alice", ResultShape.Rows.Instance);
        var rowsBob = Request("Bob", ResultShape.Rows.Instance);
        var countedAlice = Request("Alice", ResultShape.TotalCount.Instance);

        Assert.Equal(rowsAlice.ShapeFingerprint, rowsBob.ShapeFingerprint);
        Assert.NotEqual(rowsAlice.CanonicalPredicate, rowsBob.CanonicalPredicate);
        Assert.NotEqual(rowsAlice.ShapeFingerprint, countedAlice.ShapeFingerprint);
        Assert.Equal(rowsAlice.ContinuationFingerprint, countedAlice.ContinuationFingerprint);
    }

    [Fact]
    public void Count_and_existence_probes_keep_the_continuation_window_and_binding()
    {
        var request = new QueryRequest(
            Table,
            new Predicate.Equal(Name, QueryConstant.Of(Name, "Alice")),
            ImmutableArray.Create(new OrderTerm(Created, OrderDirection.Ascending, NullOrder.Last)),
            Projection.All,
            Paging.Continuation("q1.token", 25),
            ResultShape.Rows.Instance);

        var probe = QueryRequestExecution.ForExistenceProbe(request);
        Assert.Equal("q1.token", probe.Paging.ContinuationToken);
        Assert.Equal(1, probe.Paging.Limit);
        Assert.False(probe.Result.IncludesTotalCount);
        Assert.Equal(request.ContinuationFingerprint, probe.ContinuationFingerprint);
        Assert.Equal(request.CanonicalPredicate, probe.CanonicalPredicate);

        var count = QueryRequestExecution.ForProviderCount(request);
        Assert.Equal("q1.token", count.Paging.ContinuationToken);
        Assert.Equal(1, count.Paging.Limit);
        Assert.True(count.Result.IncludesTotalCount);
        Assert.Equal(request.ContinuationFingerprint, count.ContinuationFingerprint);

        Assert.Equal(7, QueryRequestExecution.RequireTotalCount(request, 7));
        var refusal = Assert.Throws<InvalidOperationException>(() => QueryRequestExecution.RequireTotalCount(request, null));
        Assert.Contains("provider-side total count", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_result_has_no_count_semantics_and_total_is_nullable()
    {
        Assert.False(ResultShape.Rows.Instance.IncludesTotalCount);
        Assert.True(ResultShape.TotalCount.Instance.IncludesTotalCount);
        Assert.Null(new QueryResult<int>(Array.Empty<int>(), null).TotalCount);
        Assert.Equal(4L, new QueryResult<int>(Array.Empty<int>(), 4L).TotalCount);
    }

    [Fact]
    public void Model_assembly_has_no_provider_or_runtime_references()
    {
        var references = typeof(QueryRequest).Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();

        Assert.DoesNotContain(references, name => name.Contains("Mongo", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.Contains("Sql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.Contains("Npgsql", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.Contains("EntityFramework", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.Extensions", StringComparison.Ordinal));
        Assert.Equal(".NETStandard,Version=v2.0", typeof(QueryRequest).Assembly.GetCustomAttribute<System.Runtime.Versioning.TargetFrameworkAttribute>()?.FrameworkName);
    }
}
