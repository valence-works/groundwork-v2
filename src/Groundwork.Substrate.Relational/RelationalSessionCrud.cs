using Groundwork.Kernel;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Owns provider-neutral point-mutation preparation, optimistic classification, and dispatch.
/// Provider adapters retain SQL generation, typed binding, generated values, and error mapping.
/// </summary>
internal sealed class RelationalSessionCrud
{
    private readonly StorageUnit unit;
    private readonly IReadOnlyList<ColumnDefinition> userColumns;
    private readonly ColumnDefinition? sequenceColumn;
    private readonly ColumnDefinition? versionColumn;
    private readonly string providerName;
    private readonly Func<StorageKey, RelationalExecution, ValueTask<StoredEntry?>> read;
    private readonly IRelationalCrudAdapter adapter;

    internal RelationalSessionCrud(
        StorageUnit unit,
        IReadOnlyList<ColumnDefinition> userColumns,
        ColumnDefinition? sequenceColumn,
        ColumnDefinition? versionColumn,
        string providerName,
        Func<StorageKey, RelationalExecution, ValueTask<StoredEntry?>> read,
        IRelationalCrudAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(userColumns);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(adapter);
        this.unit = unit;
        this.userColumns = userColumns;
        this.sequenceColumn = sequenceColumn;
        this.versionColumn = versionColumn;
        this.providerName = providerName;
        this.read = read;
        this.adapter = adapter;
    }

    internal RelationalCrudMutation PrepareMutation(
        StorageValues values,
        WriteOptions? options,
        RelationalCrudKind kind)
    {
        ArgumentNullException.ThrowIfNull(values);
        var writeOperation = kind switch
        {
            RelationalCrudKind.Insert => WriteOperation.Insert,
            RelationalCrudKind.Update => WriteOperation.Update,
            RelationalCrudKind.Upsert => WriteOperation.Upsert,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        WritePreconditionValidator.ValidateWrittenValues(unit, values.Values);
        WritePreconditionValidator.Validate(unit, writeOperation, options);
        return new RelationalCrudMutation(kind, new StorageValues(values.Values), options);
    }

    internal RelationalCrudDelete PrepareDelete(StorageKey key, WriteOptions? options)
    {
        ArgumentNullException.ThrowIfNull(key);
        WritePreconditionValidator.Validate(unit, WriteOperation.Delete, options);
        return new RelationalCrudDelete(new StorageKey(key.Values), options);
    }

    internal async ValueTask<WriteOutcome> Mutate(
        RelationalCrudMutation operation,
        RelationalExecution execution)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var values = new StorageValues(SearchKeyProjection.Populate(unit, operation.Values.Values));
        RelationalSessionPolicy.ValidateValues(
            unit,
            userColumns,
            providerName,
            values.Values,
            requireAllNonNullable: operation.Kind == RelationalCrudKind.Insert,
            allowGeneratedLocator: operation.Kind is RelationalCrudKind.Update or RelationalCrudKind.Upsert);

        if (sequenceColumn is not null &&
            operation.Kind is RelationalCrudKind.Insert or RelationalCrudKind.Upsert &&
            !values.Values.ContainsKey(sequenceColumn.Name))
        {
            ValidateExpected(operation.Options, existing: null, operation.Kind);
            return await adapter.Insert(
                values,
                operation.Kind == RelationalCrudKind.Upsert
                    ? WriteOutcomeStatus.Upserted
                    : WriteOutcomeStatus.Inserted,
                execution).ConfigureAwait(false);
        }

        var key = KeyFromValues(values.Values);
        var existing = unit.Concurrency.IsNone
            ? null
            : await read(key, execution).ConfigureAwait(false);
        if (operation.Kind == RelationalCrudKind.Insert && existing is not null)
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing.Version);
        if (operation.Kind == RelationalCrudKind.Update && existing is null && unit.Concurrency.IsOptimistic)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        if (operation.Kind == RelationalCrudKind.Upsert && sequenceColumn is not null &&
            values.Values.ContainsKey(sequenceColumn.Name) && existing is null && unit.Concurrency.IsOptimistic)
        {
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        }

        ValidateExpected(operation.Options, existing, operation.Kind);
        return operation.Kind switch
        {
            RelationalCrudKind.Insert => await adapter.Insert(
                values,
                WriteOutcomeStatus.Inserted,
                execution).ConfigureAwait(false),
            RelationalCrudKind.Update => await adapter.Update(
                values,
                key,
                existing,
                operation.Options,
                execution).ConfigureAwait(false),
            RelationalCrudKind.Upsert => await adapter.Upsert(
                values,
                key,
                existing,
                operation.Options,
                execution).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, null)
        };
    }

    internal async ValueTask<WriteOutcome> Delete(
        RelationalCrudDelete operation,
        RelationalExecution execution)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (unit.Concurrency.IsNone)
            return await adapter.Delete(operation.Key, null, operation.Options, execution).ConfigureAwait(false);

        var existing = await read(operation.Key, execution).ConfigureAwait(false);
        ValidateExpected(operation.Options, existing, RelationalCrudKind.Delete);
        if (existing is null)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        return await adapter.Delete(
            operation.Key,
            existing,
            operation.Options,
            execution).ConfigureAwait(false);
    }

    private StorageKey KeyFromValues(IReadOnlyDictionary<string, object?> values) => new(
        unit.Key.Columns
            .Where(column => column != ProviderOwnedColumns.Scope)
            .ToDictionary(
                column => column,
                column => values.TryGetValue(column, out var value)
                    ? value
                    : throw new ArgumentException($"Key column '{column}' is required.", nameof(values)),
                StringComparer.Ordinal));

    private void ValidateExpected(
        WriteOptions? options,
        StoredEntry? existing,
        RelationalCrudKind kind)
    {
        if (versionColumn is null || kind == RelationalCrudKind.Insert)
            return;
        var precondition = options?.Precondition ?? WritePrecondition.Unconditional;
        if (kind == RelationalCrudKind.Upsert)
        {
            if (precondition.Kind == WritePreconditionKind.CreateOnly && existing is not null)
                throw new RelationalConcurrencyConflictException(existing.Version);
            if (precondition.Kind == WritePreconditionKind.IfVersion &&
                (existing is null || precondition.Version != existing.Version))
            {
                throw new RelationalConcurrencyConflictException(existing?.Version);
            }
            return;
        }

        if (precondition.Kind == WritePreconditionKind.IfVersion &&
            (existing is null || precondition.Version != existing.Version))
        {
            throw new RelationalConcurrencyConflictException(existing?.Version);
        }
    }
}

internal enum RelationalCrudKind
{
    Insert,
    Update,
    Upsert,
    Delete
}

internal sealed record RelationalCrudMutation(
    RelationalCrudKind Kind,
    StorageValues Values,
    WriteOptions? Options);

internal sealed record RelationalCrudDelete(StorageKey Key, WriteOptions? Options);

internal interface IRelationalCrudAdapter
{
    ValueTask<WriteOutcome> Insert(
        StorageValues values,
        WriteOutcomeStatus status,
        RelationalExecution execution);

    ValueTask<WriteOutcome> Update(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution);

    ValueTask<WriteOutcome> Upsert(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution);

    ValueTask<WriteOutcome> Delete(
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution);
}
