using System.Collections.Immutable;
using System.Globalization;
using Groundwork.Query.Model;

namespace Groundwork.Query.Model.Tests;

internal enum Q1CorpusDecision
{
    Normalize,
    Refuse
}

internal enum Q1CorpusOperation
{
    Equal,
    In,
    Contains,
    NotEqual,
    StartsWith,
    NotContains,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

internal sealed record Q1CorpusShape(
    int Number,
    string Identity,
    Q1CorpusDecision Decision,
    string DecisionId,
    string Description,
    Func<QueryRequest>? Build);

/// <summary>
/// Provider-free projection of the pinned G2 issue #230 shape vocabulary.
/// It intentionally mirrors the corpus generator's 210 predicate, 72 order/page,
/// and 18 compound shapes without importing any provider or runtime contracts.
/// </summary>
internal static class G2Q1Corpus
{
    public const int ExpectedShapeCount = 300;

    private static readonly TableId Table = new("g2-edge-row");
    private static readonly ColumnRef Text = new(Table, "textSearch", QueryType.String);
    private static readonly ColumnRef Number = new(Table, "numberValue", QueryType.Decimal, decimalPrecision: 18, decimalScale: 4);
    private static readonly ColumnRef Boolean = new(Table, "boolValue", QueryType.Boolean);
    private static readonly ColumnRef Instant = new(Table, "dateTicks", QueryType.DateTimeOffset);
    private static readonly ColumnRef Guid = new(Table, "guidKey", QueryType.Guid);
    private static readonly ColumnRef Binary = new(Table, "binaryValue", QueryType.Binary);

    private static readonly Lazy<IReadOnlyList<Q1CorpusShape>> ShapesValue = new(CreateShapes);

    public static IReadOnlyList<Q1CorpusShape> Shapes => ShapesValue.Value;

    private static IReadOnlyList<Q1CorpusShape> CreateShapes()
    {
        var shapes = new List<Q1CorpusShape>(ExpectedShapeCount);
        var number = 1;

        AddPredicateShapes(shapes, ref number, Text, "q-textSearch",
            [Q1CorpusOperation.Equal, Q1CorpusOperation.In, Q1CorpusOperation.Contains, Q1CorpusOperation.NotEqual, Q1CorpusOperation.StartsWith, Q1CorpusOperation.NotContains],
            [null, string.Empty, "I", "i", "İ", "ı", "Straße", "e\u0301", "é"],
            (operation, value) => operation is Q1CorpusOperation.StartsWith or Q1CorpusOperation.NotContains ||
                operation == Q1CorpusOperation.Contains && value is null,
            "portable-string-search-key");
        AddPredicateShapes(shapes, ref number, Number, "q-numberValue",
            [Q1CorpusOperation.Equal, Q1CorpusOperation.In, Q1CorpusOperation.GreaterThan, Q1CorpusOperation.GreaterThanOrEqual, Q1CorpusOperation.LessThan, Q1CorpusOperation.LessThanOrEqual, Q1CorpusOperation.NotEqual],
            [null, 0m, -1m, 1.2345m, 1.2344m, 99999999999999.9999m],
            (operation, value) => value is null && operation is Q1CorpusOperation.GreaterThan or Q1CorpusOperation.GreaterThanOrEqual or Q1CorpusOperation.LessThan or Q1CorpusOperation.LessThanOrEqual,
            "typed-decimal-18-4");
        AddPredicateShapes(shapes, ref number, Boolean, "q-boolValue",
            [Q1CorpusOperation.Equal, Q1CorpusOperation.In, Q1CorpusOperation.NotEqual],
            [null, true, false, true, false, true],
            (_, _) => false,
            "total-boolean-null-complement");
        AddPredicateShapes(shapes, ref number, Instant, "q-dateTicks",
            [Q1CorpusOperation.Equal, Q1CorpusOperation.In, Q1CorpusOperation.GreaterThan, Q1CorpusOperation.GreaterThanOrEqual, Q1CorpusOperation.LessThan, Q1CorpusOperation.LessThanOrEqual, Q1CorpusOperation.NotEqual],
            [null, DateTimeOffset.UnixEpoch.AddTicks(-1), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddTicks(1), new DateTimeOffset(2024, 3, 31, 1, 59, 59, TimeSpan.FromHours(1)).AddTicks(9), new DateTimeOffset(2024, 10, 27, 1, 59, 59, TimeSpan.FromHours(2)).AddTicks(9)],
            (operation, value) => value is null && operation is Q1CorpusOperation.GreaterThan or Q1CorpusOperation.GreaterThanOrEqual or Q1CorpusOperation.LessThan or Q1CorpusOperation.LessThanOrEqual,
            "utc-ticks");
        AddPredicateShapes(shapes, ref number, Guid, "q-guidKey",
            [Q1CorpusOperation.Equal, Q1CorpusOperation.In, Q1CorpusOperation.NotEqual],
            [null, System.Guid.Empty, System.Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), System.Guid.Parse("ffeeddcc-bbaa-9988-7766-554433221100"), System.Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), System.Guid.Parse("deadbeef-dead-beef-dead-beefdeadbeef")],
            (_, _) => false,
            "rfc4122-network-guid-key");
        AddPredicateShapes(shapes, ref number, Binary, "q-binaryValue",
            [Q1CorpusOperation.Equal, Q1CorpusOperation.In, Q1CorpusOperation.GreaterThan, Q1CorpusOperation.LessThan, Q1CorpusOperation.StartsWith],
            new object?[] { null, Array.Empty<byte>(), new byte[] { 0 }, new byte[] { 0, 255 }, new byte[] { 1, 2, 3, 4, 5 }, new byte[] { 255, 0 } },
            (operation, _) => operation is not (Q1CorpusOperation.Equal or Q1CorpusOperation.In),
            "binary-equality-membership");

        var orderColumns = new (string Identity, ColumnRef Column, bool Refused)[]
        {
            ("textOrderKey", Text, false),
            ("numberValue", Number, false),
            ("boolValue", Boolean, false),
            ("dateTicks", Instant, false),
            ("guidKey", Guid, false),
            ("binaryValue", Binary, true)
        };
        foreach (var (identity, column, refused) in orderColumns)
        foreach (var direction in new[] { OrderDirection.Ascending, OrderDirection.Descending })
        for (var page = 0; page < 6; page++)
        {
            var suffix = direction == OrderDirection.Ascending ? "asc" : "desc";
            var capturedDirection = direction;
            var capturedPage = page;
            shapes.Add(new Q1CorpusShape(
                number++,
                "q-order-" + identity + "-" + suffix,
                refused ? Q1CorpusDecision.Refuse : Q1CorpusDecision.Normalize,
                refused ? "binary-order-refused" : "normalized-ordering",
                identity + " " + direction + " page " + page,
                refused
                    ? null
                    : () => Request(
                        Predicate.AlwaysTrue.Instance,
                        ImmutableArray.Create(new OrderTerm(column, capturedDirection)),
                        Paging.OffsetLimit(capturedPage * 2, 2))));
        }

        var textValues = new object?[] { string.Empty, " ", "  \t", "I", "i", "İ" };
        var numericValues = new object?[] { 0m, -1m };
        for (var index = 0; index < 18; index++)
        {
            var pair = index % 9;
            var text = textValues[pair % textValues.Length];
            var numeric = numericValues[pair / textValues.Length];
            var disjunction = index >= 9;
            var textPredicate = new Predicate.Equal(Text, QueryConstant.Of(Text, text));
            var numericPredicate = new Predicate.Equal(Number, QueryConstant.Of(Number, numeric));
            Predicate predicate = disjunction
                ? new Predicate.Or(ImmutableArray.Create<Predicate>(textPredicate, numericPredicate))
                : new Predicate.And(ImmutableArray.Create<Predicate>(textPredicate, numericPredicate));
            var capturedPredicate = predicate;
            shapes.Add(new Q1CorpusShape(
                number++,
                "q-compound",
                Q1CorpusDecision.Normalize,
                disjunction ? "compound-disjunction" : "compound-conjunction",
                disjunction ? "text OR number" : "text AND number",
                () => Request(capturedPredicate, ImmutableArray<OrderTerm>.Empty, Paging.None)));
        }

        if (shapes.Count != ExpectedShapeCount)
            throw new InvalidOperationException($"Q1 G2 corpus generated {shapes.Count} shapes, expected {ExpectedShapeCount}.");
        return shapes;
    }

    private static void AddPredicateShapes(
        ICollection<Q1CorpusShape> shapes,
        ref int number,
        ColumnRef column,
        string identity,
        IReadOnlyList<Q1CorpusOperation> operations,
        IReadOnlyList<object?> values,
        Func<Q1CorpusOperation, object?, bool> refusal,
        string decisionId)
    {
        foreach (var operation in operations)
        {
            foreach (var value in values)
            {
                var capturedOperation = operation;
                var capturedValue = value;
                var refused = refusal(operation, value);
                shapes.Add(new Q1CorpusShape(
                    number++,
                    identity,
                    refused ? Q1CorpusDecision.Refuse : Q1CorpusDecision.Normalize,
                    refused ? RefusalId(column, operation, value) : decisionId,
                    identity + " " + operation + " " + Describe(value),
                    refused ? null : () => Request(BuildPredicate(column, capturedOperation, capturedValue), ImmutableArray<OrderTerm>.Empty, Paging.None)));
            }

            if (operation == Q1CorpusOperation.In)
            {
                shapes.Add(new Q1CorpusShape(
                    number++,
                    identity,
                    Q1CorpusDecision.Normalize,
                    decisionId,
                    identity + " In [] (empty membership is false)",
                    () => Request(new Predicate.In(column, ImmutableArray<QueryConstant>.Empty), ImmutableArray<OrderTerm>.Empty, Paging.None)));
            }
        }
    }

    private static Predicate BuildPredicate(ColumnRef column, Q1CorpusOperation operation, object? value)
    {
        var constant = QueryConstant.Of(column, value);
        return operation switch
        {
            Q1CorpusOperation.Equal => new Predicate.Equal(column, constant),
            Q1CorpusOperation.In => new Predicate.In(column, value is null
                ? ImmutableArray.Create(constant)
                : ImmutableArray.Create(constant, QueryConstant.Of(column, null))),
            Q1CorpusOperation.Contains => new Predicate.Substring(column, (string)value!, Anchor.Contains),
            Q1CorpusOperation.NotEqual => new Predicate.Not(new Predicate.Equal(column, constant)),
            Q1CorpusOperation.StartsWith => new Predicate.StartsWith(column, (string)value!),
            Q1CorpusOperation.NotContains => new Predicate.Not(new Predicate.Substring(column, (string)value!, Anchor.Contains)),
            Q1CorpusOperation.GreaterThan => new Predicate.Range(column, Bound.Exclusive(constant), null),
            Q1CorpusOperation.GreaterThanOrEqual => new Predicate.Range(column, Bound.Inclusive(constant), null),
            Q1CorpusOperation.LessThan => new Predicate.Range(column, null, Bound.Exclusive(constant)),
            Q1CorpusOperation.LessThanOrEqual => new Predicate.Range(column, null, Bound.Inclusive(constant)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static QueryRequest Request(Predicate predicate, ImmutableArray<OrderTerm> order, Paging paging) => new(
        Table,
        predicate,
        order,
        Projection.All,
        paging);

    private static string RefusalId(ColumnRef column, Q1CorpusOperation operation, object? value) =>
        value is null && operation is Q1CorpusOperation.GreaterThan or Q1CorpusOperation.GreaterThanOrEqual or Q1CorpusOperation.LessThan or Q1CorpusOperation.LessThanOrEqual
            ? "null-range-refused"
            : value is null && operation is Q1CorpusOperation.Contains or Q1CorpusOperation.NotContains or Q1CorpusOperation.StartsWith
                ? "null-search-refused"
                : column.Type == QueryType.Binary
                    ? "binary-range-prefix-refused"
                    : "cross-provider-index-certification-refused";

    private static string Describe(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTimeOffset instant => instant.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}
