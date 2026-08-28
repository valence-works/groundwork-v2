using System.Collections.ObjectModel;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Kernel;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Records;

/// <summary>
/// Provider-neutral values for one record. Values are snapshotted at the boundary, so a
/// provider adapter cannot observe later mutation of the caller's object graph.
/// </summary>
public sealed class RowValues
{
    public RowValues(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = new ReadOnlyDictionary<string, object?>(
            values.ToDictionary(pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal));
    }

    public IReadOnlyDictionary<string, object?> Values { get; }

    public object? this[string name] => Values[name];

    public bool TryGetValue(string name, out object? value) => Values.TryGetValue(name, out value);

    public bool Contains(string name) => Values.ContainsKey(name);

    public static RowValues Empty { get; } = new(new Dictionary<string, object?>(StringComparer.Ordinal));

    internal static object? Clone(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case byte[] bytes:
                return bytes.ToArray();
            case JsonNode node:
                return node.DeepClone();
            case JsonElement element:
                return element.Clone();
            case JsonDocument document:
                return document.RootElement.Clone();
            case IReadOnlyDictionary<string, object?> nested:
                return new ReadOnlyDictionary<string, object?>(nested.ToDictionary(
                    pair => pair.Key, pair => Clone(pair.Value), StringComparer.Ordinal));
            case IDictionary dictionary:
            {
                var copy = new Dictionary<object, object?>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is null)
                        throw new ArgumentException("Row dictionaries cannot contain a null key.");
                    copy[entry.Key] = Clone(entry.Value);
                }

                return new ReadOnlyDictionary<object, object?>(copy);
            }
            case IEnumerable sequence when value is not string:
                return Array.AsReadOnly(sequence.Cast<object?>().Select(Clone).ToArray());
            default:
                if (value.GetType().IsValueType || value is string)
                    return value;
                throw new ArgumentException(
                    $"Cannot snapshot mutable value of type '{value.GetType().FullName}'.");
        }
    }
}

/// <summary>Outcome statuses shared by provider adapters without exposing provider types.</summary>
public enum RecordWriteStatus
{
    Inserted,
    Updated,
    Upserted,
    Deleted,
    NotFound,
    UniqueViolation,
    ConcurrencyConflict
}

/// <summary>A provider-neutral optimistic-concurrency precondition.</summary>
public sealed record RecordWriteOptions
{
    public long? ExpectedVersion { get; init; }

    public static RecordWriteOptions Unconditional { get; } = new();

    public static RecordWriteOptions IfVersion(long expectedVersion)
    {
        if (expectedVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        return new() { ExpectedVersion = expectedVersion };
    }
}

/// <summary>The provider-neutral result of a typed record mutation.</summary>
public sealed record RecordWriteResult(
    RecordWriteStatus Status,
    long? Version = null,
    IReadOnlyDictionary<string, object?>? GeneratedValues = null,
    string? UniqueIndexName = null)
{
    public bool Succeeded => Status is
        RecordWriteStatus.Inserted or
        RecordWriteStatus.Updated or
        RecordWriteStatus.Upserted or
        RecordWriteStatus.Deleted;
}

/// <summary>Provider-neutral rows returned by a typed query.</summary>
public sealed class RecordQueryResult
{
    public RecordQueryResult(IReadOnlyList<RowValues> rows, long? totalCount = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Rows = Array.AsReadOnly(rows.ToArray());
        TotalCount = totalCount;
    }

    public IReadOnlyList<RowValues> Rows { get; }

    public long? TotalCount { get; }
}

/// <summary>
/// Provider-neutral execution seam for a record contract. Implementations belong beside the
/// provider-facing adapter; Groundwork.Records never references a provider assembly.
/// </summary>
public interface IRecordStore
{
    RecordWriteResult Insert(KernelStorageUnit unit, RowValues values, RecordWriteOptions? options = null);

    RecordWriteResult Update(KernelStorageUnit unit, RowValues values, RecordWriteOptions? options = null);

    RecordWriteResult Upsert(KernelStorageUnit unit, RowValues values, RecordWriteOptions? options = null);

    RecordWriteResult Delete(KernelStorageUnit unit, RowValues key, RecordWriteOptions? options = null);

    RecordQueryResult Query(
        Groundwork.Query.Model.QueryRequest request,
        Groundwork.Query.Model.QueryRenderOptions? options = null);
}

/// <summary>
/// Optional provider-neutral aggregation capability for a typed Records adapter.
/// </summary>
/// <remarks>
/// This capability is separate from <see cref="IRecordStore"/> so existing custom record stores
/// remain source-compatible. The shipped Records.Store adapter implements it by forwarding to the
/// existing <see cref="IStorageSession.Aggregate"/> contract; it does not implement provider logic.
/// </remarks>
public interface IRecordAggregationStore
{
    AggregationResult Aggregate(KernelStorageUnit unit, AggregationQuery query);

    ValueTask<AggregationResult> AggregateAsync(
        KernelStorageUnit unit,
        AggregationQuery query,
        CancellationToken cancellationToken = default);
}
