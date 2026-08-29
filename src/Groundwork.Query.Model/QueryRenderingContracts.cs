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
    public QueryIndexColumn(string column, bool isNullable = true, QueryType? type = null)
    {
        if (string.IsNullOrWhiteSpace(column))
            throw new ArgumentException("An index column cannot be blank.", nameof(column));
        Column = column;
        IsNullable = isNullable;
        Type = type;
    }

    public string Column { get; }
    public bool IsNullable { get; }

    /// <summary>The declared value type, when the provider has enough schema context to supply it.</summary>
    public QueryType? Type { get; }
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
        : this(name, columns, pinning, includesNulls, nullableColumns, null)
    {
    }

    private QueryIndexDeclaration(
        string name,
        IEnumerable<string> columns,
        QueryIndexPinning pinning,
        bool includesNulls,
        IEnumerable<string>? nullableColumns,
        IReadOnlyDictionary<string, QueryType?>? columnTypes)
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
        ColumnTypes = (columnTypes ?? new Dictionary<string, QueryType?>())
            .Where(pair => Columns.Contains(pair.Key, StringComparer.Ordinal))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
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
        : this(name, shape.Names, pinning, includesNulls, shape.NullableNames, shape.Types)
    {
    }

    public string Name { get; }
    public ImmutableArray<string> Columns { get; }
    public QueryIndexPinning Pinning { get; }
    public bool IncludesNulls { get; }
    public ImmutableHashSet<string> NullableColumns { get; }
    public IReadOnlyDictionary<string, QueryType?> ColumnTypes { get; }

    /// <summary>Returns this declaration with provider-resolved query types attached to its columns.</summary>
    public QueryIndexDeclaration WithColumnTypes(IReadOnlyDictionary<string, QueryType?> columnTypes)
    {
        if (columnTypes is null)
            throw new ArgumentNullException(nameof(columnTypes));
        return new QueryIndexDeclaration(
            Name,
            Columns.Select(column => new QueryIndexColumn(
                column,
                NullableColumns.Contains(column),
                columnTypes.TryGetValue(column, out var type) ? type : null)),
            Pinning,
            IncludesNulls);
    }

    private sealed record IndexShape(
        ImmutableArray<string> Names,
        ImmutableArray<string> NullableNames,
        IReadOnlyDictionary<string, QueryType?> Types);

    private static IndexShape Shape(IEnumerable<QueryIndexColumn> columns)
    {
        if (columns is null)
            throw new ArgumentNullException(nameof(columns));
        var snapshot = columns.ToArray();
        if (snapshot.Any(column => column is null))
            throw new ArgumentException("Index columns cannot contain null references.", nameof(columns));
        return new(
            snapshot.Select(column => column.Column).ToImmutableArray(),
            snapshot.Where(column => column.IsNullable).Select(column => column.Column).ToImmutableArray(),
            snapshot.Where(column => column.Type is not null)
                .ToDictionary(column => column.Column, column => column.Type, StringComparer.Ordinal));
    }
}

/// <summary>Provider-neutral policy for a physical prefix search-key column.</summary>
public enum QuerySearchKeyPolicy
{
    Ordinal,
    AsciiIgnoreCase,
    UnicodeOrdinalIgnoreCase
}

/// <summary>Maps one logical text column to its physical search-key representation.</summary>
public sealed record QuerySearchKeyColumn
{
    public QuerySearchKeyColumn(
        string sourceColumn,
        string physicalColumn,
        QuerySearchKeyPolicy policy,
        int? maxLength = null,
        bool orderByPhysicalColumn = false,
        bool supportsPrefixPredicates = true)
    {
        if (string.IsNullOrWhiteSpace(sourceColumn))
            throw new ArgumentException("A source column is required.", nameof(sourceColumn));
        if (string.IsNullOrWhiteSpace(physicalColumn))
            throw new ArgumentException("A physical column is required.", nameof(physicalColumn));
        SourceColumn = sourceColumn;
        PhysicalColumn = physicalColumn;
        Policy = policy;
        MaxLength = maxLength;
        OrderByPhysicalColumn = orderByPhysicalColumn;
        SupportsPrefixPredicates = supportsPrefixPredicates;
    }

    public string SourceColumn { get; }
    public string PhysicalColumn { get; }
    public QuerySearchKeyPolicy Policy { get; }
    public int? MaxLength { get; }
    /// <summary>Whether logical ordering is executed against this persisted key.</summary>
    public bool OrderByPhysicalColumn { get; }
    /// <summary>Whether prefix predicates may be lowered through this mapping.</summary>
    public bool SupportsPrefixPredicates { get; }
}

/// <summary>Provider-neutral knobs passed to a native query renderer.</summary>
public sealed record QueryRenderOptions
{
    public QueryRenderOptions(
        IEnumerable<QueryIndexDeclaration>? indexes = null,
        string? selectedIndex = null,
        IEnumerable<ColumnRef>? tieBreakColumns = null,
        IEnumerable<ColumnRef>? drivingIdentityColumns = null)
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
        DrivingIdentityColumns = (drivingIdentityColumns ?? Array.Empty<ColumnRef>()).ToImmutableArray();
        if (DrivingIdentityColumns.Any(column => column is null))
            throw new ArgumentException("Driving identity columns cannot contain null references.", nameof(drivingIdentityColumns));
        PhysicalIndexNames = ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
        SearchKeyColumns = ImmutableDictionary<string, QuerySearchKeyColumn>.Empty.WithComparers(StringComparer.Ordinal);
        LatestPartitionColumns = ImmutableArray<ColumnRef>.Empty;
    }

    /// <summary>Provider defaults are used unless a declaration explicitly requests pinning.</summary>
    public static QueryRenderOptions Default { get; } = new();

    public ImmutableArray<QueryIndexDeclaration> Indexes { get; init; }
    public string? SelectedIndex { get; }
    /// <summary>
    /// Identity and additional tie-break columns appended to the requested order for deterministic
    /// paging. A joined request appends its declared target key after the driving identity.
    /// </summary>
    public ImmutableArray<ColumnRef> TieBreakColumns { get; init; }

    /// <summary>
    /// Complete declared identity of the driving table. Joined continuation ordering always
    /// includes these columns in this order, even when a caller supplies only a partial set of
    /// additional tie-break columns.
    /// </summary>
    public ImmutableArray<ColumnRef> DrivingIdentityColumns { get; init; }

    /// <summary>Provider-resolved driving identity used to verify a joined continuation declaration.</summary>
    internal ImmutableArray<ColumnRef> ResolvedDrivingIdentityColumns { get; init; }

    /// <summary>Provider-owned partition columns added to LatestPerKey grouping.</summary>
    public ImmutableArray<ColumnRef> LatestPartitionColumns { get; init; }

    /// <summary>Provider-resolved physical names for declared logical indexes.</summary>
    public IReadOnlyDictionary<string, string> PhysicalIndexNames { get; init; }

    /// <summary>Provider-resolved logical-to-physical prefix search-key mappings.</summary>
    public IReadOnlyDictionary<string, QuerySearchKeyColumn> SearchKeyColumns { get; init; }

    /// <summary>The maximum number of distinct values in one <c>In</c> predicate.</summary>
    public int InValueLimit { get; init; } = 1_000;

    public string ResolvePhysicalIndexName(string logicalName) =>
        PhysicalIndexNames.TryGetValue(logicalName, out var physicalName) ? physicalName : logicalName;

    /// <summary>
    /// Records the provider-resolved complete driving identity and appends it as deterministic
    /// paging tie-breaks.
    /// </summary>
    public QueryRenderOptions WithIdentityTieBreaks(IEnumerable<ColumnRef> identityColumns)
    {
        if (identityColumns is null)
            throw new ArgumentNullException(nameof(identityColumns));
        var identitySnapshot = identityColumns.ToArray();
        if (identitySnapshot.Any(column => column is null))
            throw new ArgumentException("Driving identity columns cannot contain null references.", nameof(identityColumns));
        var candidates = TieBreakColumns
            .Concat(identitySnapshot)
            .Where(column => column is not null)
            .ToArray();
        var preserveQualification = candidates.All(column => column.Table != TableId.Empty);
        var merged = candidates
            .GroupBy(column => (preserveQualification ? column.Table : TableId.Empty, column.Name))
            .Select(group => group.First())
            .ToImmutableArray();
        return this with
        {
            TieBreakColumns = merged,
            DrivingIdentityColumns = DrivingIdentityColumns.Length == 0
                ? identitySnapshot.ToImmutableArray()
                : DrivingIdentityColumns,
            ResolvedDrivingIdentityColumns = identitySnapshot.ToImmutableArray()
        };
    }

    /// <summary>
    /// Returns the requested order followed by the driving identity and, for a joined request,
    /// the declared target key in reference order.
    /// </summary>
    public ImmutableArray<OrderTerm> GetEffectiveOrder(QueryRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var terms = request.Order.ToList();
        if (request.Join is { } join)
        {
            foreach (var tieBreak in TieBreakColumns)
            {
                if (tieBreak.Table == TableId.Empty ||
                    tieBreak.Table != request.Table && tieBreak.Table != join.TargetTable)
                {
                    throw new ArgumentException(
                        "Every joined-query identity tie-break must be qualified as the source or target table.",
                        nameof(request));
                }
                if (tieBreak.Table == join.TargetTable &&
                    !join.ColumnPairs.Any(pair =>
                        ColumnRefIdentity.SameQualifiedColumn(pair.Target, tieBreak)))
                {
                    throw new ArgumentException(
                        "Target identity tie-breaks must belong to the declared reference target key.",
                        nameof(request));
                }
            }

            var drivingIdentity = DrivingIdentityColumns.Length == 0
                ? TieBreakColumns.Where(column => column.Table == request.Table).ToArray()
                : DrivingIdentityColumns.ToArray();
            foreach (var column in drivingIdentity)
                terms.Add(new OrderTerm(column, OrderDirection.Ascending, NullOrder.First));

            foreach (var tieBreak in TieBreakColumns.Where(column =>
                         column.Table == request.Table &&
                         !drivingIdentity.Any(identityColumn =>
                             ColumnRefIdentity.SameQualifiedColumn(identityColumn, column))))
            {
                if (!terms.Any(term => ColumnRefIdentity.SameQualifiedColumn(term.Column, tieBreak)))
                    terms.Add(new OrderTerm(tieBreak, OrderDirection.Ascending, NullOrder.First));
            }

            foreach (var pair in join.ColumnPairs)
                terms.Add(new OrderTerm(pair.Target, OrderDirection.Ascending, NullOrder.First));
            return terms.ToImmutableArray();
        }

        foreach (var tieBreak in TieBreakColumns)
        {
            if (terms.Any(term => ColumnRefIdentity.SameName(term.Column, tieBreak)))
                continue;
            terms.Add(new OrderTerm(tieBreak, OrderDirection.Ascending, NullOrder.First));
        }
        return terms.ToImmutableArray();
    }

    public QueryIndexDeclaration? FindPinnedIndex()
    {
        var selected = FindSelectedIndex();
        return selected?.Pinning == QueryIndexPinning.Pinned ? selected : null;
    }

    /// <summary>
    /// Returns the declared index named by <see cref="SelectedIndex"/>, or the first pinned
    /// declaration when no name was supplied. Providers may use this as an explain expectation
    /// without turning a provider-default declaration into a native hint.
    /// </summary>
    public QueryIndexDeclaration? FindSelectedIndex()
    {
        var selected = SelectedIndex is null
            ? null
            : Indexes.SingleOrDefault(index => string.Equals(index.Name, SelectedIndex, StringComparison.Ordinal));
        if (SelectedIndex is not null && selected is null)
            throw new ArgumentException($"Selected index '{SelectedIndex}' was not declared.", nameof(SelectedIndex));
        return selected ?? Indexes.SingleOrDefault(index => index.Pinning == QueryIndexPinning.Pinned);
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

/// <summary>A deterministic typed continuation token for the effective identity order of a query.</summary>
public static class QueryContinuationToken
{
    private const string Prefix = "q1.";
    private const string BoundPrefix = "q2.";

    public static string Encode(IEnumerable<QueryConstant> values)
    {
        if (values is null)
            throw new ArgumentNullException(nameof(values));
        return Prefix + string.Join(".", values.Select(value => EncodeValue(value ?? throw new ArgumentException("Continuation values cannot contain null references.", nameof(values)))));
    }

    /// <summary>Encodes a cursor bound to the query shape and its effective identity order.</summary>
    public static string Encode(QueryRequest request, QueryRenderOptions options, IEnumerable<QueryConstant> values)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (options is null) throw new ArgumentNullException(nameof(options));
        var snapshot = (values ?? throw new ArgumentNullException(nameof(values))).ToArray();
        RequireJoinedSourceIdentity(request, options);
        var order = options.GetEffectiveOrder(request);
        if (snapshot.Length != order.Length)
            throw new ArgumentException("A continuation must contain one value per effective order term.", nameof(values));
        var boundValues = new QueryConstant[snapshot.Length];
        for (var index = 0; index < snapshot.Length; index++)
        {
            var value = snapshot[index] ??
                throw new ArgumentException("Continuation values cannot contain null references.", nameof(values));
            try
            {
                boundValues[index] = value.Bind(order[index].Column);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    $"Continuation value {index} is not valid for effective order term '{order[index].Column}'.",
                    nameof(values),
                    exception);
            }
        }
        var binding = Binding(request, order);
        return BoundPrefix + EncodeText(binding) + "." + string.Join(".", boundValues.Select(EncodeValue));
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

    /// <summary>Decodes and verifies a cursor against the request shape and effective order.</summary>
    public static IReadOnlyList<QueryConstant> Decode(string token, QueryRequest request, QueryRenderOptions options)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("A continuation token cannot be blank.", nameof(token));
        if (!token.StartsWith(BoundPrefix, StringComparison.Ordinal))
            throw new FormatException("The continuation token is unbound; issue a new token from the current query result.");

        RequireJoinedSourceIdentity(request, options);
        var parts = token.Substring(BoundPrefix.Length).Split('.');
        var order = options.GetEffectiveOrder(request);
        if (parts.Length != order.Length + 1)
            throw new FormatException("The continuation token does not contain one value per effective order term.");
        var expectedBinding = Binding(request, order);
        if (!string.Equals(DecodeText(parts[0]), expectedBinding, StringComparison.Ordinal))
            throw new FormatException("The continuation token belongs to a different query shape or identity order.");
        return parts.Skip(1).Select((item, index) => DecodeValue(item, order[index].Column)).ToArray();
    }

    private static string EncodeValue(QueryConstant value)
    {
        var bytes = Encoding.UTF8.GetBytes(value.ToCanonicalString());
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string EncodeText(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodeText(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string OrderBinding(OrderTerm term) =>
        PredicateCanonicalizer.Column(term.Column) + ":" + term.Direction + ":" + term.NullOrder;

    private static string Binding(QueryRequest request, IEnumerable<OrderTerm> order) =>
        request.CanonicalPredicate + "|" + request.ContinuationFingerprint + "|" +
        request.ContinuationBindingDiscriminator + "|" + string.Join(";", order.Select(OrderBinding));

    private static void RequireJoinedSourceIdentity(QueryRequest request, QueryRenderOptions options)
    {
        if (request.Join is null)
            return;

        var declaredIdentity = options.DrivingIdentityColumns;
        var resolvedIdentity = options.ResolvedDrivingIdentityColumns;
        if (declaredIdentity.Length == 0 || resolvedIdentity.Length == 0)
        {
            throw new ArgumentException(
                "A joined continuation requires the complete declared driving identity resolved from the source schema.",
                nameof(options));
        }

        if (declaredIdentity.Any(column => column is null || column.Table != request.Table) ||
            resolvedIdentity.Any(column => column is null || column.Table != request.Table))
            throw new ArgumentException(
                "Every declared driving identity column must belong to the joined query source table.",
                nameof(options));

        if (declaredIdentity.GroupBy(column => (column.Table, column.Name)).Any(group => group.Count() != 1) ||
            resolvedIdentity.GroupBy(column => (column.Table, column.Name)).Any(group => group.Count() != 1))
            throw new ArgumentException(
                "A joined continuation requires every declared driving identity component exactly once.",
                nameof(options));

        if (declaredIdentity.Length != resolvedIdentity.Length ||
            declaredIdentity.Where((column, index) =>
                !ColumnRefIdentity.SameQualifiedColumn(column, resolvedIdentity[index])).Any())
            throw new ArgumentException(
                "The declared driving identity must match the source schema identity in order and completeness.",
                nameof(options));

        var effectiveOrder = options.GetEffectiveOrder(request);
        var identityIndex = 0;
        foreach (var term in effectiveOrder)
        {
            if (identityIndex < resolvedIdentity.Length &&
                ColumnRefIdentity.SameQualifiedColumn(term.Column, resolvedIdentity[identityIndex]))
                identityIndex++;
        }
        if (identityIndex != resolvedIdentity.Length)
            throw new ArgumentException(
                "The effective joined order must contain the complete driving identity in declaration order.",
                nameof(options));
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
