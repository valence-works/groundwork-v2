using System.Linq.Expressions;
using Groundwork.Kernel;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Records;

public sealed partial class RecordTable<T>
{
    private readonly RecordAccessor<T> accessor;
    private readonly GwTableModel<T> queryModel;
    private readonly IReadOnlySet<string> optionalReadColumns;

    internal RecordTable(KernelStorageUnit definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        accessor = RecordAccessor<T>.Instance;
        queryModel = CreateQueryModel(definition, accessor.Members);
        optionalReadColumns = definition.Concurrency is { IsOptimistic: true, TokenColumn: { } token }
            ? new HashSet<string>([token], StringComparer.Ordinal)
            : EmptyColumns.Instance;
    }

    /// <summary>The provider-neutral declaration produced by the typed authoring surface.</summary>
    public KernelStorageUnit Definition { get; }

    /// <summary>Closed query root using this table's declared names and query types.</summary>
    public IGwQueryable<T> Query => new GwQueryDatabase().Table(queryModel).Query;

    /// <summary>Number of compiled accessors for this CLR row type (one per process).</summary>
    public static int AccessorCompilationCount => RecordAccessor<T>.CompilationCount;

    /// <summary>Number of reflection-based member inspections for this CLR row type (one per process).</summary>
    public static int AccessorReflectionInspectionCount => RecordAccessor<T>.ReflectionInspectionCount;

    public RowValues ToRowValues(T value)
    {
        var mapped = accessor.Write(value, Definition.Columns);
        if (Definition.Concurrency.IsOptimistic && Definition.Concurrency.TokenColumn is { } token)
        {
            mapped = new RowValues(mapped.Values
                .Where(pair => !string.Equals(pair.Key, token, StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
        }

        return mapped;
    }

    public RowValues Map(T value) => ToRowValues(value);

    public T FromRowValues(RowValues values) =>
        accessor.Read(values ?? throw new ArgumentNullException(nameof(values)), optionalReadColumns);

    public T Materialize(RowValues values) => FromRowValues(values);

    public RecordTableSession<T> Open(IRecordStore store) =>
        new(this, store ?? throw new ArgumentNullException(nameof(store)));

    public RecordTableSession<T> Use(IRecordStore store) => Open(store);

    /// <summary>
    /// Creates a typed projection that retains both its provider query and its compiled result shape.
    /// Only direct record members may supply projected values; constants may be used for intentionally
    /// omitted members of a same-type shape.
    /// </summary>
    public RecordProjection<TResult> Select<TResult>(
        IGwQueryable<T> query,
        Expression<Func<T, TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(selector);
        ValidateRequest(query.ToQueryRequest());
        var projected = query.Select(selector);
        return new RecordProjection<TResult>(this, projected.ToQueryRequest(),
            RecordProjectionAccessor.Compile(selector, accessor.Members));
    }

    public RecordProjection<TResult> Project<TResult>(
        IGwQueryable<T> query,
        Expression<Func<T, TResult>> selector) => Select(query, selector);

    internal T Read(RowValues values) => accessor.Read(values, optionalReadColumns);

    internal void ValidateRequest(QueryRequest request)
    {
        if (!string.Equals(request.Table.Value, Definition.Name, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"A query for table '{request.Table.Value}' cannot be executed by record table '{Definition.Name}'.");
    }

    internal QueryRenderOptions CreateRenderOptions(RecordQueryOptions? options)
    {
        var indexes = Definition.Indexes.Select(index => new QueryIndexDeclaration(
            index.Name,
            index.Columns.Select(indexColumn =>
            {
                var column = Definition.Columns.Single(declaration =>
                    string.Equals(declaration.Name, indexColumn.Column, StringComparison.Ordinal));
                if (!TryGetQueryType(column.Type, out var queryType))
                    throw new InvalidOperationException(
                        $"Index '{index.Name}' column '{column.Name}' cannot participate in a typed record query.");
                return new QueryIndexColumn(column.Name, column.IsNullable, queryType);
            })));
        var renderOptions = new QueryRenderOptions(indexes, options?.SelectedIndex);
        _ = renderOptions.FindSelectedIndex();
        return renderOptions;
    }

    private static GwTableModel<T> CreateQueryModel(
        KernelStorageUnit definition,
        IReadOnlyList<RecordMember> members)
    {
        var columns = new List<GwColumn<T>>();
        foreach (var member in members)
        {
            var declaration = definition.Columns.FirstOrDefault(column =>
                string.Equals(column.Name, member.ColumnName, StringComparison.Ordinal));
            if (declaration is null)
                continue;
            if (definition.Concurrency is { IsOptimistic: true, TokenColumn: { } token } &&
                string.Equals(declaration.Name, token, StringComparison.Ordinal))
                continue;
            if (!TryGetQueryType(declaration.Type, out var type))
                continue;

            columns.Add(new GwColumn<T>(
                member.Name,
                member.ColumnName,
                type,
                declaration.IsNullable,
                declaration.MaxLength,
                declaration.Precision is { } precision ? checked((byte)precision) : null,
                declaration.Scale is { } scale ? checked((byte)scale) : null,
                declaration.Collation == PortableCollation.OrdinalIgnoreCase ||
                declaration.Collation == PortableCollation.UnicodeOrdinalIgnoreCase
                    ? QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase
                    : QueryStringComparisonPolicy.Ordinal));
        }

        if (columns.Count == 0)
            throw new InvalidOperationException($"Record table '{definition.Name}' has no queryable columns.");
        return new GwTableModel<T>(definition.Name, columns);
    }

    private static bool TryGetQueryType(PortableType type, out QueryType queryType)
    {
        queryType = type switch
        {
            PortableType.Boolean => QueryType.Boolean,
            PortableType.Int32 => QueryType.Int32,
            PortableType.Int64 => QueryType.Int64,
            PortableType.Decimal => QueryType.Decimal,
            PortableType.String => QueryType.String,
            PortableType.DateTimeOffset => QueryType.DateTimeOffset,
            PortableType.Guid => QueryType.Guid,
            PortableType.Binary => QueryType.Binary,
            _ => default
        };
        return type != PortableType.Json;
    }
}

/// <summary>Typed CRUD and query execution over a provider-neutral record store.</summary>
public sealed class RecordTableSession<T>
{
    private readonly RecordTable<T> table;
    private readonly IRecordStore store;

    internal RecordTableSession(RecordTable<T> table, IRecordStore store)
    {
        this.table = table;
        this.store = store;
    }

    public RecordWriteResult Insert(T value, RecordWriteOptions? options = null)
    {
        ValidateOptions(options);
        return store.Insert(table.Definition, table.ToRowValues(value), options);
    }

    public RecordWriteResult Update(T value, RecordWriteOptions? options = null)
    {
        ValidateOptions(options);
        return store.Update(table.Definition, table.ToRowValues(value), options);
    }

    public RecordWriteResult Upsert(T value, RecordWriteOptions? options = null)
    {
        ValidateOptions(options);
        return store.Upsert(table.Definition, table.ToRowValues(value), options);
    }

    public RecordWriteResult Delete(T value, RecordWriteOptions? options = null)
    {
        ValidateOptions(options);
        var mapped = table.ToRowValues(value);
        var key = new RowValues(table.Definition.Key.Columns.ToDictionary(
            name => name,
            name => mapped[name],
            StringComparer.Ordinal));
        return store.Delete(table.Definition, key, options);
    }

    public IReadOnlyList<T> Query(IGwQueryable<T> query, RecordQueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var request = query.ToQueryRequest();
        table.ValidateRequest(request);
        return store.Query(request, table.CreateRenderOptions(options)).Rows.Select(table.FromRowValues).ToArray();
    }

    public IReadOnlyList<T> Query(
        Func<IGwQueryable<T>, IGwQueryable<T>> configure,
        RecordQueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return Query(configure(table.Query), options);
    }

    public IReadOnlyList<TResult> Query<TResult>(
        RecordProjection<TResult> projection,
        RecordQueryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(projection);
        projection.EnsureOwner(table);
        return store.Query(projection.Request, table.CreateRenderOptions(options)).Rows
            .Select(projection.Materialize)
            .ToArray();
    }

    public long Count(IGwQueryable<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var request = query.Count().Request;
        table.ValidateRequest(request);
        var result = store.Query(QueryRequestExecution.ForProviderCount(request), table.CreateRenderOptions(null));
        return QueryRequestExecution.RequireTotalCount(request, result.TotalCount);
    }

    public bool Any(IGwQueryable<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var request = query.Any().Request;
        table.ValidateRequest(request);
        return store.Query(request, table.CreateRenderOptions(null)).Rows.Count != 0;
    }

    private void ValidateOptions(RecordWriteOptions? options)
    {
        if (options?.ExpectedVersion is not null && !table.Definition.Concurrency.IsOptimistic)
            throw new InvalidOperationException(
                $"Storage unit '{table.Definition.Name}' does not declare version machinery. " +
                "Declare .OptimisticConcurrency() before using RecordWriteOptions.IfVersion(...).");
    }
}

/// <summary>Provider-neutral options for a typed Records query.</summary>
public sealed record RecordQueryOptions
{
    public RecordQueryOptions(string? selectedIndex = null)
    {
        if (selectedIndex is not null && string.IsNullOrWhiteSpace(selectedIndex))
            throw new ArgumentException("A selected index cannot be blank.", nameof(selectedIndex));
        SelectedIndex = selectedIndex;
    }

    /// <summary>The declared logical index the provider must select or verify in its native plan.</summary>
    public string? SelectedIndex { get; }

    public static RecordQueryOptions UsingIndex(string name) => new(name);
}
