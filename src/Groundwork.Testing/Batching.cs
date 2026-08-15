using System.Runtime.CompilerServices;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Testing;

/// <summary>Capability descriptors exposed by providers that implement staged writes.</summary>
public static class BatchWriteCapabilities
{
    public static CapabilityId StagedUnitOfWork { get; } = new("groundwork.storage.batched-unit-of-work");

    public static CapabilityId PerRowOutcomes { get; } = new("groundwork.storage.batched-outcomes");

    public static CapabilityId NativeBatch { get; } = new("groundwork.storage.batched-native");

    public static CapabilityDescriptor StagedUnitOfWorkDescriptor { get; } = new(
        StagedUnitOfWork,
        "Batched unit of work",
        "Stages row writes, coalesces same-key writes, and flushes grouped writes at commit, staged reads, or the configured row cap; the aggregate or exact outcome path is selected when the unit of work begins.");

    public static CapabilityDescriptor PerRowOutcomesDescriptor { get; } = new(
        PerRowOutcomes,
        "Batched per-row outcomes",
        "Returns one outcome for each staged row through an exact-mode CommitWithOutcomes call; aggregate-mode units reject that API rather than fabricating evidence.");

    public static CapabilityDescriptor NativeBatchDescriptor { get; } = new(
        NativeBatch,
        "Native batched writes",
        "Executes grouped writes through the provider's native multi-row command or bulk-write primitive.");

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
        Array.AsReadOnly(new[] { StagedUnitOfWorkDescriptor, PerRowOutcomesDescriptor });

    public static IReadOnlyList<CapabilityDescriptor> ForProvider(
        string provider,
        bool nativeBatch,
        string exactOutcomeCost,
        string batchCost) =>
        Array.AsReadOnly(
        [
            StagedUnitOfWorkDescriptor with
            {
                Description = $"Stages and coalesces writes for {provider}; {batchCost}."
            },
            PerRowOutcomesDescriptor with
            {
                Description = $"Returns one outcome per staged row for {provider}; exact evidence cost: {exactOutcomeCost}."
            },
            ..(nativeBatch ? [NativeBatchDescriptor] : Array.Empty<CapabilityDescriptor>())
        ]);
}

/// <summary>Determines the provider path selected for a unit of work at begin time.</summary>
public enum BatchOutcomeMode
{
    /// <summary>Prefer the lowest-cost aggregate provider path; exact commit APIs are unavailable.</summary>
    Aggregate,
    /// <summary>Retain exact provider evidence through automatic flushes and commit.</summary>
    Exact
}

/// <summary>The operation applied to one staged row.</summary>
public enum RowWriteMode
{
    Insert,
    Update,
    Upsert,
    /// <summary>Executes the provider's atomic optimistic-concurrency upsert primitive.</summary>
    ConditionalUpsert,
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

    public static RowWrite ConditionalUpsert(StorageUnit unit, StorageValues values, WriteOptions? options = null) =>
        new(unit, RowWriteMode.ConditionalUpsert, values, null, options);

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

    internal string Identity => IdentityFor(Unit, KeyValues);

    internal static string IdentityFor(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> keyValues) => string.Concat(
        unit.Key.Columns.Select(column =>
        {
            var value = Canonical(keyValues[column]);
            return $"{value.Length}:{value}";
        }));

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
        null => "n:",
        string text => $"s:{text}",
        byte[] bytes => $"b:{Convert.ToBase64String(bytes)}",
        DateTimeOffset timestamp => $"d:{timestamp.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
        IFormattable formattable => $"v:{value.GetType().FullName}:{formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? ""}",
        _ => $"o:{value.GetType().FullName}:{value}"
    };
}

/// <summary>Configures staging and provider batch boundaries.</summary>
public sealed record BatchWriteOptions
{
    public int MaxRowsPerFlush { get; init; } = 1_000;

    /// <summary>
    /// Selects the outcome contract before any writes can flush. Aggregate is the low-cost
    /// default; choose Exact explicitly when per-row provider evidence is required.
    /// </summary>
    public BatchOutcomeMode OutcomeMode { get; init; } = BatchOutcomeMode.Aggregate;

    internal void Validate()
    {
        if (MaxRowsPerFlush <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRowsPerFlush));
        if (!Enum.IsDefined(OutcomeMode))
            throw new ArgumentOutOfRangeException(nameof(OutcomeMode));
    }

    public static BatchWriteOptions Default { get; } = new();

    public static BatchWriteOptions Exact { get; } = new() { OutcomeMode = BatchOutcomeMode.Exact };
}

/// <summary>One staged write and its provider outcome.</summary>
public enum RowWriteDisposition
{
    Applied,
    Superseded
}

/// <summary>Provider evidence for one staged input, including explicit coalescing disposition.</summary>
public sealed record RowWriteOutcome(
    RowWrite Write,
    WriteOutcome Outcome,
    RowWriteDisposition Disposition = RowWriteDisposition.Applied,
    int? WinnerOrdinal = null,
    WriteOutcome? WinnerEvidence = null)
{
    public bool IsSuperseded => Disposition == RowWriteDisposition.Superseded;
}

/// <summary>Aggregate and per-row evidence returned by a batched commit.</summary>
public sealed class BatchWriteSummary
{
    public BatchWriteSummary(IReadOnlyList<RowWriteOutcome> outcomes)
    {
        Outcomes = Array.AsReadOnly((outcomes ?? throw new ArgumentNullException(nameof(outcomes))).ToArray());
    }

    public IReadOnlyList<RowWriteOutcome> Outcomes { get; }

    public int Submitted => Outcomes.Count;

    /// <summary>Number of provider-applied writes that succeeded; superseded inputs are not counted.</summary>
    public int Succeeded => Outcomes.Count(item => item.Disposition == RowWriteDisposition.Applied && item.Outcome.Succeeded);

    public int Applied => Outcomes.Count(item => item.Disposition == RowWriteDisposition.Applied);

    public int Superseded => Outcomes.Count(item => item.Disposition == RowWriteDisposition.Superseded);

    public int Failed => Outcomes.Count(item => item.Disposition == RowWriteDisposition.Applied && !item.Outcome.Succeeded);

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

    /// <summary>
    /// Applies a batch while allowing providers to select a more expensive exact-outcome
    /// path for CommitWithOutcomes. Existing providers retain their ordinary path by default.
    /// </summary>
    IReadOnlyList<RowWriteOutcome> ApplyBatch(
        IReadOnlyList<RowWrite> writes,
        bool exactOutcomes) => ApplyBatch(writes);
}

/// <summary>Shared staging, grouping, coalescing, and flush-on-read behavior.</summary>
internal sealed class BatchContext
{
    private readonly BatchWriteOptions options;
    private readonly bool exactOutcomes;
    private readonly List<RowWrite> staged = [];
    private readonly List<RowWriteOutcome> completed = [];
    private readonly Dictionary<RowWrite, int> ordinals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StorageUnitId, IBatchedStorageSession> sessions = [];
    private Exception? failure;
    private int nextOrdinal;

    internal BatchContext(BatchWriteOptions? options)
    {
        this.options = options ?? BatchWriteOptions.Default;
        this.options.Validate();
        exactOutcomes = this.options.OutcomeMode == BatchOutcomeMode.Exact;
    }

    internal int Count => staged.Count;

    internal void Register(IStorageSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not IBatchedStorageSession batched)
            throw new ArgumentException("A batching session must implement IBatchedStorageSession.", nameof(session));
        sessions.TryAdd(session.Unit.Id, batched);
    }

    internal void Stage(RowWrite write)
    {
        EnsureHealthy();
        ArgumentNullException.ThrowIfNull(write);
        staged.Add(write);
        ordinals.Add(write, nextOrdinal++);
    }

    internal IReadOnlyList<RowWriteOutcome> FlushFor(StorageUnit unit, StorageKey key)
    {
        EnsureHealthy();
        var writes = staged.Where(write => write.Unit.Id == unit.Id && write.Matches(key)).ToArray();
        return writes.Length == 0 ? [] : Flush(writes);
    }

    internal IReadOnlyList<RowWriteOutcome> FlushAll()
    {
        EnsureHealthy();
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
        EnsureHealthy();
        try
        {
            // Coalescing is intentionally performed before provider grouping. A key's
            // declaration-order final write wins even when earlier writes used another
            // mode or column set; only the final writes are sent to the provider.
            var coalesced = writes
                .GroupBy(write => (write.Unit.Id, write.Identity))
                .Select(group => new CoalescedWrite(group.ToArray()))
                .ToArray();
            var outcomes = new Dictionary<RowWrite, WriteOutcome>(ReferenceEqualityComparer.Instance);
            foreach (var group in coalesced.GroupBy(item =>
                         (item.Final.Unit.Id, item.Final.Mode, item.Final.ColumnSet)))
            {
                if (!sessions.TryGetValue(group.Key.Id, out var session))
                    throw new InvalidOperationException(
                        $"Storage unit '{group.Key.Id.Value}' has no open session in this unit of work.");

                var finalWrites = group.Select(item => item.Final).ToArray();
                var groupOutcomes = session.ApplyBatch(finalWrites, exactOutcomes);
                if (groupOutcomes.Count != finalWrites.Length)
                    throw new InvalidOperationException(
                        $"The provider returned {groupOutcomes.Count} outcomes for a batch of {finalWrites.Length} writes.");
                foreach (var outcome in groupOutcomes)
                {
                    var item = group.Single(candidate =>
                        ReferenceEquals(candidate.Final, outcome.Write));
                    foreach (var original in item.Originals)
                        outcomes[original] = outcome.Outcome;
                }
            }

            var finalByOriginal = coalesced
                .SelectMany(item => item.Originals.Select(original => (original, item.Final)))
                .ToDictionary(item => item.original, item => item.Final, ReferenceEqualityComparer.Instance);
            var ordered = writes.Select(write =>
            {
                var providerOutcome = outcomes[write];
                var winner = finalByOriginal[write];
                return ReferenceEquals(write, winner)
                    ? new RowWriteOutcome(write, providerOutcome)
                    : new RowWriteOutcome(
                        write,
                        new WriteOutcome(WriteOutcomeStatus.Superseded),
                        RowWriteDisposition.Superseded,
                        ordinals[winner],
                        providerOutcome);
            }).ToArray();
            staged.RemoveAll(write => writes.Contains(write, ReferenceEqualityComparer.Instance));
            foreach (var write in writes)
                ordinals.Remove(write);
            completed.AddRange(ordered);
            if (ordered.Any(item => item.Disposition == RowWriteDisposition.Applied && !item.Outcome.Succeeded))
            {
                var failures = ordered.Where(item => item.Disposition == RowWriteDisposition.Applied && !item.Outcome.Succeeded)
                    .Select(item => $"{item.Write.Unit.Id.Value}/{KeyDescription(item.Write)}: {item.Outcome.Status}");
                throw new BatchWriteException(
                    $"A staged row write failed ({string.Join(", ", failures)}); the unit of work must be rolled back.", ordered);
            }
            return ordered;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
    }

    private void EnsureHealthy()
    {
        if (failure is not null)
            throw new InvalidOperationException(
                "The unit of work contains a failed batch and must be rolled back.", failure);
    }

    internal void RequireExactOutcomes()
    {
        EnsureHealthy();
        if (!exactOutcomes)
            throw new InvalidOperationException(
                "This unit of work selected aggregate outcomes at begin time; start a new unit of work with BatchOutcomeMode.Exact for per-row evidence.");
    }

    private sealed class CoalescedWrite(IReadOnlyList<RowWrite> originals)
    {
        internal IReadOnlyList<RowWrite> Originals { get; } = originals;

        internal RowWrite Final => Originals[^1];
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

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null)
    {
        // A query can observe any key in the unit, so its read barrier is the whole
        // staged set rather than the exact-key barrier used by Read.
        context.FlushAll();
        return inner.Query(request, options);
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
        => ApplyBatch(writes, exactOutcomes: false);

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes, bool exactOutcomes)
    {
        if (inner is IBatchedStorageSession batched)
            return batched.ApplyBatch(writes, exactOutcomes);

        return writes.Select(write => new RowWriteOutcome(write, write.Mode switch
        {
            RowWriteMode.Insert => inner.Insert(write.Values!, write.Options),
            RowWriteMode.Update => inner.Update(write.Values!, write.Options),
            RowWriteMode.Upsert when write.Options.ExpectedVersion is not null => inner is IConcurrencyStorageSession expectedConcurrency
                ? expectedConcurrency.ConditionalUpsert(write.Values!, write.Options)
                : throw new NotSupportedException("The provider session does not support conditional upsert."),
            RowWriteMode.Upsert => inner.Upsert(write.Values!, write.Options),
            RowWriteMode.ConditionalUpsert => inner is IConcurrencyStorageSession concurrency
                ? concurrency.ConditionalUpsert(write.Values!, write.Options)
                : throw new NotSupportedException("The provider session does not support conditional upsert."),
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
