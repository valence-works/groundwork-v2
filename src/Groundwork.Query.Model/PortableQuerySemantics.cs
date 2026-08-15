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

public sealed record PortableSemanticDiagnostic(string Code, string Message, string Path)
{
    public PortableSemanticDiagnostic(string code, string message)
        : this(code, message, string.Empty)
    {
    }
}

public sealed class PortableSemanticValidationResult
{
    internal PortableSemanticValidationResult(IReadOnlyList<PortableSemanticDiagnostic> diagnostics)
    {
        Diagnostics = new ReadOnlyCollection<PortableSemanticDiagnostic>(diagnostics.ToArray());
    }

    public IReadOnlyList<PortableSemanticDiagnostic> Diagnostics { get; }
    public bool IsPortable => Diagnostics.Count == 0;
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

        var diagnostics = new List<PortableSemanticDiagnostic>();
        ValidatePredicate(predicate, diagnostics, "predicate");
        return new PortableSemanticValidationResult(diagnostics);
    }

    public static PortableSemanticValidationResult Validate(QueryRequest request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var diagnostics = new List<PortableSemanticDiagnostic>();
        ValidatePredicate(request.Where, diagnostics, "where");

        foreach (var term in request.Order)
        {
            ValidateColumn(term.Column, diagnostics, "order." + term.Column.Name);
            if (term.Column.Type is QueryType.Binary or QueryType.Double)
                Refuse(diagnostics, "GW-SEM-ORDER-001", "Ordering this type is not portable; order a declared portable projection or key instead.", "order." + term.Column.Name);
            if (term.Column.Type == QueryType.String && IsCultureDependent(term.Column.StringComparison))
                Refuse(diagnostics, "GW-SEM-TEXT-001", "Culture-dependent text ordering is not portable; use Ordinal or a persisted ordinal-ignore-case search key instead.", "order." + term.Column.Name);
        }

        if (request.LatestPerKey is not null)
        {
            ValidateColumn(request.LatestPerKey.Key, diagnostics, "latestPerKey.key");
            ValidateColumn(request.LatestPerKey.Timestamp, diagnostics, "latestPerKey.timestamp");
            if (request.LatestPerKey.Timestamp.Type != QueryType.DateTimeOffset || request.LatestPerKey.Timestamp.IsNullable)
                Refuse(diagnostics, "GW-SEM-LATEST-001", "Latest-per-key requires a non-null DateTimeOffset timestamp; project a non-null UTC timestamp instead.", "latestPerKey.timestamp");
        }

        return new PortableSemanticValidationResult(diagnostics);
    }

    public static bool Evaluate(Predicate predicate, IReadOnlyDictionary<string, object?> row)
    {
        if (predicate is null)
            throw new ArgumentNullException(nameof(predicate));
        if (row is null)
            throw new ArgumentNullException(nameof(row));

        return EvaluateCore(predicate, row);
    }

    private static void ValidatePredicate(Predicate predicate, ICollection<PortableSemanticDiagnostic> diagnostics, string path)
    {
        switch (predicate)
        {
            case Predicate.AlwaysTrue:
            case Predicate.AlwaysFalse:
                return;
            case Predicate.Equal equal:
                ValidateColumn(equal.Column, diagnostics, path + ".column");
                ValidateConstant(equal.Column, equal.Value, diagnostics, path + ".value");
                return;
            case Predicate.In membership:
                ValidateColumn(membership.Column, diagnostics, path + ".column");
                foreach (var (value, index) in membership.Values.Select((value, index) => (value, index)))
                    ValidateConstant(membership.Column, value, diagnostics, path + ".values[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                return;
            case Predicate.Range range:
                ValidateColumn(range.Column, diagnostics, path + ".column");
                ValidateRangeType(range.Column, diagnostics, path);
                if (range.Lower is not null)
                    ValidateConstant(range.Column, range.Lower.Value, diagnostics, path + ".lower");
                if (range.Upper is not null)
                    ValidateConstant(range.Column, range.Upper.Value, diagnostics, path + ".upper");
                if (range.Lower?.Value.Kind == QueryConstantKind.Null || range.Upper?.Value.Kind == QueryConstantKind.Null)
                    Refuse(diagnostics, "GW-SEM-NULL-001", "A range with a null operand is not portable; use Equal(column, null) or its total complement instead.", path);
                return;
            case Predicate.StartsWith startsWith:
                ValidateColumn(startsWith.Column, diagnostics, path + ".column");
                Refuse(diagnostics, "GW-SEM-TEXT-002", "Prefix matching is not portable for this contract; use a persisted, versioned search key and Substring instead.", path);
                return;
            case Predicate.Substring substring:
                ValidateColumn(substring.Column, diagnostics, path + ".column");
                if (substring.Anchor is not (Anchor.Contains or Anchor.EndsWith))
                    Refuse(diagnostics, "GW-SEM-TEXT-003", "The requested substring anchor is not portable; use Contains or a persisted search key.", path);
                return;
            case Predicate.ElementOf elementOf:
                ValidateElementSet(elementOf, diagnostics, path);
                return;
            case Predicate.ColumnCompare compare:
                ValidateColumn(compare.Left, diagnostics, path + ".left");
                ValidateColumn(compare.Right, diagnostics, path + ".right");
                if (compare.Left.Type != compare.Right.Type)
                    Refuse(diagnostics, "GW-SEM-TYPE-001", "Column comparison requires an exact matching type; compare a portable projection with the same declared type instead.", path);
                if (compare.Op is not (CompareOp.Equal or CompareOp.NotEqual) && !IsOrderable(compare.Left.Type))
                    Refuse(diagnostics, "GW-SEM-ORDER-002", "Ordering comparison for this type is not portable; use equality or a declared orderable projection instead.", path);
                return;
            case Predicate.Not not:
                ValidatePredicate(not.Inner, diagnostics, path + ".inner");
                if ((not.Inner is Predicate.In negatedMembership && negatedMembership.Values.Length != 0) || not.Inner is Predicate.Range or Predicate.StartsWith or Predicate.Substring)
                    Refuse(diagnostics, "GW-SEM-NOT-001", "This negation is not portable; use the supported total Equal/ColumnCompare complement or a portable positive predicate instead.", path);
                return;
            case Predicate.And and:
                foreach (var (term, index) in and.Terms.Select((term, index) => (term, index)))
                    ValidatePredicate(term, diagnostics, path + ".terms[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                return;
            case Predicate.Or or:
                foreach (var (term, index) in or.Terms.Select((term, index) => (term, index)))
                    ValidatePredicate(term, diagnostics, path + ".terms[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                return;
            default:
                Refuse(diagnostics, "GW-SEM-UNKNOWN-001", "The predicate node is not portable; use a declared v2 predicate node instead.", path);
                return;
        }
    }

    private static void ValidateElementSet(Predicate.ElementOf elementOf, ICollection<PortableSemanticDiagnostic> diagnostics, string path)
    {
        QueryType? type = null;
        foreach (var (value, index) in elementOf.Values.Select((value, index) => (value, index)))
        {
            if (value is null)
            {
                Refuse(diagnostics, "GW-SEM-TYPE-004", "A null element-set constant is not portable; use an explicit typed constant instead.", path + ".values[" + index.ToString(CultureInfo.InvariantCulture) + "]");
                continue;
            }
            if (value.Kind == QueryConstantKind.Null)
                continue;
            if (value.Type == QueryType.Double)
                Refuse(diagnostics, "GW-SEM-TYPE-002", "Double membership is not portable; use an exact supported numeric type instead.", path + ".values[" + index.ToString(CultureInfo.InvariantCulture) + "]");
            if (type is null)
                type = value.Type;
            else if (type != value.Type)
                Refuse(diagnostics, "GW-SEM-TYPE-003", "Element-set values require one exact type; split the set or use a typed projection instead.", path);
        }
    }

    private static void ValidateConstant(ColumnRef column, QueryConstant constant, ICollection<PortableSemanticDiagnostic> diagnostics, string path)
    {
        if (constant is null)
        {
            Refuse(diagnostics, "GW-SEM-TYPE-004", "A null constant reference is not portable; use an explicit typed null constant instead.", path);
            return;
        }
        if (constant.Type != column.Type)
            Refuse(diagnostics, "GW-SEM-TYPE-005", "The constant type must exactly match the column type; bind an exact typed constant instead.", path);
        if (constant.Kind == QueryConstantKind.Null && !column.IsNullable)
            Refuse(diagnostics, "GW-SEM-NULL-002", "A null value for a non-nullable column is not portable; use a non-null value or a nullable projection instead.", path);
    }

    private static void ValidateRangeType(ColumnRef column, ICollection<PortableSemanticDiagnostic> diagnostics, string path)
    {
        if (!IsOrderable(column.Type))
            Refuse(diagnostics, "GW-SEM-ORDER-003", "Range ordering for this type is not portable; use equality/membership or a declared orderable projection instead.", path);
    }

    private static void ValidateColumn(ColumnRef column, ICollection<PortableSemanticDiagnostic> diagnostics, string path)
    {
        if (column.Type == QueryType.Double)
            Refuse(diagnostics, "GW-SEM-TYPE-006", "Binary floating-point values are not portable in predicates or indexes; use Int32, Int64, or declared Decimal instead.", path);
        if (column.Type == QueryType.Decimal && (column.DecimalPrecision != 18 || column.DecimalScale != 4))
            Refuse(diagnostics, "GW-SEM-DECIMAL-001", "Portable Decimal requires declared decimal(18,4) with no rounding; use decimal(18,4) or an exact integer type instead.", path);
        if (column.Type == QueryType.String && IsCultureDependent(column.StringComparison))
            Refuse(diagnostics, "GW-SEM-TEXT-001", "Culture-dependent text comparison is not portable; use Ordinal or a versioned ordinal-ignore-case search key instead.", path);
    }

    private static bool IsCultureDependent(QueryStringComparisonPolicy policy) => policy is
        QueryStringComparisonPolicy.CurrentCulture or
        QueryStringComparisonPolicy.InvariantCulture or
        QueryStringComparisonPolicy.AccentInsensitive or
        QueryStringComparisonPolicy.Icu;

    private static bool IsOrderable(QueryType type) => type is
        QueryType.Boolean or QueryType.Int32 or QueryType.Int64 or QueryType.Decimal or QueryType.String or QueryType.DateTimeOffset or QueryType.Guid;

    private static void Refuse(ICollection<PortableSemanticDiagnostic> diagnostics, string code, string message, string path) =>
        diagnostics.Add(new PortableSemanticDiagnostic(code, message, path));

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
                if (range.Lower is not null && !CompareBound(range.Column, actual, range.Lower))
                    return false;
                return range.Upper is null || CompareBound(range.Column, actual, range.Upper);
            }
            case Predicate.StartsWith startsWith:
            {
                var actual = GetValue(startsWith.Column, row) as string;
                return actual is not null && actual.StartsWith(startsWith.Prefix, StringComparisonFor(startsWith.Column.StringComparison));
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
                var elements = GetElements(elementOf.Set.Name, row);
                return elementOf.Quantifier == SetQuantifier.Any
                    ? elements.Any(element => elementOf.Values.Any(value => CompareUntyped(element, value)))
                    : elementOf.Values.All(value => elements.Any(element => CompareUntyped(element, value)));
            }
            case Predicate.ColumnCompare compare:
            {
                var left = GetValue(compare.Left, row);
                var right = GetValue(compare.Right, row);
                if (left is null || right is null)
                    return false;
                return ApplyComparison(CompareValues(compare.Left, left, right), compare.Op);
            }
            case Predicate.Not not:
                return !EvaluateCore(not.Inner, row);
            case Predicate.And and:
                return and.Terms.All(term => EvaluateCore(term, row));
            case Predicate.Or or:
                return or.Terms.Any(term => EvaluateCore(term, row));
            default:
                throw new InvalidOperationException("Unknown predicate node.");
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

    private static IEnumerable<object?> GetElements(string name, IReadOnlyDictionary<string, object?> row)
    {
        if (!row.TryGetValue(name, out var value) || value is null)
            return Array.Empty<object?>();
        if (value is string or byte[])
            return new[] { value };
        if (value is IEnumerable enumerable)
            return enumerable.Cast<object?>().ToArray();
        return new[] { value };
    }

    private static bool CompareEqual(ColumnRef column, object? actual, QueryConstant expected)
    {
        if (expected.Kind == QueryConstantKind.Null)
            return actual is null;
        if (actual is null)
            return false;
        return CompareUntyped(actual, expected, column.StringComparison);
    }

    private static bool CompareUntyped(object? actual, QueryConstant expected, QueryStringComparisonPolicy policy = QueryStringComparisonPolicy.Ordinal)
    {
        if (expected.Kind == QueryConstantKind.Null)
            return actual is null;
        if (actual is null)
            return false;
        if (!IsExactRuntimeType(expected.Type!.Value, actual))
            throw new ArgumentException("Element value must have exact type " + expected.Type + ".", nameof(actual));
        if (actual is string actualText && expected.Value is string expectedText)
            return string.Equals(actualText, expectedText, StringComparisonFor(policy));
        if (actual is byte[] actualBytes && expected.Value is byte[] expectedBytes)
            return actualBytes.SequenceEqual(expectedBytes);
        if (actual is DateTimeOffset actualInstant && expected.Value is DateTimeOffset expectedInstant)
            return actualInstant.UtcTicks == expectedInstant.UtcTicks;
        return Equals(actual, expected.Value);
    }

    private static bool CompareBound(ColumnRef column, object actual, Bound bound)
    {
        var comparison = CompareValues(column, actual, bound.Value.Value!);
        return bound.IsInclusive ? comparison >= 0 : comparison > 0;
    }

    private static int CompareValues(ColumnRef column, object left, object right)
    {
        if (left is string leftText && right is string rightText)
            return StringComparerFor(column.StringComparison).Compare(leftText, rightText);
        if (left is DateTimeOffset leftInstant && right is DateTimeOffset rightInstant)
            return leftInstant.UtcTicks.CompareTo(rightInstant.UtcTicks);
        if (left is Guid leftGuid && right is Guid rightGuid)
            return CompareBytes(GuidBytes(leftGuid), GuidBytes(rightGuid));
        if (left is IComparable comparable)
            return comparable.CompareTo(right);
        throw new ArgumentException("Values of type " + column.Type + " are not orderable.");
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

    private static int IndexOf(string value, string needle, QueryStringComparisonPolicy policy) =>
        value.IndexOf(needle, StringComparisonFor(policy));

    private static bool EndsWith(string value, string needle, QueryStringComparisonPolicy policy) =>
        value.EndsWith(needle, StringComparisonFor(policy));

    private static StringComparison StringComparisonFor(QueryStringComparisonPolicy policy) => policy switch
    {
        QueryStringComparisonPolicy.Ordinal => StringComparison.Ordinal,
        QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase or QueryStringComparisonPolicy.AsciiIgnoreCase => StringComparison.OrdinalIgnoreCase,
        _ => throw new InvalidOperationException("Culture-dependent text comparison is not portable.")
    };

    private static StringComparer StringComparerFor(QueryStringComparisonPolicy policy) => policy switch
    {
        QueryStringComparisonPolicy.Ordinal => StringComparer.Ordinal,
        QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase or QueryStringComparisonPolicy.AsciiIgnoreCase => StringComparer.OrdinalIgnoreCase,
        _ => throw new InvalidOperationException("Culture-dependent text comparison is not portable.")
    };
}
