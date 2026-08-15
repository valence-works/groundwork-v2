using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Groundwork.Testing;

namespace Groundwork.Query.Linq.Sqlite;

/// <summary>Configured async adapter for a closed LINQ database over an existing SQLite session.</summary>
public sealed class SqliteLinqExecutor : IGwQueryExecutor
{
    private readonly IStorageSession session;
    public SqliteLinqExecutor(IStorageSession session) => this.session = session ?? throw new ArgumentNullException(nameof(session));

    public Task<IReadOnlyList<T>> ToListAsync<T>(QueryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<T> rows = session.Query(request).Rows.Select(Materialize<T>).ToArray();
        return Task.FromResult(rows);
    }

    public Task<long> CountAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = session.Query(request);
        return Task.FromResult(result.TotalCount ?? result.Rows.Count);
    }

    public Task<bool> AnyAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(session.Query(request).Rows.Count != 0);
    }

    private static T Materialize<T>(IReadOnlyDictionary<string, object?> row)
    {
        if (typeof(T) == typeof(IReadOnlyDictionary<string, object?>)) return (T)(object)row;
        var value = Activator.CreateInstance<T>();
        foreach (var property in typeof(T).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(property => property.CanWrite))
            if (row.TryGetValue(property.Name, out var raw))
                property.SetValue(value, raw is null ? null : ConvertValue(raw, property.PropertyType));
        return value;
    }

    private static object? ConvertValue(object value, Type target)
    {
        var core = Nullable.GetUnderlyingType(target) ?? target;
        return core.IsInstanceOfType(value) ? value : Convert.ChangeType(value, core, System.Globalization.CultureInfo.InvariantCulture);
    }
}
