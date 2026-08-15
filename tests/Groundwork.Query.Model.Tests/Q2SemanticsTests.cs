using System.Collections.Immutable;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Query.Model.Tests;

public sealed class Q2SemanticsTests
{
    private static readonly TableId Table = new("q2-row");
    private static readonly ColumnRef Text = new(Table, "text", QueryType.String);
    private static readonly ColumnRef Number = new(Table, "number", QueryType.Int32);
    private static readonly ColumnRef Boolean = new(Table, "flag", QueryType.Boolean);
    private static readonly ColumnRef Decimal = new(Table, "amount", QueryType.Decimal, decimalPrecision: 18, decimalScale: 4);
    private static readonly ColumnRef Instant = new(Table, "instant", QueryType.DateTimeOffset);
    private static readonly ColumnRef Guid = new(Table, "guid", QueryType.Guid);
    private static readonly ColumnRef Binary = new(Table, "binary", QueryType.Binary);
    private static readonly ColumnRef Double = new(Table, "ratio", QueryType.Double);
    private static readonly ColumnRef UnshapedDecimal = new(Table, "unshapedAmount", QueryType.Decimal, decimalPrecision: 19, decimalScale: 4);

    [Fact]
    public void Leaf_complements_are_total_and_treat_missing_as_explicit_null()
    {
        var equal = new Predicate.Equal(Number, QueryConstant.Of(Number, 5));
        var equalNull = new Predicate.Equal(Number, QueryConstant.Of(Number, null));
        var membership = new Predicate.In(Number, [QueryConstant.Of(Number, 5)]);
        var nullMembership = new Predicate.In(Number, [QueryConstant.Of(Number, null)]);
        var emptyMembership = new Predicate.In(Number, ImmutableArray<QueryConstant>.Empty);
        var range = new Predicate.Range(Number, Bound.Inclusive(QueryConstant.Of(Number, 1)), Bound.Exclusive(QueryConstant.Of(Number, 10)));
        var row = new Dictionary<string, object?> { [Number.Name] = null };
        var missing = new Dictionary<string, object?>();

        Assert.False(PortableQuerySemantics.Evaluate(equal, row));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(equal), row));
        Assert.True(PortableQuerySemantics.Evaluate(equalNull, row));
        Assert.False(PortableQuerySemantics.Evaluate(new Predicate.Not(equalNull), row));
        Assert.False(PortableQuerySemantics.Evaluate(membership, row));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(membership), row));
        Assert.True(PortableQuerySemantics.Evaluate(nullMembership, row));
        Assert.False(PortableQuerySemantics.Evaluate(new Predicate.Not(nullMembership), row));
        Assert.False(PortableQuerySemantics.Evaluate(emptyMembership, row));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(emptyMembership), row));
        Assert.False(PortableQuerySemantics.Evaluate(range, row));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(range), row));
        Assert.True(PortableQuerySemantics.Evaluate(equalNull, missing));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(equal), missing));
    }

    [Fact]
    public void Range_evaluation_distinguishes_lower_and_upper_bounds_and_complements()
    {
        var range = new Predicate.Range(
            Number,
            Bound.Inclusive(QueryConstant.Of(Number, 1)),
            Bound.Exclusive(QueryConstant.Of(Number, 10)));

        Assert.True(PortableQuerySemantics.Evaluate(range, new Dictionary<string, object?> { [Number.Name] = 5 }));
        Assert.False(PortableQuerySemantics.Evaluate(range, new Dictionary<string, object?> { [Number.Name] = 20 }));
        Assert.False(PortableQuerySemantics.Evaluate(range, new Dictionary<string, object?> { [Number.Name] = null }));
        Assert.False(PortableQuerySemantics.Evaluate(new Predicate.Not(range), new Dictionary<string, object?> { [Number.Name] = 5 }));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(range), new Dictionary<string, object?> { [Number.Name] = 20 }));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(range), new Dictionary<string, object?> { [Number.Name] = null }));
    }

    [Fact]
    public void ElementOf_complement_includes_an_empty_owner()
    {
        var any = new Predicate.ElementOf(
            new ElementSetRef("tags", QueryType.String),
            [QueryConstant.Of("red")],
            SetQuantifier.Any);
        var complement = new Predicate.Not(any);

        Assert.True(PortableQuerySemantics.Evaluate(any, new Dictionary<string, object?> { ["tags"] = new[] { "red", "blue" } }));
        Assert.False(PortableQuerySemantics.Evaluate(any, new Dictionary<string, object?> { ["tags"] = Array.Empty<string>() }));
        Assert.False(PortableQuerySemantics.Evaluate(any, new Dictionary<string, object?>()));
        Assert.False(PortableQuerySemantics.Evaluate(any, new Dictionary<string, object?> { ["tags"] = null }));
        Assert.False(PortableQuerySemantics.Evaluate(any, new Dictionary<string, object?> { ["tags"] = "red" }));
        Assert.True(PortableQuerySemantics.Evaluate(complement, new Dictionary<string, object?> { ["tags"] = Array.Empty<string>() }));
        Assert.True(PortableQuerySemantics.Evaluate(complement, new Dictionary<string, object?>()));
    }

    [Fact]
    public void Element_sets_require_declared_and_exact_owner_types()
    {
        var untyped = new Predicate.ElementOf(
            new ElementSetRef("tags"),
            [QueryConstant.Of("red")],
            SetQuantifier.Any);
        var typed = new Predicate.ElementOf(
            new ElementSetRef("tags", QueryType.String),
            [QueryConstant.Of("red")],
            SetQuantifier.Any);
        var mismatchedValue = new Predicate.ElementOf(
            new ElementSetRef("tags", QueryType.String),
            [QueryConstant.Of(1)],
            SetQuantifier.Any);
        var doubleSet = new Predicate.ElementOf(
            new ElementSetRef("ratios", QueryType.Double),
            ImmutableArray<QueryConstant>.Empty,
            SetQuantifier.Any);

        Assert.Contains(PortableQuerySemantics.Validate(untyped).Refusals, refusal => refusal.Code == "GW-SEM-TYPE-007");
        Assert.False(PortableQuerySemantics.Validate(untyped).IsPortable);
        Assert.Contains(PortableQuerySemantics.Validate(mismatchedValue).Refusals, refusal => refusal.Code == "GW-SEM-TYPE-005");
        Assert.False(PortableQuerySemantics.Validate(mismatchedValue).IsPortable);
        Assert.Contains(PortableQuerySemantics.Validate(doubleSet).Refusals, refusal => refusal.Code == "GW-SEM-TYPE-002");
        Assert.False(PortableQuerySemantics.Validate(doubleSet).IsPortable);
        Assert.True(PortableQuerySemantics.Validate(typed).IsPortable);
        Assert.True(PortableQuerySemantics.Evaluate(typed, new Dictionary<string, object?> { ["tags"] = new[] { "red" } }));
        Assert.False(PortableQuerySemantics.Evaluate(typed, new Dictionary<string, object?> { ["tags"] = new[] { 1 } }));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(typed), new Dictionary<string, object?> { ["tags"] = new[] { 1 } }));
    }

    [Fact]
    public void Search_leaves_are_total_even_when_a_provider_plan_must_refuse_them()
    {
        var starts = new Predicate.StartsWith(Text, "pre");
        var contains = new Predicate.Substring(Text, "mid", Anchor.Contains);
        var row = new Dictionary<string, object?> { [Text.Name] = null };

        Assert.False(PortableQuerySemantics.Evaluate(starts, row));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(starts), row));
        Assert.False(PortableQuerySemantics.Evaluate(contains, row));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(contains), row));
        Assert.False(PortableQuerySemantics.Validate(starts).IsPortable);
        Assert.True(PortableQuerySemantics.Validate(contains).IsPortable);
        Assert.False(PortableQuerySemantics.Validate(new Predicate.Not(contains)).IsPortable);
    }

    [Fact]
    public void Column_compare_is_false_for_null_and_its_complement_is_true()
    {
        var compare = new Predicate.ColumnCompare(Number, CompareOp.LessThan, new ColumnRef(Table, "other", QueryType.Int32));
        var row = new Dictionary<string, object?> { [Number.Name] = null, ["other"] = 10 };

        Assert.False(PortableQuerySemantics.Evaluate(compare, row));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(compare), row));
    }

    [Fact]
    public void Boolean_algebra_is_total_for_all_public_nodes()
    {
        var equal = new Predicate.Equal(Number, QueryConstant.Of(Number, 5));
        var conjunction = new Predicate.And([Predicate.AlwaysTrue.Instance, equal]);
        var disjunction = new Predicate.Or([Predicate.AlwaysFalse.Instance, equal]);
        var all = new Predicate.ElementOf(new ElementSetRef("tags", QueryType.String), [QueryConstant.Of("red"), QueryConstant.Of("blue")], SetQuantifier.All);

        Assert.True(PortableQuerySemantics.Evaluate(Predicate.AlwaysTrue.Instance, new Dictionary<string, object?>()));
        Assert.False(PortableQuerySemantics.Evaluate(Predicate.AlwaysFalse.Instance, new Dictionary<string, object?>()));
        Assert.True(PortableQuerySemantics.Evaluate(conjunction, new Dictionary<string, object?> { [Number.Name] = 5 }));
        Assert.True(PortableQuerySemantics.Evaluate(disjunction, new Dictionary<string, object?> { [Number.Name] = 5 }));
        Assert.True(PortableQuerySemantics.Evaluate(all, new Dictionary<string, object?> { ["tags"] = new[] { "red", "blue" } }));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(all), new Dictionary<string, object?> { ["tags"] = new[] { "red" } }));
        Assert.True(PortableQuerySemantics.Evaluate(
            new Predicate.Substring(Text, "end", Anchor.EndsWith),
            new Dictionary<string, object?> { [Text.Name] = "the end" }));
    }

    [Fact]
    public void Semantic_validation_reports_portable_alternatives_for_refusals()
    {
        var checks = new Predicate[]
        {
            new Predicate.ColumnCompare(Double, CompareOp.Equal, Double),
            new Predicate.Range(Binary, Bound.Inclusive(QueryConstant.Of(Binary, new byte[] { 1 })), null),
            new Predicate.Range(Boolean, Bound.Inclusive(QueryConstant.Of(Boolean, true)), null),
            new Predicate.ColumnCompare(Boolean, CompareOp.LessThan, Boolean),
            new Predicate.StartsWith(Text, "prefix"),
            new Predicate.Not(new Predicate.In(Number, [QueryConstant.Of(Number, 1)])),
            new Predicate.Equal(UnshapedDecimal, QueryConstant.Of(UnshapedDecimal, 1m))
        };

        foreach (var predicate in checks)
        {
            var result = PortableQuerySemantics.Validate(predicate);
            Assert.False(result.IsPortable, predicate.ToString());
            Assert.NotEmpty(result.Refusals);
            Assert.All(result.Refusals, refusal =>
            {
                Assert.StartsWith("GW-SEM-", refusal.Code);
                Assert.Contains("portable", refusal.Message, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    [Fact]
    public void Constants_require_exact_supported_types_without_implicit_coercion()
    {
        Assert.Throws<ArgumentException>(() => QueryConstant.Of(Number, 5L));
        Assert.Throws<ArgumentException>(() => QueryConstant.Of(Decimal, 5));
        Assert.Throws<ArgumentException>(() => QueryConstant.Of(Double, 1d));
    }

    [Fact]
    public void DateTimeOffset_semantics_compare_utc_ticks_without_losing_fractional_precision()
    {
        var instant = new DateTimeOffset(2024, 3, 31, 1, 59, 59, TimeSpan.FromHours(1)).AddTicks(9);
        var equivalentUtc = instant.ToUniversalTime();
        var oneTickAway = equivalentUtc.AddTicks(1);
        var predicate = new Predicate.Equal(Instant, QueryConstant.Of(Instant, instant));

        Assert.True(PortableQuerySemantics.Evaluate(predicate, new Dictionary<string, object?> { [Instant.Name] = equivalentUtc }));
        Assert.False(PortableQuerySemantics.Evaluate(predicate, new Dictionary<string, object?> { [Instant.Name] = oneTickAway }));
    }

    [Fact]
    public void Text_policy_is_explicit_and_culture_dependent_policies_are_refused()
    {
        var ordinal = new ColumnRef(Table, "ordinalText", QueryType.String, stringComparison: QueryStringComparisonPolicy.Ordinal);
        var culture = new ColumnRef(Table, "cultureText", QueryType.String, stringComparison: QueryStringComparisonPolicy.CurrentCulture);

        Assert.False(PortableQuerySemantics.Evaluate(
            new Predicate.Equal(ordinal, QueryConstant.Of(ordinal, "I")),
            new Dictionary<string, object?> { [ordinal.Name] = "i" }));
        Assert.False(PortableQuerySemantics.Validate(new Predicate.Equal(culture, QueryConstant.Of(culture, "I"))).IsPortable);
    }

    [Fact]
    public void Folded_text_policies_are_refused_without_a_versioned_search_key()
    {
        var cases = new[]
        {
            (QueryStringComparisonPolicy.AsciiIgnoreCase, "A", "a"),
            (QueryStringComparisonPolicy.AsciiIgnoreCase, "Å", "å"),
            (QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase, "A", "a"),
            (QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase, "\U00010400", "\U00010428")
        };

        foreach (var (policy, expected, actual) in cases)
        {
            var column = new ColumnRef(Table, "foldedText", QueryType.String, stringComparison: policy);
            var predicate = new Predicate.Equal(column, QueryConstant.Of(column, expected));
            var result = PortableQuerySemantics.Validate(predicate);

            Assert.False(result.IsPortable);
            Assert.Contains(result.Refusals, refusal => refusal.Code == "GW-SEM-TEXT-001");
            Assert.False(PortableQuerySemantics.Evaluate(predicate, new Dictionary<string, object?> { [column.Name] = actual }));
            Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(predicate), new Dictionary<string, object?> { [column.Name] = actual }));
        }
    }

    [Fact]
    public void Binary_equality_preserves_null_and_empty_and_binary_order_is_refused()
    {
        var equalEmpty = new Predicate.Equal(Binary, QueryConstant.Of(Binary, Array.Empty<byte>()));
        var equalNull = new Predicate.Equal(Binary, QueryConstant.Of(Binary, null));
        Assert.True(PortableQuerySemantics.Evaluate(equalEmpty, new Dictionary<string, object?> { [Binary.Name] = Array.Empty<byte>() }));
        Assert.False(PortableQuerySemantics.Evaluate(equalEmpty, new Dictionary<string, object?> { [Binary.Name] = null }));
        Assert.True(PortableQuerySemantics.Evaluate(equalNull, new Dictionary<string, object?> { [Binary.Name] = null }));

        var otherBinary = new ColumnRef(Table, "otherBinary", QueryType.Binary);
        var equalColumns = new Predicate.ColumnCompare(Binary, CompareOp.Equal, otherBinary);
        var unequalColumns = new Predicate.ColumnCompare(Binary, CompareOp.NotEqual, otherBinary);
        Assert.True(PortableQuerySemantics.Evaluate(equalColumns, new Dictionary<string, object?>
        {
            [Binary.Name] = new byte[] { 1, 2 },
            [otherBinary.Name] = new byte[] { 1, 2 }
        }));
        Assert.True(PortableQuerySemantics.Evaluate(unequalColumns, new Dictionary<string, object?>
        {
            [Binary.Name] = new byte[] { 1, 2 },
            [otherBinary.Name] = new byte[] { 1, 3 }
        }));
        Assert.False(PortableQuerySemantics.Evaluate(
            new Predicate.ColumnCompare(Binary, CompareOp.LessThan, otherBinary),
            new Dictionary<string, object?>
            {
                [Binary.Name] = new byte[] { 1 },
                [otherBinary.Name] = new byte[] { 2 }
            }));
        Assert.False(PortableQuerySemantics.Evaluate(
            new Predicate.Range(Binary, Bound.Inclusive(QueryConstant.Of(Binary, new byte[] { 1 })), null),
            new Dictionary<string, object?> { [Binary.Name] = new byte[] { 2 } }));

        var request = new QueryRequest(Table, Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Binary, OrderDirection.Ascending, NullOrder.First)], Projection.All, Paging.None);
        var result = PortableQuerySemantics.Validate(request);
        Assert.False(result.IsPortable);
        Assert.Contains(result.Refusals, refusal => refusal.Code == "GW-SEM-ORDER-001");
    }

    [Fact]
    public void Provider_default_null_ordering_is_refused()
    {
        var request = new QueryRequest(
            Table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Number)],
            Projection.All,
            Paging.None);

        var result = PortableQuerySemantics.Validate(request);

        Assert.False(result.IsPortable);
        Assert.Contains(result.Refusals, refusal => refusal.Code == "GW-SEM-ORDER-004");
    }

    [Fact]
    public void Boolean_ordering_requires_an_explicit_three_state_projection()
    {
        var request = new QueryRequest(
            Table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(Boolean, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.None);

        var result = PortableQuerySemantics.Validate(request);

        Assert.False(result.IsPortable);
        Assert.Contains(result.Refusals, refusal => refusal.Code == "GW-SEM-ORDER-005");
        Assert.True(PortableQuerySemantics.Validate(new Predicate.Equal(Boolean, QueryConstant.Of(Boolean, true))).IsPortable);

        var projectedKey = new ColumnRef(Table, "flagKey", QueryType.Int32, isNullable: false);
        var projectedRequest = new QueryRequest(
            Table,
            Predicate.AlwaysTrue.Instance,
            [new OrderTerm(projectedKey, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.None);
        Assert.True(PortableQuerySemantics.Validate(projectedRequest).IsPortable);
    }

    [Fact]
    public void Refused_and_unknown_nodes_still_have_deterministic_boolean_evaluation()
    {
        var malformed = new Predicate.ElementOf(
            new ElementSetRef("tags", QueryType.String),
            ImmutableArray.CreateRange(new[] { (QueryConstant)null! }),
            SetQuantifier.Any);
        var row = new Dictionary<string, object?> { ["tags"] = new[] { "value" } };

        Assert.False(PortableQuerySemantics.Validate(malformed).IsPortable);
        Assert.False(PortableQuerySemantics.Evaluate(malformed, row));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(malformed), row));

        var unknown = new UnknownPredicate();
        Assert.False(PortableQuerySemantics.Validate(unknown).IsPortable);
        Assert.False(PortableQuerySemantics.Evaluate(unknown, row));
        Assert.True(PortableQuerySemantics.Evaluate(new Predicate.Not(unknown), row));
    }

    [Fact]
    public void Guid_order_uses_network_byte_order_and_is_deterministic()
    {
        var left = new ColumnRef(Table, "leftGuid", QueryType.Guid);
        var right = new ColumnRef(Table, "rightGuid", QueryType.Guid);
        var compare = new Predicate.ColumnCompare(left, CompareOp.LessThan, right);
        var leftValue = System.Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var rightValue = System.Guid.Parse("00112234-0000-0000-0000-000000000000");

        Assert.True(PortableQuerySemantics.Validate(compare).IsPortable);
        Assert.True(PortableQuerySemantics.Evaluate(compare, new Dictionary<string, object?>
        {
            [left.Name] = leftValue,
            [right.Name] = rightValue
        }));
    }

    [Fact]
    public void The_pinned_300_shape_corpus_has_a_semantic_decision_for_every_shape()
    {
        Assert.Equal(G2Q1Corpus.ExpectedShapeCount, G2Q1Corpus.Shapes.Count);
        Assert.Equal(G2Q1Corpus.Shapes.Count, G2Q1Corpus.Shapes.Select(shape => shape.CanonicalInput).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(243, G2Q1Corpus.Shapes.Count(shape => shape.Decision == Q1CorpusDecision.Normalize));
        Assert.Equal(57, G2Q1Corpus.Shapes.Count(shape => shape.Decision == Q1CorpusDecision.Refuse));

        foreach (var shape in G2Q1Corpus.Shapes)
        {
            if (shape.PublicConstructionRejects)
            {
                Assert.ThrowsAny<ArgumentException>(() => { _ = shape.Exercise(); });
                continue;
            }

            var exercise = shape.Exercise();
            var result = PortableQuerySemantics.Validate(exercise.Request);
            var row = new Dictionary<string, object?>
            {
                ["textSearch"] = "I",
                ["numberValue"] = 1.2344m,
                ["boolValue"] = true,
                ["dateTicks"] = DateTimeOffset.UnixEpoch,
                ["guidKey"] = System.Guid.Empty,
                ["binaryValue"] = new byte[] { 0 }
            };
            if (shape.Decision == Q1CorpusDecision.Normalize)
            {
                Assert.True(result.IsPortable, $"{shape.Number}: {shape.Description}: {string.Join("; ", result.Refusals.Select(refusal => refusal.Message))}");
            }
            else
            {
                Assert.False(result.IsPortable, $"{shape.Number}: {shape.Description}");
                Assert.NotEmpty(result.Refusals);
                Assert.All(result.Refusals, refusal =>
                {
                    Assert.StartsWith("GW-SEM-", refusal.Code);
                    Assert.Contains("portable", refusal.Message, StringComparison.OrdinalIgnoreCase);
                });
            }
            AssertDeterministicComplement(exercise.Request.Where, row);
        }
    }

    private static void AssertDeterministicComplement(Predicate predicate, IReadOnlyDictionary<string, object?> row)
    {
        var first = PortableQuerySemantics.Evaluate(predicate, row);
        Assert.Equal(first, PortableQuerySemantics.Evaluate(predicate, row));
        Assert.NotEqual(first, PortableQuerySemantics.Evaluate(new Predicate.Not(predicate), row));
    }

    private sealed record UnknownPredicate : Predicate;
}
