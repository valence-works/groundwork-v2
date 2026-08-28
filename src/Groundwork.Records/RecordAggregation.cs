using System.Linq.Expressions;
using System.Reflection;
using Groundwork.Kernel;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Records;

/// <summary>
/// A typed invocation of one aggregation profile declared by a record table.
/// </summary>
/// <remarks>
/// The selectors only materialize the already-declared group and reducer aliases. They cannot
/// change the profile's grouping, reducers, or budgets, and the binding creates the ordinary
/// <see cref="AggregationQuery"/> only at the execution boundary.
/// </remarks>
public sealed class RecordAggregationBinding<TGroup, TResult>
{
    private readonly object owner;
    private readonly Func<AggregationRow, TGroup> groupSelector;
    private readonly Func<AggregationRow, TResult> resultSelector;

    internal RecordAggregationBinding(
        object owner,
        string profileName,
        Expression<Func<AggregationRow, TGroup>> groupSelector,
        Expression<Func<AggregationRow, TResult>> resultSelector,
        IReadOnlyDictionary<string, Type> groupTypes,
        IReadOnlyDictionary<string, Type> resultTypes)
    {
        this.owner = owner;
        ProfileName = profileName;
        this.groupSelector = RecordAggregationSelector.Compile(
            groupSelector,
            groupTypes,
            "group",
            requireAllAliases: true);
        this.resultSelector = RecordAggregationSelector.Compile(resultSelector, resultTypes, "result");
    }

    /// <summary>The name of the declaration this binding invokes.</summary>
    public string ProfileName { get; }

    internal AggregationQuery Query => AggregationQuery.For(ProfileName);

    internal RecordAggregationResult<TGroup, TResult> Materialize(AggregationRow row) =>
        new(groupSelector(row), resultSelector(row));

    internal void EnsureOwner(object table)
    {
        if (!ReferenceEquals(owner, table))
            throw new InvalidOperationException("A record aggregation binding must be executed by the table that created it.");
    }
}

/// <summary>A typed group key and reducer result returned by a declared profile invocation.</summary>
public sealed record RecordAggregationResult<TGroup, TResult>(TGroup Group, TResult Result)
{
    /// <summary>Alias for <see cref="Result"/> for callers that use value-oriented terminology.</summary>
    public TResult Value => Result;
}

/// <summary>Typed access to one declared group or reducer alias in a profile result.</summary>
public static class AggregationRowExtensions
{
    public static T Get<T>(this AggregationRow row, string alias)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("An aggregation alias is required.", nameof(alias));
        if (!row.Values.TryGetValue(alias, out var value))
            throw new KeyNotFoundException($"The aggregation result did not contain declared alias '{alias}'.");
        if (value is null)
            return default!;
        if (value is T typed)
            return typed;
        throw new InvalidCastException(
            $"Aggregation alias '{alias}' contains '{value.GetType().FullName}', not '{typeof(T).FullName}'.");
    }
}

internal static class RecordAggregationBindingFactory
{
    public static RecordAggregationBinding<TGroup, TResult> Create<TRecord, TGroup, TResult>(
        RecordTable<TRecord> table,
        string profileName,
        Expression<Func<AggregationRow, TGroup>> groupSelector,
        Expression<Func<AggregationRow, TResult>> resultSelector)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("An aggregation profile name is required.", nameof(profileName));
        ArgumentNullException.ThrowIfNull(groupSelector);
        ArgumentNullException.ThrowIfNull(resultSelector);

        var profile = AggregationProfileValidator.ResolveOrThrow(table.Definition, profileName);
        var groupTypes = BuildGroupTypes(table.Definition, profile);
        var resultTypes = BuildResultTypes(table.Definition, profile);
        return new RecordAggregationBinding<TGroup, TResult>(
            table,
            profileName,
            groupSelector,
            resultSelector,
            groupTypes,
            resultTypes);
    }

    private static IReadOnlyDictionary<string, Type> BuildGroupTypes(
        KernelStorageUnit unit,
        AggregationProfile profile)
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var group in AggregationGroups(profile))
        {
            var type = group switch
            {
                AggregationGroup.Column column => SourceType(unit, column.Alias, nullable: true),
                AggregationGroup.TimeBucket => typeof(DateTimeOffset),
                _ => throw new ArgumentOutOfRangeException(nameof(profile))
            };
            Add(result, group.Alias, type, "group");
        }

        return result;
    }

    private static IReadOnlyDictionary<string, Type> BuildResultTypes(
        KernelStorageUnit unit,
        AggregationProfile profile)
    {
        var result = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var aggregate in profile.Aggregates)
        {
            var type = aggregate switch
            {
                Aggregate.Count => typeof(long),
                Aggregate.Min min => SourceType(unit, min.Column, nullable: IsNullableSource(unit, min.Column)),
                Aggregate.Max max => SourceType(unit, max.Column, nullable: IsNullableSource(unit, max.Column)),
                Aggregate.Sum sum => SumType(unit, sum.Column),
                Aggregate.SetUnion => typeof(string[]),
                Aggregate.FirstBy first => SourceType(unit, first.Column, nullable: IsNullableSource(unit, first.Column)),
                _ => throw new ArgumentOutOfRangeException(nameof(profile))
            };
            Add(result, aggregate.Alias, type, "result");
        }

        return result;
    }

    private static IReadOnlyList<AggregationGroup> AggregationGroups(AggregationProfile profile) =>
        profile.GroupByExpressions is { Count: > 0 }
            ? profile.GroupByExpressions
            : (profile.GroupByColumns ?? []).Select(column => (AggregationGroup)new AggregationGroup.Column(column)).ToArray();

    private static Type SumType(KernelStorageUnit unit, string column)
    {
        var declaration = FindColumn(unit, column);
        var type = declaration.Type is PortableType.Int32 or PortableType.Int64
            ? typeof(long)
            : ToClrType(declaration.Type);
        return MakeNullable(type, declaration.IsNullable);
    }

    private static Type SourceType(KernelStorageUnit unit, string column, bool nullable)
    {
        var declaration = FindColumn(unit, column);
        return MakeNullable(ToClrType(declaration.Type), nullable && declaration.IsNullable);
    }

    private static bool IsNullableSource(KernelStorageUnit unit, string column) => FindColumn(unit, column).IsNullable;

    private static ColumnDefinition FindColumn(KernelStorageUnit unit, string column) =>
        unit.Columns.SingleOrDefault(candidate => string.Equals(candidate.Name, column, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"Aggregation profile '{unit.Name}' references undeclared column '{column}'.", nameof(unit));

    private static Type ToClrType(PortableType type) => type switch
    {
        PortableType.String => typeof(string),
        PortableType.Int32 => typeof(int),
        PortableType.Int64 => typeof(long),
        PortableType.Decimal => typeof(decimal),
        PortableType.Boolean => typeof(bool),
        PortableType.DateTimeOffset => typeof(DateTimeOffset),
        PortableType.Guid => typeof(Guid),
        PortableType.Binary => typeof(byte[]),
        PortableType.Json => typeof(object),
        _ => throw new ArgumentException($"Portable type '{type}' cannot be a typed aggregation result.", nameof(type))
    };

    private static Type MakeNullable(Type type, bool nullable) =>
        nullable && type.IsValueType && Nullable.GetUnderlyingType(type) is null
            ? typeof(Nullable<>).MakeGenericType(type)
            : type;

    private static void Add(IDictionary<string, Type> aliases, string alias, Type type, string kind)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException($"A declared aggregation {kind} alias cannot be blank.", nameof(alias));
        if (!aliases.TryAdd(alias, type))
            throw new ArgumentException($"Aggregation {kind} alias '{alias}' is declared more than once.", nameof(alias));
    }
}

internal static class RecordAggregationSelector
{
    private static readonly MethodInfo GetMethod = typeof(AggregationRowExtensions)
        .GetMethod(nameof(AggregationRowExtensions.Get), BindingFlags.Public | BindingFlags.Static)!;

    public static Func<AggregationRow, TResult> Compile<TResult>(
        Expression<Func<AggregationRow, TResult>> selector,
        IReadOnlyDictionary<string, Type> aliases,
        string selectorKind,
        bool requireAllAliases = false)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(aliases);
        var visitor = new Visitor(selector.Parameters[0], aliases, selectorKind);
        visitor.Visit(selector.Body);
        if (requireAllAliases)
            visitor.EnsureAllAliasesRead();
        return selector.Compile();
    }

    private sealed class Visitor(
        ParameterExpression source,
        IReadOnlyDictionary<string, Type> aliases,
        string selectorKind) : ExpressionVisitor
    {
        private readonly HashSet<string> usedAliases = new(StringComparer.Ordinal);

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (!node.Method.IsGenericMethod || node.Method.GetGenericMethodDefinition() != GetMethod)
                throw Invalid(node, $"A typed aggregation {selectorKind} selector may call only AggregationRow.Get<T>.");
            if (node.Arguments.Count != 2 || node.Arguments[1] is not ConstantExpression { Value: string alias })
                throw Invalid(node, "AggregationRow.Get<T> requires a constant declared alias.");
            if (!aliases.TryGetValue(alias, out var expected))
                throw Invalid(node, $"Aggregation alias '{alias}' is not declared by this profile's {selectorKind} output.");

            var requested = node.Method.GetGenericArguments()[0];
            if (!CanRead(requested, expected))
                throw Invalid(node,
                    $"Aggregation alias '{alias}' is declared as '{expected.FullName}', not '{requested.FullName}'.");
            if (node.Arguments[0] != source)
                throw Invalid(node, "AggregationRow.Get<T> must read the selector's row parameter directly.");
            usedAliases.Add(alias);
            return node;
        }

        public void EnsureAllAliasesRead()
        {
            var missing = aliases.Keys.Where(alias => !usedAliases.Contains(alias)).ToArray();
            if (missing.Length != 0)
                throw new ArgumentException(
                    $"A typed aggregation {selectorKind} selector must bind every declared alias; " +
                    $"missing '{string.Join("', '", missing)}'.",
                    nameof(aliases));
        }

        protected override Expression VisitParameter(ParameterExpression node) =>
            node == source
                ? throw Invalid(node, $"A typed aggregation {selectorKind} selector must read declared aliases, not the row object.")
                : base.VisitParameter(node);

        protected override Expression VisitMember(MemberExpression node) =>
            throw Invalid(node, $"A typed aggregation {selectorKind} selector may contain only aliases and result constructors.");

        protected override Expression VisitBinary(BinaryExpression node) =>
            throw Invalid(node, "Computed aggregation expressions are not portable; bind declared aliases directly.");

        protected override Expression VisitConditional(ConditionalExpression node) =>
            throw Invalid(node, "Conditional aggregation expressions are not portable; bind declared aliases directly.");

        protected override Expression VisitInvocation(InvocationExpression node) =>
            throw Invalid(node, "Invoked aggregation expressions are not portable; bind declared aliases directly.");

        private static bool CanRead(Type requested, Type expected)
        {
            if (requested == expected)
                return true;
            if (Nullable.GetUnderlyingType(requested) == expected)
                return true;
            return !requested.IsValueType && !expected.IsValueType && requested.IsAssignableFrom(expected);
        }

        private static ArgumentException Invalid(Expression node, string message) =>
            new(message, nameof(node));
    }
}
