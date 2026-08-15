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

    internal RecordTable(KernelStorageUnit definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        accessor = RecordAccessor<T>.Instance;
        queryModel = CreateQueryModel(definition, accessor.Members);
    }

    /// <summary>The provider-neutral declaration produced by the typed authoring surface.</summary>
    public KernelStorageUnit Definition { get; }

    /// <summary>Closed query root using this table's declared names and query types.</summary>
    public IGwQueryable<T> Query => new GwQueryDatabase().Table(queryModel).Query;

    /// <summary>Number of compiled accessors for this CLR row type (one per process).</summary>
    public static int AccessorCompilationCount => RecordAccessor<T>.CompilationCount;

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
        accessor.Read(values ?? throw new ArgumentNullException(nameof(values)));

    public T Materialize(RowValues values) => FromRowValues(values);

    public RecordTableSession<T> Open(IRecordStore store) =>
        new(this, store ?? throw new ArgumentNullException(nameof(store)));

    public RecordTableSession<T> Use(IRecordStore store) => Open(store);

    internal T Read(RowValues values) => accessor.Read(values);

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

    public IReadOnlyList<T> Query(IGwQueryable<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return store.Query(query.ToQueryRequest()).Rows.Select(table.FromRowValues).ToArray();
    }

    public IReadOnlyList<T> Query(Func<IGwQueryable<T>, IGwQueryable<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return Query(configure(table.Query));
    }

    public long Count(IGwQueryable<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var result = store.Query(query.Count().Request);
        return result.TotalCount ?? result.Rows.Count;
    }

    public bool Any(IGwQueryable<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return store.Query(query.Any().Request).Rows.Count != 0;
    }

    private void ValidateOptions(RecordWriteOptions? options)
    {
        if (options?.ExpectedVersion is not null && !table.Definition.Concurrency.IsOptimistic)
            throw new InvalidOperationException(
                $"Storage unit '{table.Definition.Name}' does not declare version machinery.");
    }
}
