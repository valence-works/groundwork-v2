using System.Collections.Immutable;

namespace Groundwork.Query.Model;

public sealed class QueryNormalizationException : InvalidOperationException
{
    public QueryNormalizationException(string code, string subexpression, string message)
        : base(code + ": " + message + " Offending sub-expression: " + subexpression)
    {
        Code = code;
        Subexpression = subexpression;
    }

    public string Code { get; }
    public string Subexpression { get; }
}

public static class PredicateNormalizer
{
    public const int MaxConjuncts = 64;
    public const int MaxDisjunctsPerConjunct = 16;

    public static Predicate Normalize(Predicate predicate)
    {
        if (predicate is null)
            throw new ArgumentNullException(nameof(predicate));
        var normalizedNnf = ToNnf(predicate, negate: false);
        var clauses = ToCnf(normalizedNnf, predicate);
        var fused = Fuse(clauses);
        return FromCnf(fused);
    }

    private static Predicate ToNnf(Predicate predicate, bool negate)
    {
        switch (predicate)
        {
            case Predicate.Not not:
                return ToNnf(not.Inner, !negate);
            case Predicate.AlwaysTrue:
                return negate ? Predicate.AlwaysFalse.Instance : Predicate.AlwaysTrue.Instance;
            case Predicate.AlwaysFalse:
                return negate ? Predicate.AlwaysTrue.Instance : Predicate.AlwaysFalse.Instance;
            case Predicate.And and:
                if (and.Terms.Length == 0)
                    return negate ? Predicate.AlwaysFalse.Instance : Predicate.AlwaysTrue.Instance;
                return negate
                    ? new Predicate.Or(and.Terms.Select(term => ToNnf(term, true)).ToImmutableArray())
                    : new Predicate.And(and.Terms.Select(term => ToNnf(term, false)).ToImmutableArray());
            case Predicate.Or or:
                if (or.Terms.Length == 0)
                    return negate ? Predicate.AlwaysTrue.Instance : Predicate.AlwaysFalse.Instance;
                return negate
                    ? new Predicate.And(or.Terms.Select(term => ToNnf(term, true)).ToImmutableArray())
                    : new Predicate.Or(or.Terms.Select(term => ToNnf(term, false)).ToImmutableArray());
            case Predicate.In membership when membership.Values.Length == 0:
                return negate ? Predicate.AlwaysTrue.Instance : Predicate.AlwaysFalse.Instance;
            default:
                var leaf = NormalizeLeaf(predicate);
                return negate ? new Predicate.Not(leaf) : leaf;
        }
    }

    private static Predicate NormalizeLeaf(Predicate predicate) => predicate switch
    {
        Predicate.In membership => NormalizeMembership(membership),
        Predicate.Range range => NormalizeRange(range),
        Predicate.ElementOf elementOf => new Predicate.ElementOf(
            elementOf.Set,
            elementOf.Values.Distinct().OrderBy(value => value.ToCanonicalString()).ToImmutableArray(),
            elementOf.Quantifier),
        _ => predicate
    };

    private static Predicate NormalizeMembership(Predicate.In membership)
    {
        var values = membership.Values
            .Distinct()
            .OrderBy(value => value.ToCanonicalString(), StringComparer.Ordinal)
            .ToImmutableArray();
        return values.Length switch
        {
            0 => Predicate.AlwaysFalse.Instance,
            1 => new Predicate.Equal(membership.Column, values[0]),
            _ => new Predicate.In(membership.Column, values)
        };
    }

    private static Predicate NormalizeRange(Predicate.Range range)
    {
        if (range.Lower is null || range.Upper is null)
            return range;
        var comparison = QueryConstant.Compare(range.Lower.Value, range.Upper.Value);
        if (comparison > 0 || (comparison == 0 && (!range.Lower.IsInclusive || !range.Upper.IsInclusive)))
            return Predicate.AlwaysFalse.Instance;
        if (comparison == 0)
            return new Predicate.Equal(range.Column, range.Lower.Value);
        return range;
    }

    private static List<List<Predicate>> ToCnf(Predicate predicate, Predicate source)
    {
        switch (predicate)
        {
            case Predicate.AlwaysTrue:
                return new List<List<Predicate>>();
            case Predicate.AlwaysFalse:
                return new List<List<Predicate>> { new() };
            case Predicate.And and:
            {
                var result = new List<List<Predicate>>();
                foreach (var term in and.Terms)
                {
                    var next = ToCnf(term, source);
                    if (IsFalse(result) || IsFalse(next))
                        return new List<List<Predicate>> { new() };
                    result.AddRange(next);
                    CheckBudget(result, source);
                }
                return result;
            }
            case Predicate.Or or:
            {
                var result = new List<List<Predicate>>();
                var first = true;
                foreach (var term in or.Terms)
                {
                    var next = ToCnf(term, source);
                    if (IsTrue(next))
                        return next;
                    if (first)
                    {
                        result = next;
                        first = false;
                        CheckBudget(result, source);
                        continue;
                    }
                    if (IsTrue(result))
                        return result;
                    if (IsFalse(result))
                    {
                        result = next;
                        continue;
                    }

                    var distributed = new List<List<Predicate>>();
                    foreach (var left in result)
                    foreach (var right in next)
                    {
                        var clause = left.Concat(right).ToList();
                        if (clause.Count > MaxDisjunctsPerConjunct)
                            ThrowBudget(source, result.Count * next.Count, clause.Count);
                        distributed.Add(clause);
                        if (distributed.Count > MaxConjuncts)
                            ThrowBudget(source, distributed.Count, clause.Count);
                    }
                    result = distributed;
                    CheckBudget(result, source);
                }
                return result;
            }
            default:
                return new List<List<Predicate>> { new() { NormalizeLeaf(predicate) } };
        }
    }

    private static List<List<Predicate>> Fuse(List<List<Predicate>> clauses)
    {
        if (IsFalse(clauses))
            return new List<List<Predicate>> { new() };

        var normalizedClauses = new List<List<Predicate>>();
        foreach (var clause in clauses)
        {
            var fusedClause = FuseDisjunction(clause);
            if (fusedClause is null)
                continue;
            if (fusedClause.Count == 0)
                return new List<List<Predicate>> { new() };
            normalizedClauses.Add(fusedClause);
        }

        if (normalizedClauses.Count == 0)
            return normalizedClauses;

        if (normalizedClauses.All(clause => clause.Count == 1))
            return FuseConjunction(normalizedClauses.Select(clause => clause[0]).ToList());

        return normalizedClauses
            .OrderBy(clause => string.Join("|", clause.Select(PredicateCanonicalizer.ToCanonicalString)), StringComparer.Ordinal)
            .ToList();
    }

    private static List<Predicate>? FuseDisjunction(List<Predicate> terms)
    {
        var remaining = terms
            .Where(term => term is not Predicate.AlwaysFalse)
            .ToList();
        if (terms.Any(term => term is Predicate.AlwaysTrue))
            return null;
        if (remaining.Count == 0)
            return new List<Predicate>();

        var output = new List<Predicate>();
        foreach (var group in remaining.GroupBy(ColumnKey))
        {
            var membership = group.Where(term => term is Predicate.Equal or Predicate.In).ToList();
            if (membership.Count == 0)
            {
                output.AddRange(group);
                continue;
            }

            var values = membership.SelectMany(term => term switch
            {
                Predicate.Equal equal => new[] { equal.Value },
                Predicate.In inPredicate => inPredicate.Values.AsEnumerable(),
                _ => Enumerable.Empty<QueryConstant>()
            }).Distinct().OrderBy(value => value.ToCanonicalString(), StringComparer.Ordinal).ToImmutableArray();
            output.Add(values.Length == 1
                ? new Predicate.Equal(ColumnOf(membership[0]), values[0])
                : new Predicate.In(ColumnOf(membership[0]), values));
            output.AddRange(group.Where(term => term is not (Predicate.Equal or Predicate.In)));
        }

        return output
            .OrderBy(PredicateCanonicalizer.ToCanonicalString, StringComparer.Ordinal)
            .ToList();
    }

    private static List<List<Predicate>> FuseConjunction(List<Predicate> terms)
    {
        var output = new List<Predicate>();
        foreach (var group in terms.GroupBy(ColumnKey))
        {
            var constrained = group.Where(term => term is Predicate.Equal or Predicate.In or Predicate.Range).ToList();
            if (constrained.Count == 0)
            {
                output.AddRange(group);
                continue;
            }

            var column = ColumnOf(constrained[0]);
            List<QueryConstant>? allowed = null;
            Bound? lower = null;
            Bound? upper = null;
            foreach (var constraint in constrained)
            {
                var values = constraint switch
                {
                    Predicate.Equal equal => new List<QueryConstant> { equal.Value },
                    Predicate.In membership => membership.Values.ToList(),
                    _ => null
                };
                if (values is not null)
                {
                    allowed = allowed is null
                        ? values
                        : allowed.Where(value => values.Any(candidate => QueryConstant.Compare(value, candidate) == 0)).ToList();
                }
                if (constraint is Predicate.Range range)
                {
                    lower = MaxLower(lower, range.Lower);
                    upper = MinUpper(upper, range.Upper);
                }
            }

            if (lower is not null && upper is not null)
            {
                var comparison = QueryConstant.Compare(lower.Value, upper.Value);
                if (comparison > 0 || (comparison == 0 && (!lower.IsInclusive || !upper.IsInclusive)))
                    return new List<List<Predicate>> { new() };
            }

            if (allowed is not null)
            {
                allowed = allowed.Where(value => IsWithin(value, lower, upper)).Distinct().OrderBy(value => value.ToCanonicalString(), StringComparer.Ordinal).ToList();
                if (allowed.Count == 0)
                    return new List<List<Predicate>> { new() };
                output.Add(allowed.Count == 1
                    ? new Predicate.Equal(column, allowed[0])
                    : new Predicate.In(column, allowed.ToImmutableArray()));
            }
            else if (lower is not null || upper is not null)
            {
                output.Add(new Predicate.Range(column, lower, upper));
            }
            else
            {
                output.AddRange(constrained);
            }
            output.AddRange(group.Where(term => term is not (Predicate.Equal or Predicate.In or Predicate.Range)));
        }

        return output
            .OrderBy(PredicateCanonicalizer.ToCanonicalString, StringComparer.Ordinal)
            .Select(term => new List<Predicate> { term })
            .ToList();
    }

    private static bool IsWithin(QueryConstant value, Bound? lower, Bound? upper) =>
        (lower is null || QueryConstant.Compare(value, lower.Value) > 0 || (lower.IsInclusive && QueryConstant.Compare(value, lower.Value) == 0)) &&
        (upper is null || QueryConstant.Compare(value, upper.Value) < 0 || (upper.IsInclusive && QueryConstant.Compare(value, upper.Value) == 0));

    private static Bound? MaxLower(Bound? left, Bound? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        var comparison = QueryConstant.Compare(left.Value, right.Value);
        if (comparison > 0) return left;
        if (comparison < 0) return right;
        return left.IsInclusive && right.IsInclusive ? left : Bound.Exclusive(left.Value);
    }

    private static Bound? MinUpper(Bound? left, Bound? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        var comparison = QueryConstant.Compare(left.Value, right.Value);
        if (comparison < 0) return left;
        if (comparison > 0) return right;
        return left.IsInclusive && right.IsInclusive ? left : Bound.Exclusive(left.Value);
    }

    private static string ColumnKey(Predicate predicate) => predicate switch
    {
        Predicate.Equal equal => PredicateCanonicalizer.Column(equal.Column),
        Predicate.In membership => PredicateCanonicalizer.Column(membership.Column),
        Predicate.Range range => PredicateCanonicalizer.Column(range.Column),
        _ => "#" + PredicateCanonicalizer.ToCanonicalString(predicate)
    };

    private static ColumnRef ColumnOf(Predicate predicate) => predicate switch
    {
        Predicate.Equal equal => equal.Column,
        Predicate.In membership => membership.Column,
        Predicate.Range range => range.Column,
        _ => throw new InvalidOperationException("Predicate has no column.")
    };

    private static Predicate FromCnf(List<List<Predicate>> clauses)
    {
        if (IsFalse(clauses))
            return Predicate.AlwaysFalse.Instance;
        if (clauses.Count == 0)
            return Predicate.AlwaysTrue.Instance;

        var terms = clauses
            .Where(clause => clause.Count != 0)
            .Select(clause => clause.Count == 1
                ? clause[0]
                : new Predicate.Or(clause.OrderBy(PredicateCanonicalizer.ToCanonicalString, StringComparer.Ordinal).ToImmutableArray()))
            .OrderBy(PredicateCanonicalizer.ToCanonicalString, StringComparer.Ordinal)
            .ToImmutableArray();
        return terms.Length switch
        {
            0 => Predicate.AlwaysTrue.Instance,
            1 => terms[0],
            _ => new Predicate.And(terms)
        };
    }

    private static bool IsTrue(List<List<Predicate>> clauses) => clauses.Count == 0;
    private static bool IsFalse(List<List<Predicate>> clauses) => clauses.Count == 1 && clauses[0].Count == 0;

    private static void CheckBudget(List<List<Predicate>> clauses, Predicate source)
    {
        var maximumDisjuncts = clauses.Count == 0 ? 0 : clauses.Max(clause => clause.Count);
        if (clauses.Count > MaxConjuncts || maximumDisjuncts > MaxDisjunctsPerConjunct)
            ThrowBudget(source, clauses.Count, maximumDisjuncts);
    }

    private static void ThrowBudget(Predicate source, int conjuncts, int disjuncts)
    {
        throw new QueryNormalizationException(
            "GW-QUERY-020",
            PredicateCanonicalizer.ToCanonicalString(source),
            $"CNF budget exceeded ({conjuncts} conjuncts, {disjuncts} disjuncts per conjunct; limits are {MaxConjuncts}/{MaxDisjunctsPerConjunct}).");
    }
}
