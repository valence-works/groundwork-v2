using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Groundwork.Kernel;

/// <summary>
/// A closed, provider-neutral aggregate.  Aggregate expressions are deliberately declarations,
/// rather than an expression tree supplied by a query caller.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(Aggregate.Min), "min")]
[JsonDerivedType(typeof(Aggregate.Max), "max")]
[JsonDerivedType(typeof(Aggregate.Sum), "sum")]
[JsonDerivedType(typeof(Aggregate.SetUnion), "setUnion")]
[JsonDerivedType(typeof(Aggregate.FirstBy), "firstBy")]
public abstract record Aggregate(string Alias)
{
    public sealed record Min(string Alias, string Column) : Aggregate(Alias);

    public sealed record Max(string Alias, string Column) : Aggregate(Alias);

    public sealed record Sum(string Alias, string Column) : Aggregate(Alias);

    public sealed record SetUnion(string Alias, string Column, int MaxValues) : Aggregate(Alias);

    public sealed record FirstBy(
        string Alias,
        string Column,
        string OrderColumn,
        SortDirection Direction = SortDirection.Ascending) : Aggregate(Alias);
}

/// <summary>The only predicates that may be applied after a declared reduction.</summary>
public enum AggregationPredicateOperator
{
    Equal,
    In,
    RangeInclusive,
    Contains
}

/// <summary>Declares the post-reduction operators admitted for one output alias.</summary>
public sealed record AggregationPredicateAllowance
{
    public required string Alias { get; init; }
    public required IReadOnlySet<AggregationPredicateOperator> SupportedPredicates { get; init; }
}

/// <summary>
/// A bounded, named aggregation shape.  A caller can select this shape by name but cannot submit
/// an arbitrary aggregate expression.
/// </summary>
public sealed record AggregationProfile
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> GroupByColumns { get; init; }
    public required IReadOnlyList<Aggregate> Aggregates { get; init; }
    public IReadOnlyList<AggregationPredicateAllowance> AllowedPredicates { get; init; } = [];
    public int MaxGroups { get; init; } = 1_000;
    public int MaxInputRows { get; init; } = 100_000;
}

/// <summary>Creates an immutable declaration snapshot for provider state and schema history.</summary>
public static class AggregationProfileSnapshot
{
    public static AggregationProfile Capture(AggregationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new AggregationProfile
        {
            Name = profile.Name,
            GroupByColumns = (profile.GroupByColumns ?? []).ToArray(),
            Aggregates = (profile.Aggregates ?? []).Select(Capture).ToArray(),
            AllowedPredicates = (profile.AllowedPredicates ?? []).Select(allowance => new AggregationPredicateAllowance
            {
                Alias = allowance.Alias,
                SupportedPredicates = allowance.SupportedPredicates.ToHashSet()
            }).ToArray(),
            MaxGroups = profile.MaxGroups,
            MaxInputRows = profile.MaxInputRows
        };
    }

    public static Aggregate Capture(Aggregate aggregate) => aggregate switch
    {
        Aggregate.Min min => new Aggregate.Min(min.Alias, min.Column),
        Aggregate.Max max => new Aggregate.Max(max.Alias, max.Column),
        Aggregate.Sum sum => new Aggregate.Sum(sum.Alias, sum.Column),
        Aggregate.SetUnion set => new Aggregate.SetUnion(set.Alias, set.Column, set.MaxValues),
        Aggregate.FirstBy first => new Aggregate.FirstBy(first.Alias, first.Column, first.OrderColumn, first.Direction),
        _ => throw new ArgumentOutOfRangeException(nameof(aggregate))
    };
}

/// <summary>One post-reduction predicate tree.  Values are captured by the executor.</summary>
public abstract record AggregationPredicate
{
    private AggregationPredicate() { }

    public sealed record All(IReadOnlyList<AggregationPredicate> Predicates) : AggregationPredicate;

    public sealed record Any(IReadOnlyList<AggregationPredicate> Predicates) : AggregationPredicate;

    public sealed record Comparison(
        string Alias,
        AggregationPredicateOperator Operator,
        IReadOnlyList<object?> Values) : AggregationPredicate;
}

/// <summary>One closed aggregation query against a provider session's declared unit.</summary>
public sealed record AggregationQuery
{
    public AggregationQuery(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("An aggregation profile name is required.", nameof(profileName));
        ProfileName = profileName;
    }

    public string ProfileName { get; }

    /// <summary>Alias retained for callers that refer to the selected declaration as Profile.</summary>
    public string Profile => ProfileName;

    public AggregationPredicate? PostPredicate { get; init; }

    /// <summary>Optional output order.  It must be a group-by column or aggregate alias.</summary>
    public string? OrderBy { get; init; }

    public SortDirection OrderDirection { get; init; } = SortDirection.Ascending;

    /// <summary>Optional caller-requested page size; it never changes profile budgets.</summary>
    public int? Take { get; init; }

    public static AggregationQuery For(string profileName) => new(profileName);
}

/// <summary>A materialized row from a declared aggregation profile.</summary>
public sealed class AggregationRow
{
    public AggregationRow(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = new ReadOnlyDictionary<string, object?>(values.ToDictionary(
            pair => pair.Key,
            pair => CloneValue(pair.Value),
            StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, object?> Values { get; }

    public object? this[string name] => Values.TryGetValue(name, out var value) ? value : null;

    private static object? CloneValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => bytes.ToArray(),
        IReadOnlyDictionary<string, object?> dictionary => new ReadOnlyDictionary<string, object?>(
            dictionary.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value), StringComparer.Ordinal)),
        IEnumerable<string> strings => strings.ToArray(),
        System.Collections.IEnumerable sequence when value is not string =>
            Array.AsReadOnly(sequence.Cast<object?>().Select(CloneValue).ToArray()),
        _ when value.GetType().IsValueType || value is string => value,
        _ => throw new ArgumentException($"Cannot snapshot aggregation value of type '{value.GetType().FullName}'.")
    };
}

/// <summary>Provider-neutral output of a declared aggregation query.</summary>
public sealed class AggregationResult
{
    public AggregationResult(IReadOnlyList<AggregationRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Rows = Array.AsReadOnly(rows.Select(row => row ?? throw new ArgumentException(
            "Aggregation rows cannot contain null references.", nameof(rows))).ToArray());
    }

    public IReadOnlyList<AggregationRow> Rows { get; }
}

public sealed record AggregationValidationError(string Code, string Message, string Path);

public sealed class AggregationValidationException : ArgumentException
{
    public AggregationValidationException(IReadOnlyList<AggregationValidationError> errors)
        : base(errors is { Count: > 0 } ? errors[0].Message : "Aggregation declaration is invalid.")
    {
        Errors = new ReadOnlyCollection<AggregationValidationError>(
            (errors ?? throw new ArgumentNullException(nameof(errors))).ToArray());
    }

    public IReadOnlyList<AggregationValidationError> Errors { get; }
}

public sealed class AggregationBudgetExceededException : InvalidOperationException
{
    public AggregationBudgetExceededException(string code, string message)
        : base(message) => Code = code;

    public string Code { get; }
}

/// <summary>Validates the closed aggregation declaration and post-reduction surface.</summary>
public static class AggregationProfileValidator
{
    public static void Validate(StorageUnit unit, AggregationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(profile);
        var errors = new List<AggregationValidationError>();
        var columns = (unit.Columns ?? []).ToDictionary(column => column.Name, StringComparer.Ordinal);
        var paths = "aggregationProfiles." + profile.Name;

        if (string.IsNullOrWhiteSpace(profile.Name))
            Add("GW-AGG-DECL-001", "An aggregation profile name is required.", paths + ".name");
        if (profile.MaxGroups <= 0)
            Add("GW-AGG-BOUND-001", "MaxGroups must be positive; an unbounded aggregation is refused.", paths + ".maxGroups");
        if (profile.MaxInputRows <= 0)
            Add("GW-AGG-BOUND-002", "MaxInputRows must be positive; an unbounded aggregation is refused.", paths + ".maxInputRows");
        if (profile.GroupByColumns is null || profile.GroupByColumns.Count == 0)
            Add("GW-AGG-GROUP-001", "At least one group-by column is required.", paths + ".groupByColumns");
        else
        {
            foreach (var name in profile.GroupByColumns)
            {
                if (string.IsNullOrWhiteSpace(name) || !columns.ContainsKey(name))
                    Add("GW-AGG-COLUMN-001", $"Group-by column '{name}' is not declared.", paths + ".groupByColumns");
                else if (name.StartsWith("__groundwork_aggregation_", StringComparison.Ordinal))
                    Add("GW-AGG-DECL-009", $"Group-by column '{name}' uses a reserved aggregation alias.", paths + ".groupByColumns");
            }
            AddDuplicateErrors(profile.GroupByColumns, "group-by column", paths + ".groupByColumns");
        }

        if (profile.Aggregates is null || profile.Aggregates.Count == 0)
            Add("GW-AGG-DECL-002", "At least one aggregate is required.", paths + ".aggregates");
        else
        {
            AddDuplicateErrors(profile.Aggregates.Select(aggregate => aggregate?.Alias ?? string.Empty), "aggregate alias", paths + ".aggregates");
            foreach (var aggregate in profile.Aggregates)
            {
                if (aggregate is null)
                {
                    Add("GW-AGG-DECL-003", "Aggregate declarations cannot be null.", paths + ".aggregates");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(aggregate.Alias))
                    Add("GW-AGG-DECL-004", "Aggregate aliases are required.", paths + ".aggregates");
                if (aggregate.Alias.StartsWith("__groundwork_aggregation_", StringComparison.Ordinal))
                    Add("GW-AGG-DECL-010", $"Aggregate alias '{aggregate.Alias}' uses a reserved name.", paths + ".aggregates");
                if (profile.GroupByColumns?.Contains(aggregate.Alias, StringComparer.Ordinal) == true)
                    Add("GW-AGG-DECL-005", $"Aggregate alias '{aggregate.Alias}' collides with a group-by column.", paths + ".aggregates");
                switch (aggregate)
                {
                    case Aggregate.Min min:
                        ValidateOrderableColumn(min.Column, columns, paths + ".aggregates." + min.Alias, errors);
                        break;
                    case Aggregate.Max max:
                        ValidateOrderableColumn(max.Column, columns, paths + ".aggregates." + max.Alias, errors);
                        break;
                    case Aggregate.Sum sum:
                        if (!columns.TryGetValue(sum.Column, out var sumColumn) ||
                            sumColumn.Type is not (PortableType.Int32 or PortableType.Int64 or PortableType.Decimal))
                            Add("GW-AGG-TYPE-001", $"Sum '{sum.Alias}' requires an Int32, Int64, or Decimal column.", paths + ".aggregates." + sum.Alias);
                        break;
                    case Aggregate.SetUnion set:
                        if (!columns.TryGetValue(set.Column, out var setColumn) || setColumn.Type != PortableType.String)
                            Add("GW-AGG-TYPE-002", $"SetUnion '{set.Alias}' requires a String column.", paths + ".aggregates." + set.Alias);
                        if (set.MaxValues <= 0)
                            Add("GW-AGG-BOUND-003", $"SetUnion '{set.Alias}' requires a positive MaxValues bound.", paths + ".aggregates." + set.Alias);
                        break;
                    case Aggregate.FirstBy first:
                        if (!columns.TryGetValue(first.Column, out _))
                            Add("GW-AGG-COLUMN-002", $"FirstBy value column '{first.Column}' is not declared.", paths + ".aggregates." + first.Alias);
                        if (!columns.TryGetValue(first.OrderColumn, out var orderColumn) ||
                            !IsOrderable(orderColumn.Type) || orderColumn.IsNullable)
                            Add("GW-AGG-FIRST-001", $"FirstBy '{first.Alias}' requires a declared, non-null orderable OrderColumn.", paths + ".aggregates." + first.Alias);
                        break;
                    default:
                        Add("GW-AGG-DECL-006", "The aggregate kind is not supported by the closed surface.", paths + ".aggregates");
                        break;
                }
            }
        }

        var aliases = (profile.Aggregates ?? []).Where(aggregate => aggregate is not null)
            .Select(aggregate => aggregate!.Alias).ToHashSet(StringComparer.Ordinal);
        if (profile.AllowedPredicates is null)
            Add("GW-AGG-PRED-001", "Post-reduction predicate allowances must be declared, even when empty.", paths + ".allowedPredicates");
        else
        {
            AddDuplicateErrors(profile.AllowedPredicates.Select(allowance => allowance?.Alias ?? string.Empty), "predicate allowance", paths + ".allowedPredicates");
            foreach (var allowance in profile.AllowedPredicates)
            {
                if (allowance is null || string.IsNullOrWhiteSpace(allowance.Alias) || !aliases.Contains(allowance.Alias))
                {
                    Add("GW-AGG-PRED-002", $"Predicate allowance '{allowance?.Alias}' must name an aggregate output.", paths + ".allowedPredicates");
                    continue;
                }
                if (allowance.SupportedPredicates is null || allowance.SupportedPredicates.Count == 0)
                {
                    Add("GW-AGG-PRED-003", $"Predicate allowance '{allowance.Alias}' must name at least one operator.", paths + ".allowedPredicates");
                    continue;
                }
                var aggregate = (profile.Aggregates ?? Array.Empty<Aggregate>()).First(item => item!.Alias == allowance.Alias);
                if (aggregate is Aggregate.SetUnion && allowance.SupportedPredicates.Contains(AggregationPredicateOperator.RangeInclusive))
                    Add("GW-AGG-PRED-004", $"SetUnion output '{allowance.Alias}' cannot declare a scalar range predicate.", paths + ".allowedPredicates");
                if (aggregate is not Aggregate.SetUnion && allowance.SupportedPredicates.Contains(AggregationPredicateOperator.Contains))
                    Add("GW-AGG-PRED-005", $"Contains is only valid for SetUnion output '{allowance.Alias}'.", paths + ".allowedPredicates");
            }
        }

        if (errors.Count != 0)
            throw new AggregationValidationException(errors);

        void Add(string code, string message, string path) => errors.Add(new(code, message, path));
        void AddDuplicateErrors(IEnumerable<string> values, string kind, string path)
        {
            foreach (var duplicate in values.GroupBy(value => value, StringComparer.Ordinal).Where(group => group.Count() > 1))
                Add("GW-AGG-DECL-007", $"The {kind} '{duplicate.Key}' is declared more than once.", path);
        }
    }

    public static void Validate(AggregationProfile profile, StorageUnit unit) => Validate(unit, profile);

    public static void ValidateUnit(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var profiles = unit.AggregationProfiles ?? [];
        foreach (var duplicate in profiles.GroupBy(profile => profile?.Name ?? string.Empty, StringComparer.Ordinal).Where(group => group.Count() > 1))
            throw new AggregationValidationException([new("GW-AGG-DECL-008", $"Aggregation profile '{duplicate.Key}' is declared more than once.", "aggregationProfiles")]);
        foreach (var profile in profiles)
            Validate(unit, profile);
    }

    private static void ValidateOrderableColumn(
        string name,
        IReadOnlyDictionary<string, ColumnDefinition> columns,
        string path,
        ICollection<AggregationValidationError> errors)
    {
        if (!columns.TryGetValue(name, out var column) || !IsOrderable(column.Type))
            errors.Add(new("GW-AGG-TYPE-003", $"Column '{name}' is not declared as an orderable portable type.", path));
    }

    private static bool IsOrderable(PortableType type) => type is
        PortableType.String or PortableType.Int32 or PortableType.Int64 or PortableType.Decimal or
        PortableType.DateTimeOffset or PortableType.Guid;
}

/// <summary>Provider-neutral reduction used by the reference provider and conformance fixtures.</summary>
public static class AggregationExecutor
{
    public static AggregationResult Execute(
        StorageUnit unit,
        AggregationProfile profile,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        AggregationQuery? query = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rows);
        AggregationProfileValidator.Validate(unit, profile);
        query ??= AggregationQuery.For(profile.Name);
        if (!string.Equals(query.ProfileName, profile.Name, StringComparison.Ordinal))
            throw new AggregationValidationException([new("GW-AGG-QUERY-001", $"Profile '{query.ProfileName}' is not the selected declaration.", "profileName")]);

        var input = new List<IReadOnlyDictionary<string, object?>>(Math.Min(profile.MaxInputRows, 4096));
        foreach (var row in rows)
        {
            if (row is null)
                throw new ArgumentException("Aggregation input rows cannot contain null references.", nameof(rows));
            input.Add(row);
            if (input.Count > profile.MaxInputRows)
                throw new AggregationBudgetExceededException("GW-AGG-BOUND-004", $"Aggregation profile '{profile.Name}' refused more than MaxInputRows={profile.MaxInputRows}; input was not truncated.");
        }

        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        foreach (var row in input)
        {
            var key = string.Join("\u001f", profile.GroupByColumns.Select(column => Canonical(row.TryGetValue(column, out var value) ? value : null)));
            if (!groups.TryGetValue(key, out var group))
            {
                if (groups.Count == profile.MaxGroups)
                    throw new AggregationBudgetExceededException("GW-AGG-BOUND-005", $"Aggregation profile '{profile.Name}' refused more than MaxGroups={profile.MaxGroups}; groups were not truncated.");
                group = new Group();
                groups.Add(key, group);
            }
            group.Rows.Add(row);
        }

        var output = groups.Values.Select(group => Reduce(unit, profile, group.Rows)).ToList();
        ValidateQuery(profile, query, output);
        if (query.OrderBy is not null)
        {
            if (!IsDeclaredOutput(profile, query.OrderBy))
                throw new AggregationValidationException([new("GW-AGG-QUERY-002", $"Order alias '{query.OrderBy}' is not declared by profile '{profile.Name}'.", "orderBy")]);
            output.Sort((left, right) => Compare(left.Values.TryGetValue(query.OrderBy, out var l) ? l : null,
                right.Values.TryGetValue(query.OrderBy, out var r) ? r : null, query.OrderDirection));
        }
        else
        {
            output.Sort((left, right) => string.CompareOrdinal(
                string.Join("\u001f", profile.GroupByColumns.Select(column => Canonical(left[column]))),
                string.Join("\u001f", profile.GroupByColumns.Select(column => Canonical(right[column])))));
        }

        if (query.Take is <= 0)
            throw new AggregationValidationException([new("GW-AGG-QUERY-003", "Aggregation Take must be positive when specified.", "take")]);
        if (query.Take is int take && take > profile.MaxGroups)
            throw new AggregationBudgetExceededException("GW-AGG-BOUND-006", $"Aggregation Take={take} exceeds MaxGroups={profile.MaxGroups}.");
        if (query.Take is int pageSize)
            output = output.Take(pageSize).ToList();
        return new AggregationResult(output);
    }

    private static AggregationRow Reduce(StorageUnit unit, AggregationProfile profile, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in profile.GroupByColumns)
            values[column] = rows[0].TryGetValue(column, out var key) ? key : null;

        foreach (var aggregate in profile.Aggregates)
        {
            object? result = aggregate switch
            {
                Aggregate.Min min => ReduceMin(rows, min.Column),
                Aggregate.Max max => ReduceMax(rows, max.Column),
                Aggregate.Sum sum => ReduceSum(rows, unit.Columns.Single(column => column.Name == sum.Column)),
                Aggregate.SetUnion set => ReduceSetUnion(rows, set.Column, set.MaxValues, set.Alias),
                Aggregate.FirstBy first => ReduceFirstBy(rows, first),
                _ => throw new InvalidOperationException("Unknown aggregate declaration.")
            };
            values[aggregate.Alias] = result;
        }
        return new AggregationRow(values);
    }

    private static object? ReduceMin(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string column) =>
        rows.Select(row => row.TryGetValue(column, out var value) ? value : null).Where(value => value is not null)
            .Aggregate<object?, object?>(null, (best, value) => best is null || Compare(value, best, SortDirection.Ascending) < 0 ? value : best);

    private static object? ReduceMax(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string column) =>
        rows.Select(row => row.TryGetValue(column, out var value) ? value : null).Where(value => value is not null)
            .Aggregate<object?, object?>(null, (best, value) => best is null || Compare(value, best, SortDirection.Ascending) > 0 ? value : best);

    private static object? ReduceSum(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, ColumnDefinition column)
    {
        var values = rows.Select(row => row.TryGetValue(column.Name, out var value) ? value : null).Where(value => value is not null).ToArray();
        if (values.Length == 0) return null;
        try
        {
            if (column.Type is PortableType.Int32 or PortableType.Int64)
            {
                long sum = 0;
                foreach (var value in values)
                    sum = checked(sum + Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return sum;
            }
            if (column.Type == PortableType.Decimal)
            {
                decimal sum = 0;
                foreach (var value in values)
                    sum = checked(sum + Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                return sum;
            }
            throw new InvalidOperationException("Sum was not validated for this column type.");
        }
        catch (OverflowException exception)
        {
            throw new AggregationBudgetExceededException("GW-AGG-SUM-001", $"Sum '{column.Name}' overflowed the declared portable result type.") { Source = exception.Source };
        }
    }

    private static object ReduceSetUnion(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, string column, int maxValues, string alias)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in rows.Select(row => row.TryGetValue(column, out var item) ? item : null).OfType<string>())
        {
            values.Add(value);
            if (values.Count > maxValues)
                throw new AggregationBudgetExceededException("GW-AGG-BOUND-007", $"SetUnion '{alias}' refused more than MaxValues={maxValues}; values were not truncated.");
        }
        return values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static object? ReduceFirstBy(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, Aggregate.FirstBy first)
    {
        IReadOnlyDictionary<string, object?>? selected = null;
        foreach (var row in rows)
        {
            if (selected is null || Compare(
                row.TryGetValue(first.OrderColumn, out var currentOrder) ? currentOrder : null,
                selected.TryGetValue(first.OrderColumn, out var selectedOrder) ? selectedOrder : null,
                first.Direction) < 0)
                selected = row;
        }
        return selected is not null && selected.TryGetValue(first.Column, out var value) ? value : null;
    }

    private static void ValidateQuery(AggregationProfile profile, AggregationQuery query, List<AggregationRow> output)
    {
        if (query.PostPredicate is null) return;
        var allowances = profile.AllowedPredicates.ToDictionary(item => item.Alias, StringComparer.Ordinal);
        ValidatePredicateShape(query.PostPredicate, allowances);
        output.RemoveAll(row => !Evaluate(query.PostPredicate, row.Values, allowances));
    }

    private static void ValidatePredicateShape(
        AggregationPredicate predicate,
        IReadOnlyDictionary<string, AggregationPredicateAllowance> allowances)
    {
        switch (predicate)
        {
            case AggregationPredicate.All all when all.Predicates is { Count: > 0 }:
                foreach (var child in all.Predicates)
                {
                    if (child is null)
                        throw new AggregationValidationException([new("GW-AGG-PRED-010", "Aggregation predicate children cannot be null.", "postPredicate")]);
                    ValidatePredicateShape(child, allowances);
                }
                return;
            case AggregationPredicate.Any any when any.Predicates is { Count: > 0 }:
                foreach (var child in any.Predicates)
                {
                    if (child is null)
                        throw new AggregationValidationException([new("GW-AGG-PRED-010", "Aggregation predicate children cannot be null.", "postPredicate")]);
                    ValidatePredicateShape(child, allowances);
                }
                return;
            case AggregationPredicate.Comparison comparison:
                if (!allowances.TryGetValue(comparison.Alias, out var allowance) ||
                    !allowance.SupportedPredicates.Contains(comparison.Operator))
                    throw new AggregationValidationException([new("GW-AGG-PRED-007", $"Predicate '{comparison.Operator}' is not declared for output '{comparison.Alias}'.", "postPredicate")]);
                var values = comparison.Values ?? throw new AggregationValidationException([new("GW-AGG-PRED-008", "Predicate values are required.", "postPredicate.values")]);
                var valid = comparison.Operator switch
                {
                    AggregationPredicateOperator.Equal => values.Count == 1,
                    AggregationPredicateOperator.In => values.Count > 0,
                    AggregationPredicateOperator.RangeInclusive => values.Count == 2,
                    AggregationPredicateOperator.Contains => values.Count == 1,
                    _ => false
                };
                if (!valid)
                    throw new AggregationValidationException([new("GW-AGG-PRED-009", $"Predicate '{comparison.Operator}' has invalid value arity.", "postPredicate.values")]);
                return;
            default:
                throw new AggregationValidationException([new("GW-AGG-PRED-006", "Aggregation logical predicates must contain at least one child.", "postPredicate")]);
        }
    }

    private static bool Evaluate(AggregationPredicate predicate, IReadOnlyDictionary<string, object?> row,
        IReadOnlyDictionary<string, AggregationPredicateAllowance> allowances)
    {
        return predicate switch
        {
            AggregationPredicate.All all when all.Predicates is { Count: > 0 } => all.Predicates.All(child => Evaluate(child, row, allowances)),
            AggregationPredicate.Any any when any.Predicates is { Count: > 0 } => any.Predicates.Any(child => Evaluate(child, row, allowances)),
            AggregationPredicate.Comparison comparison => EvaluateComparison(comparison, row, allowances),
            _ => throw new AggregationValidationException([new("GW-AGG-PRED-006", "Aggregation logical predicates must contain at least one child.", "postPredicate")])
        };
    }

    private static bool EvaluateComparison(AggregationPredicate.Comparison comparison, IReadOnlyDictionary<string, object?> row,
        IReadOnlyDictionary<string, AggregationPredicateAllowance> allowances)
    {
        if (!allowances.TryGetValue(comparison.Alias, out var allowance) || !allowance.SupportedPredicates.Contains(comparison.Operator))
            throw new AggregationValidationException([new("GW-AGG-PRED-007", $"Predicate '{comparison.Operator}' is not declared for output '{comparison.Alias}'.", "postPredicate")]);
        var actual = row.TryGetValue(comparison.Alias, out var value) ? value : null;
        var values = comparison.Values ?? throw new AggregationValidationException([new("GW-AGG-PRED-008", "Predicate values are required.", "postPredicate.values")]);
        return comparison.Operator switch
        {
            AggregationPredicateOperator.Equal when values.Count == 1 => EqualsPortable(actual, values[0]),
            AggregationPredicateOperator.In when values.Count > 0 => values.Any(candidate => EqualsPortable(actual, candidate)),
            AggregationPredicateOperator.RangeInclusive when values.Count == 2 && actual is not null => Compare(actual, values[0], SortDirection.Ascending) >= 0 && Compare(actual, values[1], SortDirection.Ascending) <= 0,
            AggregationPredicateOperator.Contains when values.Count == 1 && actual is IEnumerable<string> set && values[0] is string text => set.Contains(text, StringComparer.Ordinal),
            _ => throw new AggregationValidationException([new("GW-AGG-PRED-009", $"Predicate '{comparison.Operator}' has invalid value arity.", "postPredicate.values")])
        };
    }

    private static bool IsDeclaredOutput(AggregationProfile profile, string alias) =>
        profile.GroupByColumns.Contains(alias, StringComparer.Ordinal) || profile.Aggregates.Any(aggregate => aggregate.Alias == alias);

    private static bool EqualsPortable(object? left, object? right) =>
        left is byte[] leftBytes && right is byte[] rightBytes ? leftBytes.SequenceEqual(rightBytes) : Equals(left, right);

    private static int Compare(object? left, object? right, SortDirection direction)
    {
        var result = left is null || right is null
            ? left is null && right is null ? 0 : left is null ? -1 : 1
            : left is string leftText && right is string rightText ? string.CompareOrdinal(leftText, rightText)
            : left is Guid leftGuid && right is Guid rightGuid ? CompareBytes(GuidBytes(leftGuid), GuidBytes(rightGuid))
            : left is DateTimeOffset leftInstant && right is DateTimeOffset rightInstant ? leftInstant.UtcTicks.CompareTo(rightInstant.UtcTicks)
            : left is IConvertible && right is IConvertible && IsNumeric(left) && IsNumeric(right)
                ? Convert.ToDecimal(left, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(right, CultureInfo.InvariantCulture))
            : left is IComparable comparable ? comparable.CompareTo(right)
            : string.CompareOrdinal(Canonical(left), Canonical(right));
        return direction == SortDirection.Descending ? -result : result;
    }

    private static string Canonical(object? value) => value switch
    {
        null => "null",
        string text => "s:" + text,
        DateTimeOffset instant => "t:" + instant.UtcTicks.ToString(CultureInfo.InvariantCulture),
        Guid guid => "g:" + Convert.ToHexString(GuidBytes(guid)),
        byte[] bytes => "b:" + Convert.ToHexString(bytes),
        IFormattable formattable => value.GetType().Name + ":" + formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    private static byte[] GuidBytes(Guid value) => Convert.FromHexString(value.ToString("N"));

    private static bool IsNumeric(object value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static int CompareBytes(byte[] left, byte[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
            if (left[index] != right[index]) return left[index].CompareTo(right[index]);
        return left.Length.CompareTo(right.Length);
    }

    private sealed class Group
    {
        public List<IReadOnlyDictionary<string, object?>> Rows { get; } = [];
    }
}
