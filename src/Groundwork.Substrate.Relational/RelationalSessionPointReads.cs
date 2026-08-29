using System.Data.Common;
using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Executes and materializes one relational key lookup. The adapter retains only provider-shaped
/// equality, parameter, locking, and value behavior.
/// </summary>
internal sealed class RelationalSessionPointReads
{
    private readonly StorageUnit unit;
    private readonly StorageAccess access;
    private readonly IReadOnlyList<ColumnDefinition> userColumns;
    private readonly ColumnDefinition? versionColumn;
    private readonly Func<string, DbCommand> createCommand;
    private readonly IRelationalPointReadAdapter adapter;
    private readonly IProviderCommandObserver? observer;
    private readonly string operationPrefix;

    internal RelationalSessionPointReads(
        StorageUnit unit,
        StorageAccess access,
        IReadOnlyList<ColumnDefinition> userColumns,
        ColumnDefinition? versionColumn,
        Func<string, DbCommand> createCommand,
        IRelationalPointReadAdapter adapter,
        IProviderCommandObserver? observer,
        string operationPrefix)
    {
        this.unit = unit;
        this.access = access;
        this.userColumns = userColumns;
        this.versionColumn = versionColumn;
        this.createCommand = createCommand;
        this.adapter = adapter;
        this.observer = observer;
        this.operationPrefix = operationPrefix;
    }

    internal async ValueTask<StoredEntry?> Read(
        StorageKey key,
        RelationalExecution execution,
        bool forUpdate = false,
        string? observerOperation = null,
        bool exactStringKeys = false,
        bool isProbe = true)
    {
        ArgumentNullException.ThrowIfNull(key);
        var keyColumns = unit.Key.Columns.ToList();
        if (unit.Columns.Any(column => column.Name == ProviderOwnedColumns.Scope) &&
            !keyColumns.Contains(ProviderOwnedColumns.Scope, StringComparer.Ordinal))
        {
            keyColumns.Add(ProviderOwnedColumns.Scope);
        }
        var clauses = new List<string>(keyColumns.Count);
        using var command = createCommand(string.Empty);
        foreach (var name in keyColumns)
        {
            var column = unit.Columns.First(item => item.Name == name);
            var value = name == ProviderOwnedColumns.Scope
                ? access.Scope!.Value
                : key.Values.TryGetValue(name, out var supplied)
                    ? supplied
                    : throw new ArgumentException($"Key column '{name}' is required.", nameof(key));
            var parameter = name == ProviderOwnedColumns.Scope
                ? "@__groundwork_scope"
                : "@key_" + name;
            clauses.Add(adapter.Equality(column, parameter, exactStringKeys));
            adapter.Bind(command, parameter, value, column);
        }

        var columns = userColumns.Concat(versionColumn is null ? [] : [versionColumn]);
        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(column => adapter.QuoteIdentifier(column.Name)))} " +
            $"FROM {adapter.QuoteIdentifier(unit.Name)} WHERE {string.Join(" AND ", clauses)}" +
            adapter.LockingClause(forUpdate) + ";";
        observer?.Observe(new ProviderCommandEvent(
            observerOperation ?? operationPrefix + ".write-probe",
            command.CommandText,
            ProviderCommandKind.Read,
            IsProbe: isProbe));

        await using var readerScope = await execution.ExecuteReader(command).ConfigureAwait(false);
        var reader = readerScope.Reader;
        if (!await execution.Read(reader).ConfigureAwait(false))
            return null;
        var values = new Dictionary<string, object?>(userColumns.Count, StringComparer.Ordinal);
        for (var index = 0; index < userColumns.Count; index++)
        {
            values[userColumns[index].Name] = adapter.Decode(
                reader.GetValue(index),
                userColumns[index]);
        }
        var version = versionColumn is null
            ? (long?)null
            : Convert.ToInt64(reader.GetValue(userColumns.Count), CultureInfo.InvariantCulture);
        return new StoredEntry(new StorageValues(values), version);
    }

    internal async ValueTask<StoredEntry?> ReadPublic(StorageKey key, RelationalExecution execution)
    {
        StorageAccessValidation.EnsurePointOperation(access, "read");
        return RelationalSessionPolicy.PublicEntry(await Read(
            key,
            execution,
            observerOperation: operationPrefix + ".read",
            isProbe: false).ConfigureAwait(false));
    }
}

internal interface IRelationalPointReadAdapter
{
    string QuoteIdentifier(string identifier);

    string Equality(ColumnDefinition column, string parameter, bool exactStringKeys);

    void Bind(DbCommand command, string parameter, object? value, ColumnDefinition column);

    object? Decode(object value, ColumnDefinition column);

    string LockingClause(bool forUpdate);
}
