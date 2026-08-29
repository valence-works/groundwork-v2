using System.Linq.Expressions;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Groundwork.Query.Model;

namespace Groundwork.Query.Linq;

/// <summary>Marks a static expression-producing member as syntactically inlineable by the lowerer.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class GwQueryFragmentAttribute : Attribute
{
}

/// <summary>Declares the text comparison policy used by a mapped member for analyzer fix-it safety.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class GwStringComparisonAttribute : Attribute
{
    public GwStringComparisonAttribute(StringComparison comparison)
    {
        if (comparison is not StringComparison.Ordinal and not StringComparison.OrdinalIgnoreCase)
            throw new ArgumentOutOfRangeException(nameof(comparison), "Only Ordinal and OrdinalIgnoreCase are portable.");
        Comparison = comparison;
    }
    public StringComparison Comparison { get; }
}

/// <summary>A diagnostic produced by the closed LINQ allow-list.</summary>
public sealed record LinqDiagnostic(string Code, string Message, Expression Span)
{
    public string Path { get; init; } = "predicate";
}

/// <summary>Thrown when a LINQ expression is outside the portable closed surface.</summary>
public sealed class LinqTranslationException : InvalidOperationException
{
    public LinqTranslationException(IReadOnlyList<LinqDiagnostic> diagnostics)
        : base(diagnostics is null || diagnostics.Count == 0
            ? "The expression is not a supported Groundwork query."
            : diagnostics[0].Code + ": " + diagnostics[0].Message)
    {
        Diagnostics = new ReadOnlyCollection<LinqDiagnostic>((diagnostics ?? throw new ArgumentNullException(nameof(diagnostics))).ToArray());
    }

    public IReadOnlyList<LinqDiagnostic> Diagnostics { get; }
}

/// <summary>Metadata used by the expression lowerer to map a CLR type to a query table.</summary>
public sealed class GwTableModel<T>
{
    private readonly IReadOnlyDictionary<string, ColumnRef> columns;
    private readonly IReadOnlyDictionary<string, ElementSetRef> elementSets;

    public GwTableModel(string name, IEnumerable<GwColumn<T>> columns, IEnumerable<GwElementSet<T>>? elementSets = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A table name is required.", nameof(name));
        Name = name;
        var supplied = (columns ?? throw new ArgumentNullException(nameof(columns))).ToArray();
        if (supplied.Length == 0) throw new ArgumentException("At least one column is required.", nameof(columns));
        var mappedColumns = supplied.ToDictionary(x => x.MemberName, x => new ColumnRef(new TableId(name), x.ColumnName, x.Type,
            x.IsNullable, x.MaxLength, x.DecimalPrecision, x.DecimalScale, x.StringComparison), StringComparer.Ordinal);
        if (mappedColumns.Count != supplied.Length) throw new ArgumentException("Column members must be unique.", nameof(columns));
        this.columns = new ReadOnlyDictionary<string, ColumnRef>(mappedColumns);
        var sets = (elementSets ?? Array.Empty<GwElementSet<T>>()).ToArray();
        var mappedElementSets = sets.ToDictionary(x => x.MemberName, x => new ElementSetRef(x.SetName, x.ElementType), StringComparer.Ordinal);
        if (mappedElementSets.Count != sets.Length) throw new ArgumentException("Element-set members must be unique.", nameof(elementSets));
        this.elementSets = new ReadOnlyDictionary<string, ElementSetRef>(mappedElementSets);
    }

    public string Name { get; }
    public TableId Table => new(Name);
    public IReadOnlyDictionary<string, ColumnRef> Columns => columns;
    public IReadOnlyDictionary<string, ElementSetRef> ElementSets => elementSets;

    /// <summary>Binds a typed navigation member to one provider-neutral declared-reference join.</summary>
    public GwReference<T, TTarget> Reference<TTarget>(
        Expression<Func<T, TTarget>> navigation,
        GwTableModel<TTarget> target,
        ReferenceJoin declaration) => new(this, target, navigation, declaration);

    public static GwTableModel<T> Infer(string name)
    {
        var result = new List<GwColumn<T>>();
        foreach (var member in typeof(T).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(x => x.MemberType is System.Reflection.MemberTypes.Property or System.Reflection.MemberTypes.Field)
            .OrderBy(x => x.MetadataToken))
        {
            var type = member is System.Reflection.PropertyInfo p ? p.PropertyType : ((System.Reflection.FieldInfo)member).FieldType;
            var nullable = !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
            var core = Nullable.GetUnderlyingType(type) ?? type;
            var queryType = core == typeof(string) ? QueryType.String : core == typeof(bool) ? QueryType.Boolean :
                core == typeof(int) ? QueryType.Int32 : core == typeof(long) ? QueryType.Int64 :
                core == typeof(decimal) ? QueryType.Decimal : core == typeof(DateTimeOffset) ? QueryType.DateTimeOffset :
                core == typeof(Guid) ? QueryType.Guid : core == typeof(byte[]) ? QueryType.Binary :
                throw new NotSupportedException($"Member '{member.Name}' has no portable query type.");
            var comparison = member.GetCustomAttributes(typeof(GwStringComparisonAttribute), inherit: true).OfType<GwStringComparisonAttribute>().FirstOrDefault()?.Comparison switch
            {
                StringComparison.OrdinalIgnoreCase => QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase,
                _ => QueryStringComparisonPolicy.Ordinal
            };
            result.Add(new GwColumn<T>(member.Name, member.Name, queryType, nullable,
                DecimalPrecision: queryType == QueryType.Decimal ? (byte)18 : null,
                DecimalScale: queryType == QueryType.Decimal ? (byte)4 : null,
                StringComparison: comparison));
        }
        return new GwTableModel<T>(name, result);
    }

    internal ColumnRef Column(string member) => columns.TryGetValue(member, out var value)
        ? value : throw new ArgumentException($"'{member}' is not a mapped column on '{typeof(T).Name}'.", nameof(member));
    internal ElementSetRef ElementSet(string member) => elementSets.TryGetValue(member, out var value)
        ? value : throw new ArgumentException($"'{member}' is not a declared element set on '{typeof(T).Name}'.", nameof(member));
}

internal interface IGwNavigation
{
    string NavigationMember { get; }
    IReadOnlyDictionary<string, ColumnRef> TargetColumns { get; }
}

/// <summary>
/// A typed navigation bound to one provider-neutral declared reference. It can only be activated
/// by <c>IGwQueryable.Join</c>.
/// </summary>
public sealed class GwReference<TSource, TTarget> : IGwNavigation
{
    internal GwReference(
        GwTableModel<TSource> source,
        GwTableModel<TTarget> target,
        Expression<Func<TSource, TTarget>> navigation,
        ReferenceJoin declaration)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (navigation is null) throw new ArgumentNullException(nameof(navigation));
        Declaration = declaration ?? throw new ArgumentNullException(nameof(declaration));

        var body = navigation.Body;
        while (body is UnaryExpression unary && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
            body = unary.Operand;
        if (body is not MemberExpression member || member.Expression != navigation.Parameters[0])
            throw new ArgumentException("A navigation must be one direct property or field on the source row.", nameof(navigation));
        var memberType = member.Member switch
        {
            System.Reflection.PropertyInfo property => property.PropertyType,
            System.Reflection.FieldInfo field => field.FieldType,
            _ => null
        };
        if (memberType != typeof(TTarget))
            throw new ArgumentException("The navigation member type must exactly match the target table model type.", nameof(navigation));
        NavigationMember = member.Member.Name;

        if (declaration.SourceTable != source.Table)
            throw new ArgumentException("The declared reference source must match the source table model.", nameof(declaration));
        if (declaration.TargetTable != target.Table)
            throw new ArgumentException("The declared reference target must match the target table model.", nameof(declaration));
        if (declaration.ColumnPairs.Any(pair =>
                !source.Columns.Values.Contains(pair.Source) ||
                !target.Columns.Values.Contains(pair.Target)))
        {
            throw new ArgumentException(
                "Every declared reference column must exactly match its source or target table model.",
                nameof(declaration));
        }
    }

    public GwTableModel<TSource> Source { get; }
    public GwTableModel<TTarget> Target { get; }
    public string NavigationMember { get; }
    public ReferenceJoin Declaration { get; }
    IReadOnlyDictionary<string, ColumnRef> IGwNavigation.TargetColumns => Target.Columns;
}

public sealed record GwColumn<T>(string MemberName, string ColumnName, QueryType Type, bool IsNullable = true,
    int? MaxLength = null, byte? DecimalPrecision = null, byte? DecimalScale = null,
    QueryStringComparisonPolicy StringComparison = QueryStringComparisonPolicy.Ordinal);

public sealed record GwElementSet<T>(string MemberName, string SetName, QueryType ElementType);

/// <summary>Closed query surface; deliberately does not implement <see cref="System.Linq.IQueryable"/>.</summary>
public interface IGwQueryable<T>
{
    QueryRequest ToQueryRequest();
    IGwQueryable<T> Join<TTarget>(GwReference<T, TTarget> reference);
    IGwQueryable<T> Where(Expression<Func<T, bool>> predicate);
    IGwQueryable<T> WhereIf(bool condition, Expression<Func<T, bool>> predicate);
    IGwQueryable<T> OrderBy<TKey>(Expression<Func<T, TKey>> selector);
    IGwQueryable<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> selector);
    IGwQueryable<T> ThenBy<TKey>(Expression<Func<T, TKey>> selector);
    IGwQueryable<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> selector);
    IGwQueryable<T> Skip(int count);
    IGwQueryable<T> Take(int count);
    IGwQueryable<TResult> Select<TResult>(Expression<Func<T, TResult>> selector);
    IGwQueryable<T> Distinct();
    IGwQueryable<T> AcceptScan(string id, string reason, string owner, DateTimeOffset expiresOn);
    IGwQueryable<T> LatestPer<TKey, TTimestamp>(Expression<Func<T, TKey>> key, Expression<Func<T, TTimestamp>> timestamp);
    LinqTerminal<T> ToList();
    Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default);
    LinqTerminal<long> Count();
    LinqTerminal<bool> Any();
    LinqTerminal<T> First();
    LinqTerminal<T> FirstOrDefault();
    LinqTerminal<T> Single();
    LinqTerminal<T> SingleOrDefault();
    LinqTerminal<long?> Sum(Expression<Func<T, int>> selector);
    LinqTerminal<long?> Sum(Expression<Func<T, int?>> selector);
    LinqTerminal<long?> Sum(Expression<Func<T, long>> selector);
    LinqTerminal<long?> Sum(Expression<Func<T, long?>> selector);
    LinqTerminal<decimal?> Sum(Expression<Func<T, decimal>> selector);
    LinqTerminal<decimal?> Sum(Expression<Func<T, decimal?>> selector);
    LinqTerminal<int?> Min(Expression<Func<T, int>> selector);
    LinqTerminal<int?> Min(Expression<Func<T, int?>> selector);
    LinqTerminal<long?> Min(Expression<Func<T, long>> selector);
    LinqTerminal<long?> Min(Expression<Func<T, long?>> selector);
    LinqTerminal<decimal?> Min(Expression<Func<T, decimal>> selector);
    LinqTerminal<decimal?> Min(Expression<Func<T, decimal?>> selector);
    LinqTerminal<string?> Min(Expression<Func<T, string?>> selector);
    LinqTerminal<DateTimeOffset?> Min(Expression<Func<T, DateTimeOffset>> selector);
    LinqTerminal<DateTimeOffset?> Min(Expression<Func<T, DateTimeOffset?>> selector);
    LinqTerminal<Guid?> Min(Expression<Func<T, Guid>> selector);
    LinqTerminal<Guid?> Min(Expression<Func<T, Guid?>> selector);
    LinqTerminal<int?> Max(Expression<Func<T, int>> selector);
    LinqTerminal<int?> Max(Expression<Func<T, int?>> selector);
    LinqTerminal<long?> Max(Expression<Func<T, long>> selector);
    LinqTerminal<long?> Max(Expression<Func<T, long?>> selector);
    LinqTerminal<decimal?> Max(Expression<Func<T, decimal>> selector);
    LinqTerminal<decimal?> Max(Expression<Func<T, decimal?>> selector);
    LinqTerminal<string?> Max(Expression<Func<T, string?>> selector);
    LinqTerminal<DateTimeOffset?> Max(Expression<Func<T, DateTimeOffset>> selector);
    LinqTerminal<DateTimeOffset?> Max(Expression<Func<T, DateTimeOffset?>> selector);
    LinqTerminal<Guid?> Max(Expression<Func<T, Guid>> selector);
    LinqTerminal<Guid?> Max(Expression<Func<T, Guid?>> selector);
}

/// <summary>A query terminal carrying the provider-neutral request, ready for a runtime adapter.</summary>
public sealed class LinqTerminal<TResult>
{
    internal LinqTerminal(QueryRequest request) => Request = request;
    public QueryRequest Request { get; }
    public Task<TResult> ExecuteAsync(Func<QueryRequest, Task<TResult>> executor) =>
        (executor ?? throw new ArgumentNullException(nameof(executor)))(Request);
}

/// <summary>Provider adapter seam for async terminal execution; the LINQ package performs no I/O.</summary>
public interface IGwQueryExecutor
{
    Task<IReadOnlyList<T>> ToListAsync<T>(QueryRequest request, GwTableModel<T>? model = null, CancellationToken cancellationToken = default);
    Task<long> CountAsync(QueryRequest request, CancellationToken cancellationToken = default);
    Task<bool> AnyAsync(QueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a provider-native scalar reduction. Implementations must not route this request
    /// through ordinary row materialization: the reduction shape is the execution contract.
    /// </summary>
    Task<TResult> ReduceAsync<TResult>(QueryRequest request, CancellationToken cancellationToken = default);
}

public static class GwQueryAsyncExtensions
{
    public static Task<IReadOnlyList<T>> ToListAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, CancellationToken cancellationToken = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (executor is null) throw new ArgumentNullException(nameof(executor));
        var model = query switch
        {
            GwQueryable<T> typed => typed.Model,
            GwQueryTable<T> table => table.Model,
            _ => null
        };
        if (model is null)
            throw new InvalidOperationException("The public executor extension requires a model-aware adapter; mapped projections must use an adapter-specific materializer.");
        return executor.ToListAsync(query.ToQueryRequest(), model, cancellationToken);
    }

    public static Task<long> CountAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, CancellationToken cancellationToken = default) =>
        (executor ?? throw new ArgumentNullException(nameof(executor))).CountAsync(query.Count().Request, cancellationToken);

    public static Task<bool> AnyAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, CancellationToken cancellationToken = default) =>
        (executor ?? throw new ArgumentNullException(nameof(executor))).AnyAsync(query.Any().Request, cancellationToken);

    public static Task<long?> SumAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, int>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, long?>(query, executor, () => query.Sum(selector).Request, cancellationToken);

    public static Task<long?> SumAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, int?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, long?>(query, executor, () => query.Sum(selector).Request, cancellationToken);

    public static Task<long?> SumAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, long>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, long?>(query, executor, () => query.Sum(selector).Request, cancellationToken);

    public static Task<long?> SumAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, long?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, long?>(query, executor, () => query.Sum(selector).Request, cancellationToken);

    public static Task<decimal?> SumAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, decimal>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, decimal?>(query, executor, () => query.Sum(selector).Request, cancellationToken);

    public static Task<decimal?> SumAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, decimal?>(query, executor, () => query.Sum(selector).Request, cancellationToken);

    public static Task<int?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, int>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, int?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<int?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, int?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, int?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<long?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, long>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, long?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<long?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, long?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, long?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<decimal?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, decimal>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, decimal?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<decimal?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, decimal?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<string?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, string?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, string?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<DateTimeOffset?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, DateTimeOffset>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, DateTimeOffset?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<DateTimeOffset?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, DateTimeOffset?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, DateTimeOffset?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<Guid?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, Guid>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, Guid?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<Guid?> MinAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, Guid?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, Guid?>(query, executor, () => query.Min(selector).Request, cancellationToken);

    public static Task<int?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, int>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, int?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static Task<int?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, int?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, int?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static Task<long?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, long>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, long?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static Task<long?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, long?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, long?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static Task<decimal?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, decimal>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, decimal?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static Task<decimal?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, decimal?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, decimal?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static Task<string?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, string?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, string?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static Task<DateTimeOffset?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, DateTimeOffset>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, DateTimeOffset?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static Task<DateTimeOffset?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, DateTimeOffset?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, DateTimeOffset?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static Task<Guid?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, Guid>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, Guid?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static Task<Guid?> MaxAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, Expression<Func<T, Guid?>> selector, CancellationToken cancellationToken = default) =>
        ExecuteReduction<T, Guid?>(query, executor, () => query.Max(selector).Request, cancellationToken);

    public static async Task<T> FirstAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, CancellationToken cancellationToken = default)
    {
        var rows = await ReadCardinalityAsync(query, executor, () => query.First().Request, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? throw new InvalidOperationException("Sequence contains no elements.") : rows[0];
    }

    public static async Task<T> FirstOrDefaultAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, CancellationToken cancellationToken = default)
    {
        var rows = await ReadCardinalityAsync(query, executor, () => query.FirstOrDefault().Request, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? default! : rows[0];
    }

    public static async Task<T> SingleAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, CancellationToken cancellationToken = default)
    {
        var rows = await ReadCardinalityAsync(query, executor, () => query.Single().Request, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) throw new InvalidOperationException("Sequence contains no elements.");
        if (rows.Count > 1) throw new InvalidOperationException("Sequence contains more than one element.");
        return rows[0];
    }

    public static async Task<T> SingleOrDefaultAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, CancellationToken cancellationToken = default)
    {
        var rows = await ReadCardinalityAsync(query, executor, () => query.SingleOrDefault().Request, cancellationToken).ConfigureAwait(false);
        if (rows.Count > 1) throw new InvalidOperationException("Sequence contains more than one element.");
        return rows.Count == 0 ? default! : rows[0];
    }

    private static Task<IReadOnlyList<T>> ReadCardinalityAsync<T>(IGwQueryable<T> query, IGwQueryExecutor executor, Func<QueryRequest> requestFactory, CancellationToken cancellationToken)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (executor is null) throw new ArgumentNullException(nameof(executor));
        if (requestFactory is null) throw new ArgumentNullException(nameof(requestFactory));
        var model = query switch
        {
            GwQueryable<T> typed => typed.Model,
            GwQueryTable<T> table => table.Model,
            _ => null
        };
        return executor.ToListAsync(requestFactory(), model, cancellationToken);
    }

    private static Task<TResult> ExecuteReduction<T, TResult>(
        IGwQueryable<T> query,
        IGwQueryExecutor executor,
        Func<QueryRequest> requestFactory,
        CancellationToken cancellationToken)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (executor is null) throw new ArgumentNullException(nameof(executor));
        if (requestFactory is null) throw new ArgumentNullException(nameof(requestFactory));
        return executor.ReduceAsync<TResult>(requestFactory(), cancellationToken);
    }
}

/// <summary>Creates closed typed query roots.</summary>
public sealed class GwQueryDatabase
{
    private readonly IGwQueryExecutor? executor;
    public GwQueryDatabase(IGwQueryExecutor? executor = null) => this.executor = executor;
    public GwQueryTable<T> Table<T>() => Table<T>(typeof(T).Name);
    public GwQueryTable<T> Table<T>(string name) => new(GwTableModel<T>.Infer(name), executor);
    public GwQueryTable<T> Table<T>(GwTableModel<T> model) => new(model ?? throw new ArgumentNullException(nameof(model)), executor);
}

/// <summary>Convenience factory for applications that do not need a database object.</summary>
public static class GwQuery
{
    public static GwQueryDatabase Database { get; } = new();
}

public sealed class GwQueryTable<T> : IGwQueryable<T>
{
    private readonly GwTableModel<T> model;
    private readonly IGwQueryExecutor? executor;
    internal GwTableModel<T> Model => model;
    internal GwQueryTable(GwTableModel<T> model, IGwQueryExecutor? executor = null) { this.model = model; this.executor = executor; }
    public IGwQueryable<T> Query => new GwQueryable<T>(model, executor);
    public IGwQueryable<T> AsQueryable() => Query;
    private IGwQueryable<T> Root => Query;
    public QueryRequest ToQueryRequest() => Root.ToQueryRequest();
    public IGwQueryable<T> Join<TTarget>(GwReference<T, TTarget> reference) => Root.Join(reference);
    public IGwQueryable<T> Where(Expression<Func<T, bool>> predicate) => Root.Where(predicate);
    public IGwQueryable<T> WhereIf(bool condition, Expression<Func<T, bool>> predicate) => Root.WhereIf(condition, predicate);
    public IGwQueryable<T> OrderBy<TKey>(Expression<Func<T, TKey>> selector) => Root.OrderBy(selector);
    public IGwQueryable<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> selector) => Root.OrderByDescending(selector);
    public IGwQueryable<T> ThenBy<TKey>(Expression<Func<T, TKey>> selector) => Root.ThenBy(selector);
    public IGwQueryable<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> selector) => Root.ThenByDescending(selector);
    public IGwQueryable<T> Skip(int count) => Root.Skip(count);
    public IGwQueryable<T> Take(int count) => Root.Take(count);
    public IGwQueryable<TResult> Select<TResult>(Expression<Func<T, TResult>> selector) => Root.Select(selector);
    public IGwQueryable<T> Distinct() => Root.Distinct();
    public IGwQueryable<T> AcceptScan(string id, string reason, string owner, DateTimeOffset expiresOn) => Root.AcceptScan(id, reason, owner, expiresOn);
    public IGwQueryable<T> LatestPer<TKey, TTimestamp>(Expression<Func<T, TKey>> key, Expression<Func<T, TTimestamp>> timestamp) => Root.LatestPer(key, timestamp);
    public LinqTerminal<T> ToList() => Root.ToList();
    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default) => Root.ToListAsync(cancellationToken);
    public LinqTerminal<long> Count() => Root.Count();
    public LinqTerminal<bool> Any() => Root.Any();
    public LinqTerminal<T> First() => Root.First();
    public LinqTerminal<T> FirstOrDefault() => Root.FirstOrDefault();
    public LinqTerminal<T> Single() => Root.Single();
    public LinqTerminal<T> SingleOrDefault() => Root.SingleOrDefault();
    public LinqTerminal<long?> Sum(Expression<Func<T, int>> selector) => Root.Sum(selector);
    public LinqTerminal<long?> Sum(Expression<Func<T, int?>> selector) => Root.Sum(selector);
    public LinqTerminal<long?> Sum(Expression<Func<T, long>> selector) => Root.Sum(selector);
    public LinqTerminal<long?> Sum(Expression<Func<T, long?>> selector) => Root.Sum(selector);
    public LinqTerminal<decimal?> Sum(Expression<Func<T, decimal>> selector) => Root.Sum(selector);
    public LinqTerminal<decimal?> Sum(Expression<Func<T, decimal?>> selector) => Root.Sum(selector);
    public LinqTerminal<int?> Min(Expression<Func<T, int>> selector) => Root.Min(selector);
    public LinqTerminal<int?> Min(Expression<Func<T, int?>> selector) => Root.Min(selector);
    public LinqTerminal<long?> Min(Expression<Func<T, long>> selector) => Root.Min(selector);
    public LinqTerminal<long?> Min(Expression<Func<T, long?>> selector) => Root.Min(selector);
    public LinqTerminal<decimal?> Min(Expression<Func<T, decimal>> selector) => Root.Min(selector);
    public LinqTerminal<decimal?> Min(Expression<Func<T, decimal?>> selector) => Root.Min(selector);
    public LinqTerminal<string?> Min(Expression<Func<T, string?>> selector) => Root.Min(selector);
    public LinqTerminal<DateTimeOffset?> Min(Expression<Func<T, DateTimeOffset>> selector) => Root.Min(selector);
    public LinqTerminal<DateTimeOffset?> Min(Expression<Func<T, DateTimeOffset?>> selector) => Root.Min(selector);
    public LinqTerminal<Guid?> Min(Expression<Func<T, Guid>> selector) => Root.Min(selector);
    public LinqTerminal<Guid?> Min(Expression<Func<T, Guid?>> selector) => Root.Min(selector);
    public LinqTerminal<int?> Max(Expression<Func<T, int>> selector) => Root.Max(selector);
    public LinqTerminal<int?> Max(Expression<Func<T, int?>> selector) => Root.Max(selector);
    public LinqTerminal<long?> Max(Expression<Func<T, long>> selector) => Root.Max(selector);
    public LinqTerminal<long?> Max(Expression<Func<T, long?>> selector) => Root.Max(selector);
    public LinqTerminal<decimal?> Max(Expression<Func<T, decimal>> selector) => Root.Max(selector);
    public LinqTerminal<decimal?> Max(Expression<Func<T, decimal?>> selector) => Root.Max(selector);
    public LinqTerminal<string?> Max(Expression<Func<T, string?>> selector) => Root.Max(selector);
    public LinqTerminal<DateTimeOffset?> Max(Expression<Func<T, DateTimeOffset>> selector) => Root.Max(selector);
    public LinqTerminal<DateTimeOffset?> Max(Expression<Func<T, DateTimeOffset?>> selector) => Root.Max(selector);
    public LinqTerminal<Guid?> Max(Expression<Func<T, Guid>> selector) => Root.Max(selector);
    public LinqTerminal<Guid?> Max(Expression<Func<T, Guid?>> selector) => Root.Max(selector);
}
