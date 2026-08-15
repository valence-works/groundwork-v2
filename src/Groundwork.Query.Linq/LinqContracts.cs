using System.Linq.Expressions;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Groundwork.Query.Model;

namespace Groundwork.Query.Linq;

/// <summary>Marks a static expression-producing member as syntactically inlineable by the lowerer.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class GwQueryFragmentAttribute : Attribute
{
}

/// <summary>Declares the text comparison policy used by a mapped member for analyzer fix-it safety.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class GwStringComparisonAttribute : Attribute
{
    public GwStringComparisonAttribute(StringComparison comparison) => Comparison = comparison;
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
        this.columns = supplied.ToDictionary(x => x.MemberName, x => new ColumnRef(new TableId(name), x.ColumnName, x.Type,
            x.IsNullable, x.MaxLength, x.DecimalPrecision, x.DecimalScale, x.StringComparison), StringComparer.Ordinal);
        if (this.columns.Count != supplied.Length) throw new ArgumentException("Column members must be unique.", nameof(columns));
        var sets = (elementSets ?? Array.Empty<GwElementSet<T>>()).ToArray();
        this.elementSets = sets.ToDictionary(x => x.MemberName, x => new ElementSetRef(x.SetName, x.ElementType), StringComparer.Ordinal);
        if (this.elementSets.Count != sets.Length) throw new ArgumentException("Element-set members must be unique.", nameof(elementSets));
    }

    public string Name { get; }
    public TableId Table => new(Name);
    public IReadOnlyDictionary<string, ColumnRef> Columns => columns;
    public IReadOnlyDictionary<string, ElementSetRef> ElementSets => elementSets;

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

public sealed record GwColumn<T>(string MemberName, string ColumnName, QueryType Type, bool IsNullable = true,
    int? MaxLength = null, byte? DecimalPrecision = null, byte? DecimalScale = null,
    QueryStringComparisonPolicy StringComparison = QueryStringComparisonPolicy.Ordinal);

public sealed record GwElementSet<T>(string MemberName, string SetName, QueryType ElementType);

/// <summary>Closed query surface; deliberately does not implement <see cref="System.Linq.IQueryable"/>.</summary>
public interface IGwQueryable<T>
{
    QueryRequest ToQueryRequest();
    IGwQueryable<T> Where(Expression<Func<T, bool>> predicate);
    IGwQueryable<T> WhereIf(bool condition, Expression<Func<T, bool>> predicate);
    IGwQueryable<T> OrderBy<TKey>(Expression<Func<T, TKey>> selector);
    IGwQueryable<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> selector);
    IGwQueryable<T> ThenBy<TKey>(Expression<Func<T, TKey>> selector);
    IGwQueryable<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> selector);
    IGwQueryable<T> Skip(int count);
    IGwQueryable<T> Take(int count);
    IGwQueryable<TResult> Select<TResult>(Expression<Func<T, TResult>> selector);
    IGwQueryable<T> AcceptScan(string id, string reason, string owner, DateTimeOffset expiresOn);
    IGwQueryable<T> LatestPer<TKey, TTimestamp>(Expression<Func<T, TKey>> key, Expression<Func<T, TTimestamp>> timestamp);
    LinqTerminal<T> ToList();
    Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default);
    LinqTerminal<long> Count();
    LinqTerminal<bool> Any();
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
    Task<IReadOnlyList<T>> ToListAsync<T>(QueryRequest request, CancellationToken cancellationToken = default);
    Task<long> CountAsync(QueryRequest request, CancellationToken cancellationToken = default);
    Task<bool> AnyAsync(QueryRequest request, CancellationToken cancellationToken = default);
}

public static class GwQueryAsyncExtensions
{
    public static Task<IReadOnlyList<T>> ToListAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, CancellationToken cancellationToken = default) =>
        (executor ?? throw new ArgumentNullException(nameof(executor))).ToListAsync<T>(query.ToQueryRequest(), cancellationToken);

    public static Task<long> CountAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, CancellationToken cancellationToken = default) =>
        (executor ?? throw new ArgumentNullException(nameof(executor))).CountAsync(query.Count().Request, cancellationToken);

    public static Task<bool> AnyAsync<T>(this IGwQueryable<T> query, IGwQueryExecutor executor, CancellationToken cancellationToken = default) =>
        (executor ?? throw new ArgumentNullException(nameof(executor))).AnyAsync(query.Any().Request, cancellationToken);
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
    internal GwQueryTable(GwTableModel<T> model, IGwQueryExecutor? executor = null) { this.model = model; this.executor = executor; }
    public IGwQueryable<T> Query => new GwQueryable<T>(model, executor);
    public IGwQueryable<T> AsQueryable() => Query;
    private IGwQueryable<T> Root => Query;
    public QueryRequest ToQueryRequest() => Root.ToQueryRequest();
    public IGwQueryable<T> Where(Expression<Func<T, bool>> predicate) => Root.Where(predicate);
    public IGwQueryable<T> WhereIf(bool condition, Expression<Func<T, bool>> predicate) => Root.WhereIf(condition, predicate);
    public IGwQueryable<T> OrderBy<TKey>(Expression<Func<T, TKey>> selector) => Root.OrderBy(selector);
    public IGwQueryable<T> OrderByDescending<TKey>(Expression<Func<T, TKey>> selector) => Root.OrderByDescending(selector);
    public IGwQueryable<T> ThenBy<TKey>(Expression<Func<T, TKey>> selector) => Root.ThenBy(selector);
    public IGwQueryable<T> ThenByDescending<TKey>(Expression<Func<T, TKey>> selector) => Root.ThenByDescending(selector);
    public IGwQueryable<T> Skip(int count) => Root.Skip(count);
    public IGwQueryable<T> Take(int count) => Root.Take(count);
    public IGwQueryable<TResult> Select<TResult>(Expression<Func<T, TResult>> selector) => Root.Select(selector);
    public IGwQueryable<T> AcceptScan(string id, string reason, string owner, DateTimeOffset expiresOn) => Root.AcceptScan(id, reason, owner, expiresOn);
    public IGwQueryable<T> LatestPer<TKey, TTimestamp>(Expression<Func<T, TKey>> key, Expression<Func<T, TTimestamp>> timestamp) => Root.LatestPer(key, timestamp);
    public LinqTerminal<T> ToList() => Root.ToList();
    public Task<IReadOnlyList<T>> ToListAsync(CancellationToken cancellationToken = default) => Root.ToListAsync(cancellationToken);
    public LinqTerminal<long> Count() => Root.Count();
    public LinqTerminal<bool> Any() => Root.Any();
}
