using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Groundwork.Query.Model;

/// <summary>Controls whether a declared index may be pinned in a native query.</summary>
public enum QueryIndexPinning
{
    /// <summary>Leave index choice to the provider. This is the default.</summary>
    ProviderDefault,

    /// <summary>Pin the declaration on providers that support native hints.</summary>
    Pinned,

    /// <summary>Explicitly avoid a native index hint.</summary>
    Unpinned
}

/// <summary>One query-visible index key and its nullable declaration.</summary>
public sealed record QueryIndexColumn
{
    public QueryIndexColumn(string column, bool isNullable = true)
    {
        if (string.IsNullOrWhiteSpace(column))
            throw new ArgumentException("An index column cannot be blank.", nameof(column));
        Column = column;
        IsNullable = isNullable;
    }

    public string Column { get; }
    public bool IsNullable { get; }
}

/// <summary>A query-visible index declaration used to control native index selection.</summary>
public sealed record QueryIndexDeclaration
{
    public QueryIndexDeclaration(
        string name,
        IEnumerable<string> columns,
        QueryIndexPinning pinning = QueryIndexPinning.ProviderDefault,
        bool includesNulls = true,
        IEnumerable<string>? nullableColumns = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An index name cannot be blank.", nameof(name));
        if (!Enum.IsDefined(typeof(QueryIndexPinning), pinning))
            throw new ArgumentOutOfRangeException(nameof(pinning), pinning, null);

        Name = name;
        Columns = (columns ?? throw new ArgumentNullException(nameof(columns))).ToImmutableArray();
        if (Columns.Length == 0 || Columns.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("An index must declare at least one non-blank column.", nameof(columns));
        if (Columns.Distinct(StringComparer.Ordinal).Count() != Columns.Length)
            throw new ArgumentException("An index cannot contain duplicate columns.", nameof(columns));
        Pinning = pinning;
        IncludesNulls = includesNulls;
        NullableColumns = (nullableColumns ?? Columns).ToImmutableHashSet(StringComparer.Ordinal);
        if (NullableColumns.Any(column => !Columns.Contains(column, StringComparer.Ordinal)))
            throw new ArgumentException("Nullable index columns must be declared index columns.", nameof(nullableColumns));
    }

    public QueryIndexDeclaration(
        string name,
        IEnumerable<QueryIndexColumn> columns,
        QueryIndexPinning pinning = QueryIndexPinning.ProviderDefault,
        bool includesNulls = true)
        : this(name, Shape(columns), pinning, includesNulls)
    {
    }

    private QueryIndexDeclaration(string name, IndexShape shape, QueryIndexPinning pinning, bool includesNulls)
        : this(name, shape.Names, pinning, includesNulls, shape.NullableNames)
    {
    }

    public string Name { get; }
    public ImmutableArray<string> Columns { get; }
    public QueryIndexPinning Pinning { get; }
    public bool IncludesNulls { get; }
    public ImmutableHashSet<string> NullableColumns { get; }

    private sealed record IndexShape(ImmutableArray<string> Names, ImmutableArray<string> NullableNames);

    private static IndexShape Shape(IEnumerable<QueryIndexColumn> columns)
    {
        if (columns is null)
            throw new ArgumentNullException(nameof(columns));
        var snapshot = columns.ToArray();
        if (snapshot.Any(column => column is null))
            throw new ArgumentException("Index columns cannot contain null references.", nameof(columns));
        return new(
            snapshot.Select(column => column.Column).ToImmutableArray(),
            snapshot.Where(column => column.IsNullable).Select(column => column.Column).ToImmutableArray());
    }
}

/// <summary>Provider-neutral knobs passed to a native query renderer.</summary>
public sealed record QueryRenderOptions
{
    public QueryRenderOptions(
        IEnumerable<QueryIndexDeclaration>? indexes = null,
        string? selectedIndex = null,
        IEnumerable<ColumnRef>? tieBreakColumns = null)
    {
        Indexes = (indexes ?? Array.Empty<QueryIndexDeclaration>()).ToImmutableArray();
        if (Indexes.Any(index => index is null))
            throw new ArgumentException("Index declarations cannot contain null references.", nameof(indexes));
        if (selectedIndex is not null && string.IsNullOrWhiteSpace(selectedIndex))
            throw new ArgumentException("A selected index cannot be blank.", nameof(selectedIndex));
        SelectedIndex = selectedIndex;
        TieBreakColumns = (tieBreakColumns ?? Array.Empty<ColumnRef>()).ToImmutableArray();
        if (TieBreakColumns.Any(column => column is null))
            throw new ArgumentException("Tie-break columns cannot contain null references.", nameof(tieBreakColumns));
    }

    /// <summary>Provider defaults are used unless a declaration explicitly requests pinning.</summary>
    public static QueryRenderOptions Default { get; } = new();

    public ImmutableArray<QueryIndexDeclaration> Indexes { get; }
    public string? SelectedIndex { get; }
    /// <summary>Declared identity columns appended to the requested order for deterministic paging.</summary>
    public ImmutableArray<ColumnRef> TieBreakColumns { get; }

    /// <summary>The maximum number of distinct values in one <c>In</c> predicate.</summary>
    public int InValueLimit { get; init; } = 1_000;

    public QueryIndexDeclaration? FindPinnedIndex()
    {
        var selected = SelectedIndex is null
            ? null
            : Indexes.SingleOrDefault(index => string.Equals(index.Name, SelectedIndex, StringComparison.Ordinal));
        if (SelectedIndex is not null && selected is null)
            throw new ArgumentException($"Selected index '{SelectedIndex}' was not declared.", nameof(SelectedIndex));
        if (selected is not null)
            return selected.Pinning == QueryIndexPinning.Pinned ? selected : null;
        return Indexes.SingleOrDefault(index => index.Pinning == QueryIndexPinning.Pinned);
    }
}

/// <summary>A typed parameter emitted by a provider-neutral relational renderer.</summary>
public sealed record QueryRenderParameter
{
    public QueryRenderParameter(string name, QueryType type, object? value)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A parameter name cannot be blank.", nameof(name));
        Name = name;
        Type = type;
        Value = value is byte[] bytes ? bytes.ToArray() : value;
    }

    public string Name { get; }
    public QueryType Type { get; }
    public object? Value { get; }
}

/// <summary>A deterministic continuation token for the ordered terms of a query.</summary>
public static class QueryContinuationToken
{
    private const string Prefix = "q1.";

    public static string Encode(IEnumerable<QueryConstant> values)
    {
        if (values is null)
            throw new ArgumentNullException(nameof(values));
        return Prefix + string.Join(".", values.Select(value => EncodeValue(value ?? throw new ArgumentException("Continuation values cannot contain null references.", nameof(values)))));
    }

    public static IReadOnlyList<QueryConstant> Decode(string token, IReadOnlyList<ColumnRef> columns)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A continuation token cannot be blank.", nameof(token));
        if (columns is null)
            throw new ArgumentNullException(nameof(columns));
        if (!token.StartsWith(Prefix, StringComparison.Ordinal))
            throw new FormatException("The continuation token has an unsupported version.");

        var encoded = token.Substring(Prefix.Length).Split('.');
        if (encoded.Length != columns.Count)
            throw new FormatException("The continuation token does not contain one value per order term.");
        return encoded.Select((item, index) => DecodeValue(item, columns[index])).ToArray();
    }

    private static string EncodeValue(QueryConstant value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToCanonicalString());
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static QueryConstant DecodeValue(string encoded, ColumnRef column)
    {
        var padded = encoded.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        var canonical = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        var nullPrefix = "null<" + column.Type + ">";
        if (string.Equals(canonical, nullPrefix, StringComparison.Ordinal))
            return QueryConstant.Of(column, null);

        var separator = canonical.IndexOf(':');
        if (separator <= 0)
            throw new FormatException("The continuation token contains an invalid value.");
        var kind = canonical.Substring(0, separator);
        var value = canonical.Substring(separator + 1);
        object parsed = column.Type switch
        {
            QueryType.Boolean when kind == "boolean" => bool.Parse(value),
            QueryType.Int32 when kind == "int32" => int.Parse(value, CultureInfo.InvariantCulture),
            QueryType.Int64 when kind == "int64" => long.Parse(value, CultureInfo.InvariantCulture),
            QueryType.Decimal when kind == "decimal" => decimal.Parse(value, CultureInfo.InvariantCulture),
            QueryType.Double when kind == "double" => double.Parse(value, CultureInfo.InvariantCulture),
            QueryType.String when kind == "string" => DecodeUtf8Hex(value),
            QueryType.DateTimeOffset when kind == "datetimeoffset" =>
                new DateTimeOffset(new DateTime(long.Parse(value, CultureInfo.InvariantCulture), DateTimeKind.Utc)),
            QueryType.Guid when kind == "guid" => Guid.Parse(value),
            QueryType.Binary when kind == "binary" => Convert.FromBase64String(value),
            _ => throw new FormatException("The continuation token value type does not match its order column.")
        };
        return QueryConstant.Of(column, parsed);
    }

    private static string DecodeUtf8Hex(string value)
    {
        if (value.Length % 2 != 0)
            throw new FormatException("The continuation token contains invalid text bytes.");
        var bytes = new byte[value.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = byte.Parse(value.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>Failure raised when a normalized request cannot be safely rendered.</summary>
public sealed class QueryRenderException : InvalidOperationException
{
    public QueryRenderException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
