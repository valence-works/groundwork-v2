using System.Runtime.CompilerServices;
using Groundwork.Kernel;

namespace Groundwork.Testing;

/// <summary>Capability descriptors exposed by providers that implement staged writes.</summary>
public static class BatchWriteCapabilities
{
    public static CapabilityId StagedUnitOfWork { get; } = new("groundwork.storage.batched-unit-of-work");

    public static CapabilityId PerRowOutcomes { get; } = new("groundwork.storage.batched-outcomes");

    public static CapabilityDescriptor StagedUnitOfWorkDescriptor { get; } = new(
        StagedUnitOfWork,
        "Batched unit of work",
        "Stages row writes, coalesces same-key writes, and flushes grouped writes at commit, staged reads, or the configured row cap.");

    public static CapabilityDescriptor PerRowOutcomesDescriptor { get; } = new(
        PerRowOutcomes,
        "Batched per-row outcomes",
        "Returns one outcome for each staged row through CommitWithOutcomesAsync; providers may use a native returning/output path or a documented fallback.");

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
        Array.AsReadOnly(new[] { StagedUnitOfWorkDescriptor, PerRowOutcomesDescriptor });
}

/// <summary>The operation applied to one staged row.</summary>
public enum RowWriteMode
{
    Insert,
    Update,
    Upsert,
    Delete
}

/// <summary>One provider-neutral row mutation staged in a unit of work.</summary>
public sealed class RowWrite
{
    private RowWrite(
        StorageUnit unit,
        RowWriteMode mode,
        StorageValues? values,
        StorageKey? key,
        WriteOptions? options)
    {
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
        Mode = mode;
        Values = values;
        Key = key;
        Options = options ?? WriteOptions.Unconditional;

        if (mode == RowWriteMode.Delete)
        {
            if (key is null)
                throw new ArgumentNullException(nameof(key), "A delete must provide a key.");
            if (values is not null)
                throw new ArgumentException("A delete cannot provide row values.", nameof(values));
        }
        else if (values is null)
        {
            throw new ArgumentNullException(nameof(values), "A row mutation must provide values.");
        }
        else if (key is not null)
        {
            throw new ArgumentException("Only a delete may provide a separate key.", nameof(key));
        }
    }

    public StorageUnit Unit { get; }

    public RowWriteMode Mode { get; }

    public StorageValues? Values { get; }

    public StorageKey? Key { get; }

    public WriteOptions Options { get; }

    public static RowWrite Insert(StorageUnit unit, StorageValues values, WriteOptions? options = null) =>
        new(unit, RowWriteMode.Insert, values, null, options);

    public static RowWrite Update(StorageUnit unit, StorageValues values, WriteOptions? options = null) =>
        new(unit, RowWriteMode.Update, values, null, options);

    public static RowWrite Upsert(StorageUnit unit, StorageValues values, WriteOptions? options = null) =>
        new(unit, RowWriteMode.Upsert, values, null, options);

    public static RowWrite Delete(StorageUnit unit, StorageKey key, WriteOptions? options = null) =>
        new(unit, RowWriteMode.Delete, null, key, options);

    internal IReadOnlyDictionary<string, object?> KeyValues =>
        Key?.Values ?? Unit.Key.Columns.ToDictionary(
            column => column,
            column => Values!.Values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(Values)),
            StringComparer.Ordinal);

    internal string ColumnSet => Mode == RowWriteMode.Delete
        ? string.Join("\u001f", Unit.Key.Columns)
        : string.Join("\u001f", Values!.Values.Keys.OrderBy(value => value, StringComparer.Ordinal));

    internal string Identity => string.Join(
        "\u001e",
        Unit.Key.Columns.Select(column => Canonical(KeyValues[column])));

    internal bool Matches(StorageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        foreach (var column in Unit.Key.Columns)
        {
            if (!KeyValues.TryGetValue(column, out var left) ||
                !key.Values.TryGetValue(column, out var right) ||
                !EqualValue(left, right))
                return false;
        }
        return true;
    }

    private static bool EqualValue(object? left, object? right) => left switch
    {
        byte[] leftBytes when right is byte[] rightBytes => leftBytes.SequenceEqual(rightBytes),
        _ => Equals(left, right)
    };

    private static string Canonical(object? value) => value switch
    {
        null => "<null>",
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTimeOffset timestamp => timestamp.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ => value.ToString() ?? ""
    };
}

/// <summary>Configures staging and provider batch boundaries.</summary>
public sealed record BatchWriteOptions
{
    public int MaxRowsPerFlush { get; init; } = 1_000;

    internal void Validate()
    {
        if (MaxRowsPerFlush <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRowsPerFlush));
    }

    public static BatchWriteOptions Default { get; } = new();
}

/// <summary>One staged write and its provider outcome.</summary>
public sealed record RowWriteOutcome(RowWrite Write, WriteOutcome Outcome);

/// <summary>Aggregate and per-row evidence returned by a batched commit.</summary>
public sealed class BatchWriteSummary
{
    public BatchWriteSummary(IReadOnlyList<RowWriteOutcome> outcomes)
    {
        Outcomes = Array.AsReadOnly((outcomes ?? throw new ArgumentNullException(nameof(outcomes))).ToArray());
    }

    public IReadOnlyList<RowWriteOutcome> Outcomes { get; }

    public int Submitted => Outcomes.Count;

    public int Succeeded => Outcomes.Count(item => item.Outcome.Succeeded);

    public int Failed => Submitted - Succeeded;

    public bool IsSuccessful => Failed == 0;
}

/// <summary>Raised when a staged batch cannot be committed atomically.</summary>
public sealed class BatchWriteException : InvalidOperationException
{
    public BatchWriteException(string message, IReadOnlyList<RowWriteOutcome> outcomes)
        : base(message)
    {
        Outcomes = Array.AsReadOnly((outcomes ?? throw new ArgumentNullException(nameof(outcomes))).ToArray());
    }

    public IReadOnlyList<RowWriteOutcome> Outcomes { get; }
}

/// <summary>Provider hook for executing one already-grouped batch inside the current transaction.</summary>
public interface IBatchedStorageSession
{
    IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes);
}

/// <summary>Shared staging, grouping, coalescing, and flush-on-read behavior.</summary>
internal sealed class BatchContext
{
    private readonly BatchWriteOptions options;
    private readonly List<RowWrite> staged = [];
    private readonly List<RowWriteOutcome> completed = [];
    private readonly Dictionary<StorageUnitId, BatchStorageSession> sessions = [];

    internal BatchContext(BatchWriteOptions? options)
    {
        this.options = options ?? BatchWriteOptions.Default;
        this.options.Validate();
    }

    internal int Count => staged.Count;

    internal void Register(BatchStorageSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        sessions.TryAdd(session.Unit.Id, session);
    }

    internal void Stage(RowWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        staged.Add(write);
    }

    internal IReadOnlyList<RowWriteOutcome> FlushFor(StorageUnit unit, StorageKey key)
    {
        var writes = staged.Where(write => write.Unit.Id == unit.Id && write.Matches(key)).ToArray();
        return writes.Length == 0 ? [] : Flush(writes);
    }

    internal IReadOnlyList<RowWriteOutcome> FlushAll()
    {
        if (staged.Count == 0)
            return [];
        return Flush(staged.ToArray());
    }

    internal IReadOnlyList<RowWriteOutcome> DrainCompleted()
    {
        var result = completed.ToArray();
        completed.Clear();
        return result;
    }

    internal bool ReachedCap => staged.Count >= options.MaxRowsPerFlush;

    private IReadOnlyList<RowWriteOutcome> Flush(IReadOnlyList<RowWrite> writes)
    {
        var outcomes = new List<RowWriteOutcome>(writes.Count);
        foreach (var group in writes.GroupBy(write =>
                     (write.Unit.Id, write.Mode, write.ColumnSet)))
        {
            if (!sessions.TryGetValue(group.Key.Id, out var session))
                throw new InvalidOperationException(
                    $"Storage unit '{group.Key.Id.Value}' has no open session in this unit of work.");

            var groupWrites = group.ToArray();
            var coalesced = groupWrites
                .GroupBy(write => write.Identity, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
            var groupOutcomes = session.ApplyBatch(coalesced);
            if (groupOutcomes.Count != coalesced.Length)
                throw new InvalidOperationException(
                    $"The provider returned {groupOutcomes.Count} outcomes for a batch of {coalesced.Length} writes.");
            foreach (var outcome in groupOutcomes)
            {
                var original = groupWrites.Where(write => write.Identity == outcome.Write.Identity).ToArray();
                outcomes.AddRange(original.Select(write => new RowWriteOutcome(write, outcome.Outcome)));
            }
        }

        var flushed = writes.ToHashSet(ReferenceEqualityComparer.Instance);
        staged.RemoveAll(write => flushed.Contains(write));
        completed.AddRange(outcomes);
        if (outcomes.Any(item => !item.Outcome.Succeeded))
        {
            var failures = outcomes.Where(item => !item.Outcome.Succeeded)
                .Select(item => $"{item.Write.Unit.Id.Value}/{KeyDescription(item.Write)}: {item.Outcome.Status}");
            throw new BatchWriteException(
                $"A staged row write failed ({string.Join(", ", failures)}); the unit of work must be rolled back.", outcomes);
        }
        return outcomes;
    }

    private static string KeyDescription(RowWrite write)
    {
        var values = write.Key?.Values ?? write.Values!.Values;
        return string.Join(",", write.Unit.Key.Columns.Select(column =>
            $"{column}={values.GetValueOrDefault(column)}"));
    }
}

/// <summary>Testing-layer wrapper that makes staged-key reads flush before delegating.</summary>
internal sealed class BatchStorageSession : IStorageSession, IConcurrencyStorageSession, IBatchedStorageSession
{
    private readonly IStorageSession inner;
    private readonly BatchContext context;

    internal BatchStorageSession(IStorageSession inner, BatchContext context)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        Unit = inner.Unit;
        Access = inner.Access;
    }

    public StorageUnit Unit { get; }

    public StorageAccess Access { get; }

    public StoredEntry? Read(StorageKey key)
    {
        context.FlushFor(Unit, key);
        return inner.Read(key);
    }

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) => inner.Insert(values, options);

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null) => inner.Update(values, options);

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) => inner.Upsert(values, options);

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) => inner.Delete(key, options);

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null) =>
        inner is IConcurrencyStorageSession concurrency
            ? concurrency.ConditionalUpsert(values, options)
            : throw new NotSupportedException("The provider session does not support conditional upsert.");

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes)
    {
        if (inner is IBatchedStorageSession batched)
            return batched.ApplyBatch(writes);

        return writes.Select(write => new RowWriteOutcome(write, write.Mode switch
        {
            RowWriteMode.Insert => inner.Insert(write.Values!, write.Options),
            RowWriteMode.Update => inner.Update(write.Values!, write.Options),
            RowWriteMode.Upsert => inner.Upsert(write.Values!, write.Options),
            RowWriteMode.Delete => inner.Delete(write.Key!, write.Options),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        })).ToArray();
    }
}

internal sealed class ReferenceEqualityComparer : IEqualityComparer<RowWrite>
{
    internal static ReferenceEqualityComparer Instance { get; } = new();

    public bool Equals(RowWrite? x, RowWrite? y) => ReferenceEquals(x, y);

    public int GetHashCode(RowWrite obj) => RuntimeHelpers.GetHashCode(obj);
}
