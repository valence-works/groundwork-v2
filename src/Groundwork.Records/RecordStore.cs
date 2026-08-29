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

    private readonly IReadOnlyDictionary<string, RecordReferenceBinding> references;

    internal RecordTable(
        KernelStorageUnit definition,
        IEnumerable<RecordReferenceBinding>? referenceBindings = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        accessor = RecordAccessor<T>.Instance;
        queryModel = CreateQueryModel(definition, accessor.Members);
        references = (referenceBindings ?? [])
            .ToDictionary(reference => reference.Name, StringComparer.Ordinal);
        optionalReadColumns = new HashSet<string>(
            accessor.Members
                .Where(member => definition.Columns.All(column =>
                    !string.Equals(column.Name, member.ColumnName, StringComparison.Ordinal)))
                .Select(member => member.ColumnName)
                .Concat(definition.Concurrency is { IsOptimistic: true, TokenColumn: { } token }
                    ? [token]
                    : []),
            StringComparer.Ordinal);
    }

    /// <summary>The provider-neutral declaration produced by the typed authoring surface.</summary>
    public KernelStorageUnit Definition { get; }

    /// <summary>Closed query root using this table's declared names and query types.</summary>
    public IGwQueryable<T> Query => new GwQueryDatabase().Table(queryModel).Query;

    /// <summary>Number of compatibility accessors compiled for this CLR row type (zero when generated).</summary>
    public static int AccessorCompilationCount => RecordAccessor<T>.CompilationCount;

    /// <summary>Number of compatibility reflection inspections for this CLR row type (zero when generated).</summary>
    public static int AccessorReflectionInspectionCount => RecordAccessor<T>.ReflectionInspectionCount;

    /// <summary>Number of runtime accessor delegates compiled for this CLR row type.</summary>
    public static int AccessorDynamicCodeGenerationCount => RecordAccessor<T>.DynamicCodeGenerationCount;

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

    /// <summary>Resolves one typed navigation that was declared by this record table's builder.</summary>
    public RecordReference<T, TTarget> Reference<TTarget>(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A record reference name is required.", nameof(name));
        if (!references.TryGetValue(name, out var binding) ||
            binding.Target is not RecordTable<TTarget> target ||
            binding.Navigation is not Expression<Func<T, TTarget>> navigation)
        {
            throw new ArgumentException(
                $"Record reference '{name}' is not bound from '{typeof(T).FullName}' to '{typeof(TTarget).FullName}'.",
                nameof(name));
        }

        var definition = Definition.References.Single(reference =>
            string.Equals(reference.Name, name, StringComparison.Ordinal));
        var pairs = definition.Columns.Zip(target.Definition.Key.Columns, (source, targetColumn) =>
            new JoinColumnPair(
                queryModel.Columns.Values.Single(column => string.Equals(column.Name, source, StringComparison.Ordinal)),
                target.queryModel.Columns.Values.Single(column => string.Equals(column.Name, targetColumn, StringComparison.Ordinal))));
        var declaration = new ReferenceJoin(name, target.queryModel.Table, pairs);
        return new RecordReference<T, TTarget>(
            this,
            target,
            queryModel.Reference(navigation, target.queryModel, declaration));
    }

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

    /// <summary>
    /// Creates a terminal typed projection over both sides of one activated record reference.
    /// The selector is compiled once and result fields remain table-qualified through provider I/O.
    /// </summary>
    public RecordProjection<TResult> Select<TTarget, TResult>(
        IGwQueryable<T> query,
        RecordReference<T, TTarget> reference,
        Expression<Func<T, TTarget, TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(selector);
        reference.EnsureSource(this);
        var request = query.ToQueryRequest();
        ValidateRequest(request);
        reference.EnsureActivated(request);
        var compiled = RecordProjectionAccessor.CompileJoined(
            selector,
            this,
            reference.Target,
            request.Join!);
        if (compiled.Columns.Count == 0)
            throw new ArgumentException("A joined record projection must select at least one source or target member.", nameof(selector));
        return new RecordProjection<TResult>(
            this,
            WithProjection(request, compiled.Columns),
            compiled.Materializer);
    }

    /// <summary>
    /// Binds typed selectors to one aggregation profile declared by this table. The selectors can
    /// read only the profile's declared group and reducer aliases; the profile itself supplies all
    /// grouping, reducer, and budget semantics.
    /// </summary>
    public RecordAggregationBinding<TGroup, TResult> Aggregate<TGroup, TResult>(
        string profileName,
        Expression<Func<AggregationRow, TGroup>> groupSelector,
        Expression<Func<AggregationRow, TResult>> resultSelector) =>
        RecordAggregationBindingFactory.Create(this, profileName, groupSelector, resultSelector);

    /// <summary>Convenience form for a profile with one declared group alias.</summary>
    public RecordAggregationBinding<TGroup, TResult> Aggregate<TGroup, TResult>(
        string profileName,
        string groupAlias,
        Expression<Func<AggregationRow, TResult>> resultSelector)
    {
        if (string.IsNullOrWhiteSpace(groupAlias))
            throw new ArgumentException("An aggregation group alias is required.", nameof(groupAlias));
        ArgumentNullException.ThrowIfNull(resultSelector);
        var row = Expression.Parameter(typeof(AggregationRow), "row");
        var group = Expression.Lambda<Func<AggregationRow, TGroup>>(
            Expression.Call(
                typeof(AggregationRowExtensions),
                nameof(AggregationRowExtensions.Get),
                [typeof(TGroup)],
                row,
                Expression.Constant(groupAlias)),
            row);
        return Aggregate(profileName, group, resultSelector);
    }

    internal T Read(RowValues values) => accessor.Read(values, optionalReadColumns);

    internal IReadOnlyList<RecordMember> Members => accessor.Members;
    internal GwTableModel<T> QueryModel => queryModel;

    internal T ReadQualified(RowValues values, ReferenceJoin join)
    {
        var logical = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in queryModel.Columns.Values)
        {
            if (values.Values.TryGetValue(QueryRequestExecution.ResultFieldName(join, column), out var value))
                logical[column.Name] = value;
        }
        return accessor.Read(new RowValues(logical), optionalReadColumns);
    }

    internal void EnsureWholeRecordQueryable()
    {
        var missing = accessor.Members.FirstOrDefault(member =>
            !optionalReadColumns.Contains(member.ColumnName) &&
            !queryModel.Columns.ContainsKey(member.Name));
        if (missing is not null)
        {
            throw new ArgumentException(
                $"Whole-record joined projection is unavailable because member '{missing.Name}' is not a queryable scalar column.");
        }
    }

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

    private static QueryRequest WithProjection(QueryRequest request, IReadOnlyList<ColumnRef> columns) =>
        new(
            request.Table,
            request.Join ?? throw new InvalidOperationException("A joined record projection requires one activated reference."),
            request.Where,
            request.Order,
            Projection.ColumnsOnly(columns),
            request.Paging,
            request.Result,
            request.LatestPerKey,
            request.AcceptedScan,
            request.Distinct);
}

internal sealed record RecordReferenceBinding(
    string Name,
    LambdaExpression Navigation,
    System.Reflection.MemberInfo NavigationMember,
    object Target);

/// <summary>A declared typed record navigation and its target materializer.</summary>
public sealed class RecordReference<TSource, TTarget>
{
    private readonly RecordTable<TSource> source;
    private readonly GwReference<TSource, TTarget> queryReference;

    internal RecordReference(
        RecordTable<TSource> source,
        RecordTable<TTarget> target,
        GwReference<TSource, TTarget> queryReference)
    {
        this.source = source;
        Target = target;
        this.queryReference = queryReference;
    }

    internal RecordTable<TTarget> Target { get; }

    /// <summary>Activates this declared reference on a source record query.</summary>
    public IGwQueryable<TSource> Join(IGwQueryable<TSource> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Join(queryReference);
    }

    internal void EnsureSource(RecordTable<TSource> expected)
    {
        if (!ReferenceEquals(source, expected))
            throw new InvalidOperationException("A record reference must be projected by the source table that declared it.");
    }

    internal void EnsureActivated(QueryRequest request)
    {
        if (!ReferenceEquals(request.Join, queryReference.Declaration))
            throw new InvalidOperationException("The joined projection requires the same activated record reference.");
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
        if (request.Join is not null)
        {
            throw new InvalidOperationException(
                "A joined Records query must use the terminal table.Select(query, reference, selector) projection surface.");
        }
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
        return store.Query(QueryRequestExecution.ForExistenceProbe(request), table.CreateRenderOptions(null)).Rows.Count != 0;
    }

    /// <summary>Executes a typed declared aggregation through the provider's covered aggregation path.</summary>
    public IReadOnlyList<RecordAggregationResult<TGroup, TResult>> Aggregate<TGroup, TResult>(
        RecordAggregationBinding<TGroup, TResult> binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.EnsureOwner(table);
        var result = RequireAggregationStore().Aggregate(table.Definition, binding.Query);
        return result.Rows.Select(binding.Materialize).ToArray();
    }

    /// <summary>Creates and executes a typed declared aggregation in one call.</summary>
    public IReadOnlyList<RecordAggregationResult<TGroup, TResult>> Aggregate<TGroup, TResult>(
        string profileName,
        Expression<Func<AggregationRow, TGroup>> groupSelector,
        Expression<Func<AggregationRow, TResult>> resultSelector) =>
        Aggregate(table.Aggregate(profileName, groupSelector, resultSelector));

    /// <summary>Asynchronously executes a typed declared aggregation through the provider's covered path.</summary>
    public async Task<IReadOnlyList<RecordAggregationResult<TGroup, TResult>>> AggregateAsync<TGroup, TResult>(
        RecordAggregationBinding<TGroup, TResult> binding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.EnsureOwner(table);
        var result = await RequireAggregationStore()
            .AggregateAsync(table.Definition, binding.Query, cancellationToken)
            .ConfigureAwait(false);
        return result.Rows.Select(binding.Materialize).ToArray();
    }

    /// <summary>Creates and asynchronously executes a typed declared aggregation in one call.</summary>
    public Task<IReadOnlyList<RecordAggregationResult<TGroup, TResult>>> AggregateAsync<TGroup, TResult>(
        string profileName,
        Expression<Func<AggregationRow, TGroup>> groupSelector,
        Expression<Func<AggregationRow, TResult>> resultSelector,
        CancellationToken cancellationToken = default) =>
        AggregateAsync(table.Aggregate(profileName, groupSelector, resultSelector), cancellationToken);

    private IRecordAggregationStore RequireAggregationStore() => store as IRecordAggregationStore ??
        throw new InvalidOperationException(
            "The configured record store does not support declared aggregation execution. " +
            "Provide an adapter implementing IRecordAggregationStore.");

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
