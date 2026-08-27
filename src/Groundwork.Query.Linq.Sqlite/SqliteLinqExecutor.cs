using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Groundwork.Store;

namespace Groundwork.Query.Linq.Sqlite;

/// <summary>Configured async adapter for a closed LINQ database over an existing SQLite session.</summary>
public sealed class SqliteLinqExecutor : IGwQueryExecutor
{
    private readonly IStorageSession session;

    public SqliteLinqExecutor(IStorageSession session) =>
        this.session = session ?? throw new ArgumentNullException(nameof(session));

    public async Task<IReadOnlyList<T>> ToListAsync<T>(QueryRequest request, GwTableModel<T>? model = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await QueryAsync(request, cancellationToken).ConfigureAwait(false);
        return result.Rows.Select(row => Materialize<T>(row, model)).ToArray();
    }

    public async Task<long> CountAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await QueryAsync(QueryRequestExecution.ForProviderCount(request), cancellationToken).ConfigureAwait(false);
        return QueryRequestExecution.RequireTotalCount(request, result.TotalCount);
    }

    public async Task<bool> AnyAsync(QueryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await QueryAsync(QueryRequestExecution.ForExistenceProbe(request), cancellationToken).ConfigureAwait(false);
        return result.Rows.Count != 0;
    }

    /// <summary>Uses the session's async query capability when advertised; otherwise the query completes synchronously.</summary>
    private Task<QueryMaterializedResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        if (session is IAsyncQueryStorageSession asyncQuery)
            return asyncQuery.QueryAsync(request, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(session.Query(request));
    }

    private static T Materialize<T>(IReadOnlyDictionary<string, object?> row, GwTableModel<T>? model)
    {
        if (typeof(T) == typeof(IReadOnlyDictionary<string, object?>)) return (T)(object)row;
        var value = Activator.CreateInstance<T>();
        var mappings = model?.Columns.Select(column => (Member: column.Key, Column: column.Value.Name))
            ?? typeof(T).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(member => member is System.Reflection.PropertyInfo or System.Reflection.FieldInfo)
                .Select(member => (Member: member.Name, Column: member.Name));
        foreach (var column in mappings)
        {
            if (!row.TryGetValue(column.Column, out var raw)) continue;
            var member = typeof(T).GetMember(column.Member, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).FirstOrDefault(item => item is System.Reflection.PropertyInfo or System.Reflection.FieldInfo);
            switch (member)
            {
                case System.Reflection.PropertyInfo property when property.CanWrite:
                    property.SetValue(value, raw is null ? null : ConvertValue(raw, property.PropertyType));
                    break;
                case System.Reflection.FieldInfo field when !field.IsInitOnly:
                    field.SetValue(value, raw is null ? null : ConvertValue(raw, field.FieldType));
                    break;
            }
        }
        return value;
    }

    private static object? ConvertValue(object value, Type target)
    {
        var core = Nullable.GetUnderlyingType(target) ?? target;
        return core.IsInstanceOfType(value) ? value : Convert.ChangeType(value, core, System.Globalization.CultureInfo.InvariantCulture);
    }
}
