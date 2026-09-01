using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Groundwork.Query.Model;

/// <summary>Whether a query construct has a provider-neutral v2 interpretation.</summary>
public enum PortableSemanticDecision
{
    Normalize,
    Refuse
}

public sealed record PortableSemanticRefusal(string Code, string Message, string Path)
{
    public PortableSemanticRefusal(string code, string message)
        : this(code, message, string.Empty)
    {
    }
}

public sealed class PortableSemanticValidationResult
{
    internal PortableSemanticValidationResult(IReadOnlyList<PortableSemanticRefusal> refusals)
    {
        Refusals = new ReadOnlyCollection<PortableSemanticRefusal>(refusals.ToArray());
    }

    public IReadOnlyList<PortableSemanticRefusal> Refusals { get; }
    public bool IsPortable => Refusals.Count == 0;
    public PortableSemanticDecision Decision => IsPortable ? PortableSemanticDecision.Normalize : PortableSemanticDecision.Refuse;
}

/// <summary>
/// The provider-neutral v2 semantic contract. This type deliberately contains
/// no provider, runtime, or serialization dependencies.
/// </summary>
public static class PortableQuerySemantics
{
    public static PortableSemanticValidationResult Validate(Predicate predicate)
    {
        if (predicate is null)
            throw new ArgumentNullException(nameof(predicate));

        var refusals = new List<PortableSemanticRefusal>();
        ValidatePredicate(predicate, refusals, "predicate");
        return new PortableSemanticValidationResult(refusals);
    }

    public static PortableSemanticValidationResult Validate(QueryRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var refusals = new List<PortableSemanticRefusal>();
        ValidatePredicate(request.Where, refusals, "where");

        if (request.Join is not null)
        {
            foreach (var (pair, index) in request.Join.ColumnPairs.Select((pair, index) => (pair, index)))
            {
                var path = "join.columnPairs[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                ValidateColumn(pair.Source, refusals, path + ".source");
                ValidateColumn(pair.Target, refusals, path + ".target");
            }
        }

        foreach (var term in request.Order)
        {
            ValidateColumn(term.Column, refusals, "order." + term.Column.Name);
            if (term.NullOrder == NullOrder.ProviderDefault)
                Refuse(refusals, "GW-SEM-ORDER-004", "Provider-default null ordering is not portable; choose explicit nulls-first or nulls-last ordering.", "order." + term.Column.Name);
            if (term.Column.Type is QueryType.Binary or QueryType.Double)
                Refuse(refusals, "GW-SEM-ORDER-001", "Ordering this type is not portable; order a declared portable projection or key instead.", "order." + term.Column.Name);
            if (term.Column.Type == QueryType.Boolean)
                Refuse(refusals, "GW-SEM-ORDER-005", "Boolean ordering is not portable without an explicit three-state projected key; order the declared projection instead.", "order." + term.Column.Name);
        }

        if (request.Result.RequiresDeterministicOrder && request.Order.Length == 0)
            Refuse(refusals, "GW-SEM-ORDER-006", "First and FirstOrDefault queries require an explicit deterministic order; add an OrderBy term.", "order");

        ValidateReduction(request.Result, refusals);

        if (request.LatestPerKey is not null)
        {
            ValidateColumn(request.LatestPerKey.Key, refusals, "latestPerKey.key");
            ValidateColumn(request.LatestPerKey.Timestamp, refusals, "latestPerKey.timestamp");
            if (request.LatestPerKey.Timestamp.Type != QueryType.DateTimeOffset || request.LatestPerKey.Timestamp.IsNullable)
                Refuse(refusals, "GW-SEM-LATEST-001", "Latest-per-key requires a non-null DateTimeOffset timestamp; project a non-null UTC timestamp instead.", "latestPerKey.timestamp");
        }

        return new PortableSemanticValidationResult(refusals);
    }

    private static void ValidateReduction(ResultShape result, ICollection<PortableSemanticRefusal> refusals)
    {
        if (result is not ResultShape.Reduction reduction)
            return;

        if (result is ResultShape.Sum)
        {
            if (reduction.Column.Type is not (QueryType.Int32 or QueryType.Int64 or QueryType.Decimal))
            {
                Refuse(refusals, "GW-SEM-AGG-001", "Sum requires an Int32, Int64, or Decimal column.", "result.column");
                return;
            }
        }
        else if (result is ResultShape.Min or ResultShape.Max)
        {
            if (!IsOrderable(reduction.Column.Type))
            {
                Refuse(refusals, "GW-SEM-AGG-002", "Min and Max require an orderable column.", "result.column");
                return;
            }
        }

        ValidateColumn(reduction.Column, refusals, "result.column");
    }

    public static bool Evaluate(Predicate predicate, IReadOnlyDictionary<string, object?> row)
    {
        if (predicate is null)
            throw new ArgumentNullException(nameof(predicate));
        if (row is null)
            throw new ArgumentNullException(nameof(row));

        return EvaluateCore(predicate, row);
    }

    private static void ValidatePredicate(Predicate predicate, ICollection<PortableSemanticRefusal> refusals, string path)
    {
        switch (predicate)
        {
            case Predicate.AlwaysTrue:
            case Predicate.AlwaysFalse:
                return;
            case Predicate.Equal equal:
                ValidateColumn(equal.Column, refusals, path + ".column");
                ValidateConstant(equal.Column, equal.Value, refusals, path + ".value");
                return;
            case Predicate.In membership:
                ValidateColumn(membership.Column, refusals, path + ".column");
                foreach (var (value, index) in membership.Values.Select((value, index) => (value, index)))
                    ValidateConstant(membership.Column, value, refusals, path + ".values[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                return;
            case Predicate.Range range:
                ValidateColumn(range.Column, refusals, path + ".column");
                ValidateRangeType(range.Column, refusals, path);
                if (range.Lower is not null)
                    ValidateConstant(range.Column, range.Lower.Value, refusals, path + ".lower");
                if (range.Upper is not null)
                    ValidateConstant(range.Column, range.Upper.Value, refusals, path + ".upper");
                if (range.Lower?.Value.Kind == QueryConstantKind.Null || range.Upper?.Value.Kind == QueryConstantKind.Null)
                    Refuse(refusals, "GW-SEM-NULL-001", "A range with a null operand is not portable; use Equal(column, null) or its total complement instead.", path);
                return;
            case Predicate.StartsWith startsWith:
                ValidateColumn(startsWith.Column, refusals, path + ".column");
                return;
            case Predicate.Substring substring:
                ValidateColumn(substring.Column, refusals, path + ".column");
                if (substring.Anchor is not (Anchor.Contains or Anchor.EndsWith))
                    Refuse(refusals, "GW-SEM-TEXT-003", "The requested substring anchor is not portable; use Contains or a persisted search key.", path);
                return;
            case Predicate.ElementOf elementOf:
                ValidateElementSet(elementOf, refusals, path);
                return;
            case Predicate.ElementSubstring elementSubstring:
                ValidateElementSubstring(elementSubstring, refusals, path);
                return;
            case Predicate.ColumnCompare compare:
                ValidateColumn(compare.Left, refusals, path + ".left");
                ValidateColumn(compare.Right, refusals, path + ".right");
                if (compare.Left.Type != compare.Right.Type)
                    Refuse(refusals, "GW-SEM-TYPE-001", "Column comparison requires an exact matching type; compare a portable projection with the same declared type instead.", path);
                if (compare.Op is not (CompareOp.Equal or CompareOp.NotEqual) && !IsOrderable(compare.Left.Type))
                    Refuse(refusals, "GW-SEM-ORDER-002", "Ordering comparison for this type is not portable; use equality or a declared orderable projection instead.", path);
                return;
            case Predicate.Not not:
                ValidatePredicate(not.Inner, refusals, path + ".inner");
                if ((not.Inner is Predicate.In negatedMembership && negatedMembership.Values.Length != 0) || not.Inner is Predicate.Range or Predicate.StartsWith or Predicate.Substring or Predicate.ElementSubstring)
                    Refuse(refusals, "GW-SEM-NOT-001", "This negation is not portable; use the supported total Equal/ColumnCompare complement or a portable positive predicate instead.", path);
                return;
            case Predicate.And and:
                foreach (var (term, index) in and.Terms.Select((term, index) => (term, index)))
                    ValidatePredicate(term, refusals, path + ".terms[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                return;
            case Predicate.Or or:
                foreach (var (term, index) in or.Terms.Select((term, index) => (term, index)))
                    ValidatePredicate(term, refusals, path + ".terms[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                return;
            default:
                Refuse(refusals, "GW-SEM-UNKNOWN-001", "The predicate node is not portable; use a declared v2 predicate node instead.", path);
                return;
        }
    }

    private static void ValidateElementSet(Predicate.ElementOf elementOf, ICollection<PortableSemanticRefusal> refusals, string path)
    {
        if (elementOf.Set.Type is not QueryType type)
        {
            Refuse(refusals, "GW-SEM-TYPE-007", "An element set must declare its exact element type; bind a typed set before provider planning.", path + ".set");
            return;
        }
        if (type == QueryType.Double)
            Refuse(refusals, "GW-SEM-TYPE-002", "Double membership is not portable; use an exact supported numeric type instead.", path + ".set");

        foreach (var (value, index) in elementOf.Values.Select((value, index) => (value, index)))
        {
            if (value is null)
            {
                Refuse(refusals, "GW-SEM-TYPE-004", "A null element-set constant is not portable; use an explicit typed constant instead.", path + ".values[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                continue;
            }
            if (value.Kind == QueryConstantKind.Null)
            {
                if (value.Type != type)
                    Refuse(refusals, "GW-SEM-TYPE-005", "The element-set null constant must carry the set's exact declared type; bind a typed null instead.", path + ".values[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                continue;
            }
            if (value.Type != type)
            {
                Refuse(refusals, "GW-SEM-TYPE-005", "Element-set values must exactly match the set's declared type; use a typed projection instead.", path + ".values[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                continue;
            }
        }
    }

    private static void ValidateElementSubstring(
        Predicate.ElementSubstring elementSubstring,
        ICollection<PortableSemanticRefusal> refusals,
        string path)
    {
        if (elementSubstring.Set.Type is not QueryType type)
        {
            Refuse(refusals, "GW-SEM-TYPE-007", "An element set must declare its exact element type; bind a typed string set before provider planning.", path + ".set");
            return;
        }
        if (type != QueryType.String)
            Refuse(refusals, "GW-SEM-TYPE-005", "Element substring matching requires a string element set; bind the declared string set instead.", path + ".set");
        if (elementSubstring.Anchor is not (Anchor.Contains or Anchor.EndsWith))
            Refuse(refusals, "GW-SEM-TEXT-003", "The requested element substring anchor is not portable; use Contains or EndsWith.", path);
        if (!IsElementSubstringPolicy(elementSubstring.StringComparison))
            Refuse(refusals, "GW-SEM-TEXT-001", "Element substring matching requires an explicit Ordinal or AsciiIgnoreCase policy; UnicodeOrdinalIgnoreCase requires a persisted per-element search key and is not admitted for a raw array.", path + ".stringComparison");
    }

    private static bool IsElementSubstringPolicy(QueryStringComparisonPolicy policy) => policy is
        QueryStringComparisonPolicy.Ordinal or
        QueryStringComparisonPolicy.AsciiIgnoreCase;

    private static bool IsElementSubstringEvaluationPolicy(QueryStringComparisonPolicy policy) => policy is
        QueryStringComparisonPolicy.Ordinal or
        QueryStringComparisonPolicy.AsciiIgnoreCase or
        QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase;

    private static void ValidateConstant(ColumnRef column, QueryConstant constant, ICollection<PortableSemanticRefusal> refusals, string path)
    {
        if (constant is null)
        {
            Refuse(refusals, "GW-SEM-TYPE-004", "A null constant reference is not portable; use an explicit typed null constant instead.", path);
            return;
        }
        if (constant.Type != column.Type)
            Refuse(refusals, "GW-SEM-TYPE-005", "The constant type must exactly match the column type; bind an exact typed constant instead.", path);
        if (constant.Kind == QueryConstantKind.Null && !column.IsNullable)
            Refuse(refusals, "GW-SEM-NULL-002", "A null value for a non-nullable column is not portable; use a non-null value or a nullable projection instead.", path);
    }

    private static void ValidateRangeType(ColumnRef column, ICollection<PortableSemanticRefusal> refusals, string path)
    {
        if (!IsOrderable(column.Type))
            Refuse(refusals, "GW-SEM-ORDER-003", "Range ordering for this type is not portable; use equality/membership or a declared orderable projection instead.", path);
    }

    private static void ValidateColumn(ColumnRef column, ICollection<PortableSemanticRefusal> refusals, string path)
    {
        if (column.Type == QueryType.Double)
            Refuse(refusals, "GW-SEM-TYPE-006", "Binary floating-point values are not portable in predicates or indexes; use Int32, Int64, or declared Decimal instead.", path);
        if (column.Type == QueryType.Decimal && (column.DecimalPrecision != 18 || column.DecimalScale != 4))
            Refuse(refusals, "GW-SEM-DECIMAL-001", "Portable Decimal requires declared decimal(18,4) with no rounding; use decimal(18,4) or an exact integer type instead.", path);
        if (column.Type == QueryType.String && IsRefusedTextComparison(column.StringComparison))
            Refuse(refusals, "GW-SEM-TEXT-001", "This text comparison policy is not portable without an explicit versioned persisted search-key projection; use Ordinal or bind such a projection.", path);
    }

    private static bool IsRefusedTextComparison(QueryStringComparisonPolicy policy) => policy != QueryStringComparisonPolicy.Ordinal;

    private static bool IsOrderable(QueryType type) => type is
        QueryType.Int32 or QueryType.Int64 or QueryType.Decimal or QueryType.String or QueryType.DateTimeOffset or QueryType.Guid;

    private static void Refuse(ICollection<PortableSemanticRefusal> refusals, string code, string message, string path) =>
        refusals.Add(new PortableSemanticRefusal(code, message, path));

    private static bool EvaluateCore(Predicate predicate, IReadOnlyDictionary<string, object?> row)
    {
        switch (predicate)
        {
            case Predicate.AlwaysTrue:
                return true;
            case Predicate.AlwaysFalse:
                return false;
            case Predicate.Equal equal:
                return CompareEqual(equal.Column, GetValue(equal.Column, row), equal.Value);
            case Predicate.In membership:
            {
                var actual = GetValue(membership.Column, row);
                return membership.Values.Any(value => CompareEqual(membership.Column, actual, value));
            }
            case Predicate.Range range:
            {
                var actual = GetValue(range.Column, row);
                if (actual is null)
                    return false;
                if (range.Lower is not null && !CompareBound(range.Column, actual, range.Lower, isLower: true))
                    return false;
                return range.Upper is null || CompareBound(range.Column, actual, range.Upper, isLower: false);
            }
            case Predicate.StartsWith startsWith:
            {
                var actual = GetValue(startsWith.Column, row) as string;
                return actual is not null
                    && startsWith.Column.StringComparison == QueryStringComparisonPolicy.Ordinal
                    && actual.StartsWith(startsWith.Prefix, StringComparison.Ordinal);
            }
            case Predicate.Substring substring:
            {
                var actual = GetValue(substring.Column, row) as string;
                if (actual is null)
                    return false;
                return substring.Anchor == Anchor.Contains
                    ? IndexOf(actual, substring.Needle, substring.Column.StringComparison) >= 0
                    : EndsWith(actual, substring.Needle, substring.Column.StringComparison);
            }
            case Predicate.ElementOf elementOf:
            {
                if (!TryGetElements(elementOf.Set, row, out var elements))
                    return false;
                return elementOf.Quantifier == SetQuantifier.Any
                    ? elements.Any(element => elementOf.Values.Any(value => CompareUntyped(element, value)))
                    : elementOf.Values.All(value => elements.Any(element => CompareUntyped(element, value)));
            }
            case Predicate.ElementSubstring elementSubstring:
            {
                if (elementSubstring.Set.Type != QueryType.String ||
                    !IsElementSubstringEvaluationPolicy(elementSubstring.StringComparison) ||
                    !TryGetElementSubstringElements(elementSubstring.Set, row, out var elements))
                    return false;
                return elements
                    .OfType<string>()
                    .Any(element => elementSubstring.Anchor == Anchor.Contains
                        ? ElementIndexOf(element, elementSubstring.Needle, elementSubstring.StringComparison) >= 0
                        : ElementEndsWith(element, elementSubstring.Needle, elementSubstring.StringComparison));
            }
            case Predicate.ColumnCompare compare:
            {
                var left = GetValue(compare.Left, row);
                var right = GetValue(compare.Right, row);
                if (left is null || right is null)
                    return false;
                if (compare.Left.Type == QueryType.Binary && compare.Op is not (CompareOp.Equal or CompareOp.NotEqual))
                    return false;
                var comparison = CompareValues(compare.Left, left, right);
                return comparison is int value && ApplyComparison(value, compare.Op);
            }
            case Predicate.Not not:
                return !EvaluateCore(not.Inner, row);
            case Predicate.And and:
                return and.Terms.All(term => EvaluateCore(term, row));
            case Predicate.Or or:
                return or.Terms.Any(term => EvaluateCore(term, row));
            default:
                return false;
        }
    }

    private static object? GetValue(ColumnRef column, IReadOnlyDictionary<string, object?> row)
    {
        row.TryGetValue(column.Name, out var value);
        if (value is null)
            return null;
        if (column.Type == QueryType.String && value is string text)
            return text;
        if (!IsExactRuntimeType(column.Type, value))
            throw new ArgumentException("Row value for '" + column + "' must have exact type " + column.Type + ".", nameof(row));
        return value;
    }

    private static bool IsExactRuntimeType(QueryType type, object value) => type switch
    {
        QueryType.Boolean => value is bool,
        QueryType.Int32 => value is int,
        QueryType.Int64 => value is long,
        QueryType.Decimal => value is decimal,
        QueryType.Double => value is double,
        QueryType.String => value is string,
        QueryType.DateTimeOffset => value is DateTimeOffset,
        QueryType.Guid => value is Guid,
        QueryType.Binary => value is byte[],
        _ => false
    };

    private static bool TryGetElements(ElementSetRef set, IReadOnlyDictionary<string, object?> row, out IReadOnlyList<object?> elements)
    {
        elements = Array.Empty<object?>();
        if (set.Type is not QueryType type)
            return false;
        if (!row.TryGetValue(set.Name, out var value) || value is null)
            return false;

        if (value is string or byte[] || value is not IEnumerable enumerable)
            return false;

        var candidate = enumerable.Cast<object?>().ToArray();
        if (candidate.Any(element => element is not null && !IsExactRuntimeType(type, element)))
            return false;

        elements = candidate;
        return true;
    }

    private static bool TryGetElementSubstringElements(ElementSetRef set, IReadOnlyDictionary<string, object?> row, out IReadOnlyList<object?> elements)
    {
        elements = Array.Empty<object?>();
        if (set.Type != QueryType.String || !row.TryGetValue(set.Name, out var value) || value is null || value is string or byte[] || value is not IEnumerable enumerable)
            return false;

        // A malformed member does not invalidate its siblings: substring matching is defined
        // per string element, so null and non-string members are simply non-matches.
        elements = enumerable.Cast<object?>().ToArray();
        return true;
    }

    private static bool CompareEqual(ColumnRef column, object? actual, QueryConstant expected)
    {
        if (expected.Kind == QueryConstantKind.Null)
            return actual is null;
        if (actual is null)
            return false;
        return CompareUntyped(actual, expected, column.StringComparison);
    }

    private static bool CompareUntyped(object? actual, QueryConstant? expected, QueryStringComparisonPolicy policy = QueryStringComparisonPolicy.Ordinal)
    {
        if (expected is null)
            return false;
        if (expected.Kind == QueryConstantKind.Null)
            return actual is null;
        if (actual is null)
            return false;
        if (!IsExactRuntimeType(expected.Type!.Value, actual))
            throw new ArgumentException("Element value must have exact type " + expected.Type + ".", nameof(actual));
        if (actual is string actualText && expected.Value is string expectedText)
            return policy == QueryStringComparisonPolicy.Ordinal && string.Equals(actualText, expectedText, StringComparison.Ordinal);
        if (actual is byte[] actualBytes && expected.Value is byte[] expectedBytes)
            return actualBytes.SequenceEqual(expectedBytes);
        if (actual is DateTimeOffset actualInstant && expected.Value is DateTimeOffset expectedInstant)
            return actualInstant.UtcTicks == expectedInstant.UtcTicks;
        return Equals(actual, expected.Value);
    }

    private static bool CompareBound(ColumnRef column, object actual, Bound bound, bool isLower)
    {
        if (column.Type == QueryType.Binary)
            return false;
        if (bound.Value.Kind == QueryConstantKind.Null || bound.Value.Value is null)
            return false;
        var comparison = CompareValues(column, actual, bound.Value.Value);
        if (comparison is not int value)
            return false;
        if (isLower)
            return bound.IsInclusive ? value >= 0 : value > 0;
        return bound.IsInclusive ? value <= 0 : value < 0;
    }

    private static int? CompareValues(ColumnRef column, object left, object right)
    {
        if (left is string leftText && right is string rightText)
            return column.StringComparison == QueryStringComparisonPolicy.Ordinal
                ? string.CompareOrdinal(leftText, rightText)
                : null;
        if (left is DateTimeOffset leftInstant && right is DateTimeOffset rightInstant)
            return leftInstant.UtcTicks.CompareTo(rightInstant.UtcTicks);
        if (left is Guid leftGuid && right is Guid rightGuid)
            return PortableValueComparison.CompareGuid(leftGuid, rightGuid);
        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return PortableValueComparison.CompareBinary(leftBytes, rightBytes);
        if (left is IComparable comparable)
            return comparable.CompareTo(right);
        return null;
    }

    private static bool ApplyComparison(int comparison, CompareOp op) => op switch
    {
        CompareOp.Equal => comparison == 0,
        CompareOp.NotEqual => comparison != 0,
        CompareOp.LessThan => comparison < 0,
        CompareOp.LessThanOrEqual => comparison <= 0,
        CompareOp.GreaterThan => comparison > 0,
        CompareOp.GreaterThanOrEqual => comparison >= 0,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    private static int IndexOf(string value, string needle, QueryStringComparisonPolicy policy) =>
        policy == QueryStringComparisonPolicy.Ordinal ? value.IndexOf(needle, StringComparison.Ordinal) : -1;

    private static bool EndsWith(string value, string needle, QueryStringComparisonPolicy policy) =>
        policy == QueryStringComparisonPolicy.Ordinal && value.EndsWith(needle, StringComparison.Ordinal);

    private static int ElementIndexOf(string value, string needle, QueryStringComparisonPolicy policy) => policy switch
    {
        QueryStringComparisonPolicy.Ordinal => value.IndexOf(needle, StringComparison.Ordinal),
        // The oracle keeps the complete .NET behavior for shapes that are explicitly refused by
        // planning. Admitted raw-array providers are limited to Ordinal and the exact ASCII fold.
        QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase => value.IndexOf(needle, StringComparison.OrdinalIgnoreCase),
        QueryStringComparisonPolicy.AsciiIgnoreCase => AsciiIndexOf(value, needle),
        _ => -1
    };

    private static bool ElementEndsWith(string value, string needle, QueryStringComparisonPolicy policy) => policy switch
    {
        QueryStringComparisonPolicy.Ordinal => value.EndsWith(needle, StringComparison.Ordinal),
        QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase => value.EndsWith(needle, StringComparison.OrdinalIgnoreCase),
        QueryStringComparisonPolicy.AsciiIgnoreCase => needle.Length == 0 || AsciiIndexOf(value, needle) == value.Length - needle.Length,
        _ => false
    };

    private static int AsciiIndexOf(string value, string needle)
    {
        if (needle.Length == 0)
            return 0;
        if (needle.Length > value.Length)
            return -1;
        for (var start = 0; start <= value.Length - needle.Length; start++)
        {
            var match = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (FoldAscii(value[start + offset]) != FoldAscii(needle[offset]))
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return start;
        }
        return -1;
    }

    private static char FoldAscii(char value) => value is >= 'A' and <= 'Z'
        ? (char)(value + ('a' - 'A'))
        : value;
}

internal static class PortableValueComparison
{
    internal static int CompareGuid(Guid left, Guid right) => CompareBytes(GuidBytes(left), GuidBytes(right));

    internal static int CompareBinary(byte[] left, byte[] right) => CompareBytes(left, right);

    private static byte[] GuidBytes(Guid value)
    {
        var text = value.ToString("D", CultureInfo.InvariantCulture).Replace("-", string.Empty);
        var bytes = new byte[16];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = byte.Parse(text.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0)
                return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }
}
