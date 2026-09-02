using System.Collections.Immutable;

namespace Groundwork.Query.Model;

public abstract record Predicate
{
    public string CanonicalForm => PredicateCanonicalizer.ToCanonicalString(this);

    public sealed record Equal : Predicate
    {
        public Equal(ColumnRef column, QueryConstant value)
        {
            Column = column ?? throw new ArgumentNullException(nameof(column));
            Value = value?.Bind(column) ?? throw new ArgumentNullException(nameof(value));
        }

        public ColumnRef Column { get; }
        public QueryConstant Value { get; }
    }

    public sealed record In : Predicate
    {
        public In(ColumnRef column, ImmutableArray<QueryConstant> values)
        {
            Column = column ?? throw new ArgumentNullException(nameof(column));
            Values = values.IsDefault
                ? ImmutableArray<QueryConstant>.Empty
                : values.Select(value => value?.Bind(column) ?? throw new ArgumentException("Membership values cannot be null references.", nameof(values))).ToImmutableArray();
        }

        public In(ColumnRef column, IEnumerable<QueryConstant> values)
            : this(column, (values ?? throw new ArgumentNullException(nameof(values))).ToImmutableArray())
        {
        }

        public ColumnRef Column { get; }
        public ImmutableArray<QueryConstant> Values { get; }
    }

    public sealed record Range : Predicate
    {
        public Range(ColumnRef column, Bound? lower, Bound? upper)
        {
            Column = column ?? throw new ArgumentNullException(nameof(column));
            Lower = Bind(lower, column);
            Upper = Bind(upper, column);
            if (Lower is null && Upper is null)
                throw new ArgumentException("A range must have a lower or upper bound.", nameof(lower));
        }

        public ColumnRef Column { get; }
        public Bound? Lower { get; }
        public Bound? Upper { get; }

        private static Bound? Bind(Bound? bound, ColumnRef column) => bound is null
            ? null
            : bound.IsInclusive
                ? Bound.Inclusive(bound.Value.Bind(column))
                : Bound.Exclusive(bound.Value.Bind(column));
    }

    public sealed record StartsWith : Predicate
    {
        public StartsWith(ColumnRef column, string prefix)
        {
            Column = RequireStringColumn(column);
            if (!QueryConstant.IsWellFormedUtf16(prefix ?? throw new ArgumentNullException(nameof(prefix))))
                throw new ArgumentException("Prefix must contain well-formed UTF-16.", nameof(prefix));
            Prefix = prefix;
        }

        public ColumnRef Column { get; }
        public string Prefix { get; }
    }

    public sealed record Substring : Predicate
    {
        public Substring(ColumnRef column, string needle, Anchor anchor)
        {
            Column = RequireStringColumn(column);
            if (!QueryConstant.IsWellFormedUtf16(needle ?? throw new ArgumentNullException(nameof(needle))))
                throw new ArgumentException("Substring needle must contain well-formed UTF-16.", nameof(needle));
            Needle = needle;
            Anchor = anchor;
        }

        public ColumnRef Column { get; }
        public string Needle { get; }
        public Anchor Anchor { get; }
    }

    public sealed record ElementOf : Predicate
    {
        public ElementOf(ElementSetRef set, ImmutableArray<QueryConstant> values, SetQuantifier quantifier)
        {
            Set = set ?? throw new ArgumentNullException(nameof(set));
            Values = values.IsDefault ? ImmutableArray<QueryConstant>.Empty : values;
            Quantifier = quantifier;
        }

        public ElementOf(ElementSetRef set, IEnumerable<QueryConstant> values, SetQuantifier quantifier)
            : this(set, (values ?? throw new ArgumentNullException(nameof(values))).ToImmutableArray(), quantifier)
        {
        }

        public ElementSetRef Set { get; }
        public ImmutableArray<QueryConstant> Values { get; }
        public SetQuantifier Quantifier { get; }
    }

    /// <summary>Matches a substring against at least one string element in a typed set.</summary>
    public sealed record ElementSubstring : Predicate
    {
        public ElementSubstring(
            ElementSetRef set,
            string needle,
            Anchor anchor,
            QueryStringComparisonPolicy stringComparison = QueryStringComparisonPolicy.Ordinal)
        {
            Set = set ?? throw new ArgumentNullException(nameof(set));
            if (!QueryConstant.IsWellFormedUtf16(needle ?? throw new ArgumentNullException(nameof(needle))))
                throw new ArgumentException("Substring needle must contain well-formed UTF-16.", nameof(needle));
            Needle = needle;
            Anchor = anchor;
            StringComparison = stringComparison;
        }

        public ElementSetRef Set { get; }
        public string Needle { get; }
        public Anchor Anchor { get; }
        public QueryStringComparisonPolicy StringComparison { get; }
    }

    public sealed record ColumnCompare : Predicate
    {
        public ColumnCompare(ColumnRef left, CompareOp op, ColumnRef right)
        {
            Left = left ?? throw new ArgumentNullException(nameof(left));
            Right = right ?? throw new ArgumentNullException(nameof(right));
            if (left.Table != right.Table)
                throw new ArgumentException("Column comparisons must use columns from the same table.", nameof(right));
            if (left.Type != right.Type)
                throw new ArgumentException("Column comparisons must use compatible column types.", nameof(right));
            Op = op;
        }

        public ColumnRef Left { get; }
        public CompareOp Op { get; }
        public ColumnRef Right { get; }
    }

    public sealed record Not : Predicate
    {
        public Not(Predicate inner) => Inner = inner ?? throw new ArgumentNullException(nameof(inner));

        public Predicate Inner { get; }
    }

    public sealed record And : Predicate
    {
        public And(ImmutableArray<Predicate> terms)
        {
            Terms = terms.IsDefault
                ? ImmutableArray<Predicate>.Empty
                : terms.Select(term => term ?? throw new ArgumentException("Conjunction terms cannot be null references.", nameof(terms))).ToImmutableArray();
        }

        public And(IEnumerable<Predicate> terms)
            : this((terms ?? throw new ArgumentNullException(nameof(terms))).ToImmutableArray())
        {
        }

        public ImmutableArray<Predicate> Terms { get; }
    }

    public sealed record Or : Predicate
    {
        public Or(ImmutableArray<Predicate> terms)
        {
            Terms = terms.IsDefault
                ? ImmutableArray<Predicate>.Empty
                : terms.Select(term => term ?? throw new ArgumentException("Disjunction terms cannot be null references.", nameof(terms))).ToImmutableArray();
        }

        public Or(IEnumerable<Predicate> terms)
            : this((terms ?? throw new ArgumentNullException(nameof(terms))).ToImmutableArray())
        {
        }

        public ImmutableArray<Predicate> Terms { get; }
    }

    public sealed record AlwaysTrue : Predicate
    {
        public static AlwaysTrue Instance { get; } = new();
    }

    public sealed record AlwaysFalse : Predicate
    {
        public static AlwaysFalse Instance { get; } = new();
    }

    private static ColumnRef RequireStringColumn(ColumnRef? column)
    {
        if (column is null)
            throw new ArgumentNullException(nameof(column));
        if (column.Type != QueryType.String)
            throw new ArgumentException("String predicates require a string column.", nameof(column));
        return column;
    }
}
