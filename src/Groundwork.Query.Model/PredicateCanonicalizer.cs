using System.Text;

namespace Groundwork.Query.Model;

public static class PredicateCanonicalizer
{
    public static string ToCanonicalString(Predicate predicate) => ToCanonicalString(predicate, includeValues: true);

    internal static string ToShapeString(Predicate predicate) => ToCanonicalString(predicate, includeValues: false);

    internal static string ToCanonicalString(Predicate predicate, bool includeValues)
    {
        if (predicate is null)
            throw new ArgumentNullException(nameof(predicate));
        return predicate switch
        {
            Predicate.Equal equal => "equal(" + Column(equal.Column) + "," + equal.Value.ToCanonicalString(includeValues) + ")",
            Predicate.In membership => "in(" + Column(membership.Column) + "," + string.Join(",", membership.Values.Select(value => value.ToCanonicalString(includeValues))) + ")",
            Predicate.Range range => "range(" + Column(range.Column) + "," + Bound(range.Lower, includeValues) + "," + Bound(range.Upper, includeValues) + ")",
            Predicate.StartsWith startsWith => "starts(" + Column(startsWith.Column) + "," + String(startsWith.Prefix, includeValues) + ")",
            Predicate.Substring substring => "substring(" + Column(substring.Column) + "," + substring.Anchor + "," + String(substring.Needle, includeValues) + ")",
            Predicate.ElementOf elementOf => "element-of(" + Escape(elementOf.Set.Name) + "," + (elementOf.Set.Type?.ToString() ?? "untyped") + "," + elementOf.Quantifier + "," + string.Join(",", elementOf.Values.Select(value => value.ToCanonicalString(includeValues))) + ")",
            Predicate.ColumnCompare compare => "column-compare(" + Column(compare.Left) + "," + compare.Op + "," + Column(compare.Right) + ")",
            Predicate.Not not => "not(" + ToCanonicalString(not.Inner, includeValues) + ")",
            Predicate.And and => "and(" + string.Join(",", and.Terms.Select(term => ToCanonicalString(term, includeValues))) + ")",
            Predicate.Or or => "or(" + string.Join(",", or.Terms.Select(term => ToCanonicalString(term, includeValues))) + ")",
            Predicate.AlwaysTrue => "true",
            Predicate.AlwaysFalse => "false",
            _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate.GetType().FullName)
        };
    }

    internal static string Column(ColumnRef column) =>
        "column(" + Escape(column.Table.Value) + "," + Escape(column.Name) + "," + column.Type + "," + (column.IsNullable ? "nullable" : "required") + "," + column.StringComparison + ")";

    internal static string Bound(Bound? bound, bool includeValues) => bound is null
        ? "none"
        : (bound.IsInclusive ? "inclusive:" : "exclusive:") + bound.Value.ToCanonicalString(includeValues);

    internal static string String(string value, bool includeValue) => includeValue ? "string:" + Escape(value) : "string<?>";

    internal static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length * 2);
        foreach (var character in value)
            builder.Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
