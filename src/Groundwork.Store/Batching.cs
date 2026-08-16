using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>Capability descriptors exposed by providers that implement staged writes.</summary>
public static class BatchWriteCapabilities
{
    public static CapabilityId ProviderSequence { get; } = new("groundwork.column.provider-sequence");

    public static CapabilityDescriptor ProviderSequenceDescriptor { get; } = new(
        ProviderSequence,
        "Provider-assigned monotonic sequence",
        "Relational providers monotonically allocate a durable Int64 key in the insert command; concurrent commit order may differ and no additional provider command is required.",
        AdditionalProviderCommandsPerWrite: 0);

    public static CapabilityId StagedUnitOfWork { get; } = new("groundwork.storage.batched-unit-of-work");

    public static CapabilityId PerRowOutcomes { get; } = new("groundwork.storage.batched-outcomes");

    public static CapabilityId NativeBatch { get; } = new("groundwork.storage.batched-native");

    public static CapabilityId AppendIdempotency { get; } = new("groundwork.storage.append-idempotency");

    public static CapabilityId ExactAppendOutcomes { get; } = new("groundwork.storage.exact-append-outcomes");

    public static CapabilityId DurableHighWaterInspection { get; } = new("groundwork.storage.durable-high-water-inspection");

    public static CapabilityId ExactRetention { get; } = new("groundwork.storage.exact-retention");

    public static CapabilityId CompareAndDelete { get; } = new("groundwork.storage.compare-and-delete");

    public static CapabilityDescriptor StagedUnitOfWorkDescriptor { get; } = new(
        StagedUnitOfWork,
        "Batched unit of work",
        "Stages row writes, coalesces same-key writes, and flushes grouped writes at commit, staged reads, or the configured row cap; the aggregate or exact outcome path is selected when the unit of work begins.");

    public static CapabilityDescriptor PerRowOutcomesDescriptor { get; } = new(
        PerRowOutcomes,
        "Batched per-row outcomes",
        "Returns a BatchWriteReport with one outcome for each staged row through an exact-mode CommitWithOutcomes call; aggregate commits return counts only.");

    public static CapabilityDescriptor NativeBatchDescriptor { get; } = new(
        NativeBatch,
        "Native batched writes",
        "Executes grouped writes through the provider's native multi-row command or bulk-write primitive.");

    public static CapabilityDescriptor AppendIdempotencyDescriptor { get; } = new(
        AppendIdempotency,
        "Idempotent append",
        "Records caller-supplied append operation nonces in a kernel-owned durable ledger and commits the ledger entry with the payload atomically.");

    public static CapabilityDescriptor ExactAppendOutcomesDescriptor { get; } = new(
        ExactAppendOutcomes,
        "Replay-stable exact append outcomes",
        "Returns ordered per-row generated values and persists the canonical payload fingerprint plus exact result for replay.");

    public static CapabilityDescriptor DurableHighWaterInspectionDescriptor { get; } = new(
        DurableHighWaterInspection,
        "Durable scoped sequence high-water inspection",
        "Returns the committed ProviderSequence high-water for the current scope, including rows later removed by retention, across provider/session restart.");

    public static CapabilityDescriptor ExactRetentionDescriptor { get; } = new(
        ExactRetention,
        "Replay-stable exact retention",
        "Records a caller operation identity and immutable retention result so acknowledgement-loss retries replay without deleting again.");

    public static CapabilityDescriptor CompareAndDeleteDescriptor { get; } = new(
        CompareAndDelete,
        "Atomic compare-and-delete",
        "Deletes one identified row only when every declared equality value matches in the provider-owned atomic decision; row absence and comparison mismatch remain distinct.");

    public static CapabilityDescriptor AtomicCommitDescriptor { get; } = new(
        WellKnownCapabilities.AtomicCommit,
        "Atomic commit",
        "Commits all staged writes across the declared storage units in one provider transaction.",
        EvidenceGatedByDefault: true);

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
        Array.AsReadOnly(new[] { StagedUnitOfWorkDescriptor, PerRowOutcomesDescriptor, ProviderSequenceDescriptor, AppendIdempotencyDescriptor, ExactAppendOutcomesDescriptor, DurableHighWaterInspectionDescriptor, ExactRetentionDescriptor, CompareAndDeleteDescriptor });

    public static IReadOnlyList<CapabilityDescriptor> ForProvider(
        string provider,
        bool nativeBatch,
        string exactOutcomeCost,
        string batchCost) => ForProvider(
            provider,
            nativeBatch,
            exactOutcomeCost,
            batchCost,
            exactAppendOutcomes: false);

    public static IReadOnlyList<CapabilityDescriptor> ForProvider(
        string provider,
        bool nativeBatch,
        string exactOutcomeCost,
        string batchCost,
        bool exactAppendOutcomes)
        => ForProvider(provider, nativeBatch, exactOutcomeCost, batchCost, exactAppendOutcomes, durableHighWaterInspection: false, exactRetention: false);

    public static IReadOnlyList<CapabilityDescriptor> ForProvider(
        string provider,
        bool nativeBatch,
        string exactOutcomeCost,
        string batchCost,
        bool exactAppendOutcomes,
        bool durableHighWaterInspection,
        bool exactRetention,
        bool atomicCommit = false,
        bool compareAndDelete = false)
    {
        var descriptors = new List<CapabilityDescriptor>
        {
            ProviderSequenceDescriptor with
            {
                Description = $"{provider} monotonically allocates a durable Int64 key in the insert command; concurrent commit order may differ and no additional provider command is required."
            },
            StagedUnitOfWorkDescriptor with
            {
                Description = $"Stages and coalesces writes for {provider}; {batchCost}."
            },
            PerRowOutcomesDescriptor with
            {
                Description = $"Returns one outcome per staged row for {provider}; exact evidence cost: {exactOutcomeCost}."
            },
            AppendIdempotencyDescriptor with
            {
                Description = $"Provides durable idempotent appends on {provider}; ledger and payload are committed atomically."
            },
        };
        if (exactAppendOutcomes)
            descriptors.Add(ExactAppendOutcomesDescriptor with
            {
                Description = $"Returns replay-stable exact generated outcomes on {provider}; result evidence is stored in the idempotency ledger."
            });
        if (durableHighWaterInspection)
            descriptors.Add(DurableHighWaterInspectionDescriptor with
            {
                Description = $"Returns the committed scoped ProviderSequence high-water on {provider}; the value survives retention and provider/session restart."
            });
        if (exactRetention)
            descriptors.Add(ExactRetentionDescriptor with
            {
                Description = $"Replays operation-identified retention results on {provider}; the operation ledger and cleanup are durable."
            });
        if (atomicCommit)
            descriptors.Add(AtomicCommitDescriptor with
            {
                Description = $"Commits all staged writes across declared {provider} storage units in one provider transaction."
            });
        if (compareAndDelete)
            descriptors.Add(CompareAndDeleteDescriptor with
            {
                Description = $"Deletes one identified row on {provider} only when every declared equality value matches in one provider-owned atomic decision/transaction; zero-row outcomes distinguish absence from comparison mismatch."
            });
        if (nativeBatch)
            descriptors.Add(NativeBatchDescriptor);
        return Array.AsReadOnly(descriptors.ToArray());
    }
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
    Delete,
    /// <summary>Deletes one row only when its declared equality values still match.</summary>
    CompareAndDelete
}

/// <summary>One provider-neutral row mutation staged in a unit of work.</summary>
public sealed class RowWrite
{
    private readonly object generatedKeyOccurrence = new();

    private RowWrite(
        StorageUnit unit,
        RowWriteMode mode,
        StorageValues? values,
        StorageKey? key,
        WriteOptions? options,
        IReadOnlyDictionary<string, object?>? expectedValues = null)
    {
        Unit = unit ?? throw new ArgumentNullException(nameof(unit));
        Mode = mode;
        Values = values;
        Key = key;
        Options = options ?? WriteOptions.Unconditional;
        ExpectedValues = mode == RowWriteMode.CompareAndDelete
            ? CompareAndDeleteValidation.Validate(
                unit,
                key!,
                expectedValues ?? throw new ArgumentNullException(nameof(expectedValues)),
                Options)
            : expectedValues is null
                ? ImmutableExpectedValues.Empty
                : new StorageValues(expectedValues).Values;

        WritePreconditionValidator.Validate(
            unit,
            mode switch
            {
                RowWriteMode.Insert => WriteOperation.Insert,
                RowWriteMode.Update => WriteOperation.Update,
                RowWriteMode.Upsert => WriteOperation.Upsert,
                RowWriteMode.ConditionalUpsert => WriteOperation.ConditionalUpsert,
                RowWriteMode.Delete => WriteOperation.Delete,
                RowWriteMode.CompareAndDelete => WriteOperation.CompareAndDelete,
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            },
            Options);

        if (mode is RowWriteMode.Delete or RowWriteMode.CompareAndDelete)
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

        if (values is not null)
            WritePreconditionValidator.ValidateSystemOwnedValues(unit, values.Values);
    }

    public StorageUnit Unit { get; }

    public RowWriteMode Mode { get; }

    public StorageValues? Values { get; }

    public StorageKey? Key { get; }

    public WriteOptions Options { get; }

    /// <summary>Immutable equality values required by a compare-and-delete write.</summary>
    public IReadOnlyDictionary<string, object?> ExpectedValues { get; }

    /// <summary>Internal compare-and-delete correlation fingerprint.</summary>
    internal string Fingerprint
    {
        get
        {
            if (Mode != RowWriteMode.CompareAndDelete)
                return string.Empty;

            var values = Key?.Values ?? Values?.Values ?? ImmutableExpectedValues.Empty;
            var key = new StorageKey(Unit.Key.Columns
                .Where(column => !CompareAndDeleteValidation.IsProviderOwnedColumn(column))
                .ToDictionary(column => column, column => values.GetValueOrDefault(column), StringComparer.Ordinal));
            return RowWriteFingerprint.Create(Unit, Mode, key, ExpectedValues, Options);
        }
    }

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

    public static RowWrite CompareAndDelete(
        StorageUnit unit,
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null) =>
        new(unit, RowWriteMode.CompareAndDelete, null, key, options, expectedValues);

    internal IReadOnlyDictionary<string, object?> KeyValues =>
        Key?.Values ?? Unit.Key.Columns.ToDictionary(
            column => column,
            column => Values!.Values.TryGetValue(column, out var value)
                ? value
                : throw new ArgumentException($"Key column '{column}' is required.", nameof(Values)),
            StringComparer.Ordinal);

    internal string ColumnSet => Mode is RowWriteMode.Delete or RowWriteMode.CompareAndDelete
        ? string.Join("\u001f", Unit.Key.Columns)
        : string.Join("\u001f", Values!.Values.Keys.OrderBy(value => value, StringComparer.Ordinal));

    internal object CoalescingIdentity
    {
        get
        {
            if (HasUnassignedProviderSequenceKey)
            {
                // A generated key cannot identify an uncommitted insert. A private object
                // token gives the declaration collision-free reference identity until the
                // provider returns its real key.
                return generatedKeyOccurrence;
            }

            return Identity;
        }
    }

    internal bool HasUnassignedProviderSequenceKey
    {
        get
        {
            var values = Key?.Values ?? Values!.Values;
            return Unit.Key.Columns.Any(column =>
                Unit.Columns.FirstOrDefault(definition => definition.Name == column)?.Generation == ColumnGeneration.ProviderSequence &&
                !values.ContainsKey(column));
        }
    }

    internal string Identity => IdentityFor(Unit, Key?.Values ?? Values!.Values);

    internal RowWrite PopulateSearchKeyValues()
    {
        if (Values is null)
            return this;
        var values = SearchKeyProjection.Populate(Unit, Values.Values);
        return Mode switch
        {
            RowWriteMode.Insert => Insert(Unit, new StorageValues(values), Options),
            RowWriteMode.Update => Update(Unit, new StorageValues(values), Options),
            RowWriteMode.Upsert => Upsert(Unit, new StorageValues(values), Options),
            RowWriteMode.ConditionalUpsert => ConditionalUpsert(Unit, new StorageValues(values), Options),
            _ => this
        };
    }

    internal static string IdentityFor(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> keyValues) => string.Concat(
        unit.Key.Columns.Select(column =>
        {
            var value = Canonical(keyValues[column]);
            return $"{value.Length}:{value}";
        }));

    internal static string IdentityForAvailableKeys(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> values) => string.Concat(
        unit.Key.Columns
            .Where(values.ContainsKey)
            .Select(column =>
            {
                var value = Canonical(values[column]);
                return $"{column.Length}:{column}{value.Length}:{value}";
            }));

    internal bool Matches(StorageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (HasUnassignedProviderSequenceKey)
            return false;
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

/// <summary>Counts returned by an aggregate batched commit; no per-row evidence is exposed.</summary>
public sealed class BatchWriteSummary
{
    public BatchWriteSummary(int submitted, int applied, int succeeded, int failed, int superseded)
    {
        if (submitted < 0) throw new ArgumentOutOfRangeException(nameof(submitted));
        if (applied < 0) throw new ArgumentOutOfRangeException(nameof(applied));
        if (succeeded < 0) throw new ArgumentOutOfRangeException(nameof(succeeded));
        if (failed < 0) throw new ArgumentOutOfRangeException(nameof(failed));
        if (superseded < 0) throw new ArgumentOutOfRangeException(nameof(superseded));
        if (submitted != applied + superseded)
            throw new ArgumentException("Submitted must equal applied plus superseded.", nameof(submitted));
        if (applied != succeeded + failed)
            throw new ArgumentException("Applied must equal succeeded plus failed.", nameof(applied));

        Submitted = submitted;
        Applied = applied;
        Succeeded = succeeded;
        Failed = failed;
        Superseded = superseded;
    }

    public int Submitted { get; }

    public int Succeeded { get; }

    public int Applied { get; }

    public int Superseded { get; }

    public int Failed { get; }

    public bool IsSuccessful => Failed == 0;

    internal static BatchWriteSummary FromOutcomes(IReadOnlyList<RowWriteOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var applied = outcomes.Count(item => item.Disposition == RowWriteDisposition.Applied);
        var succeeded = outcomes.Count(item =>
            item.Disposition == RowWriteDisposition.Applied && item.Outcome.Succeeded);
        return new BatchWriteSummary(
            outcomes.Count,
            applied,
            succeeded,
            applied - succeeded,
            outcomes.Count - applied);
    }
}

/// <summary>Exact per-row evidence returned by an exact-mode batched commit.</summary>
public sealed class BatchWriteReport
{
    public BatchWriteReport(IReadOnlyList<RowWriteOutcome> outcomes)
    {
        Outcomes = Array.AsReadOnly((outcomes ?? throw new ArgumentNullException(nameof(outcomes))).ToArray());
        Summary = BatchWriteSummary.FromOutcomes(Outcomes);
    }

    public IReadOnlyList<RowWriteOutcome> Outcomes { get; }

    public BatchWriteSummary Summary { get; }

    public int Submitted => Summary.Submitted;

    public int Succeeded => Summary.Succeeded;

    public int Applied => Summary.Applied;

    public int Superseded => Summary.Superseded;

    public int Failed => Summary.Failed;

    public bool IsSuccessful => Summary.IsSuccessful;
}

/// <summary>
/// Raised when modeled row failures prevent an atomic commit. Outcomes contains only
/// attributed applied failures; aggregate synthetic successes are never included.
/// </summary>
public sealed class BatchWriteException : InvalidOperationException
{
    public BatchWriteException(string message, IReadOnlyList<RowWriteOutcome> outcomes)
        : base(message)
    {
        var attributedFailures = (outcomes ?? throw new ArgumentNullException(nameof(outcomes))).ToArray();
        if (attributedFailures.Any(outcome =>
                outcome.Disposition != RowWriteDisposition.Applied || outcome.Outcome.Succeeded))
        {
            throw new ArgumentException(
                "Batch write exceptions may contain only attributed applied failures.",
                nameof(outcomes));
        }
        Outcomes = Array.AsReadOnly(attributedFailures);
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
    private readonly HashSet<RowWrite> declarations = new(ReferenceEqualityComparer.Instance);
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
        if (!declarations.Add(write))
            throw new ArgumentException(
                "The same RowWrite instance cannot be staged more than once; create a new RowWrite declaration for each position.",
                nameof(write));
        staged.Add(write);
        ordinals.Add(write, nextOrdinal++);
    }

    internal IReadOnlyList<RowWriteOutcome> FlushFor(StorageUnit unit, StorageKey key)
    {
        EnsureHealthy();
        var writes = staged.Where(write =>
            write.Unit.Id == unit.Id &&
            (write.HasUnassignedProviderSequenceKey || write.Matches(key))).ToArray();
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
                .GroupBy(write => (write.Unit.Id, write.CoalescingIdentity))
                .Select(group => new CoalescedWrite(group.ToArray()))
                .ToArray();
            var outcomes = new Dictionary<RowWrite, WriteOutcome>(ReferenceEqualityComparer.Instance);
            foreach (var group in coalesced.GroupBy(item =>
                         (item.Final.Unit.Id, item.Final.Mode, item.Final.ColumnSet)))
            {
                if (!sessions.TryGetValue(group.Key.Id, out var session))
                    throw new InvalidOperationException(
                        $"Storage unit '{group.Key.Id.Value}' has no open session in this unit of work.");

                var groupItems = group.ToArray();
                var finalWrites = groupItems.Select(item => item.Final).ToArray();
                var groupOutcomes = session.ApplyBatch(finalWrites, exactOutcomes);
                if (groupOutcomes.Count != finalWrites.Length)
                    throw new InvalidOperationException(
                        $"The provider returned {groupOutcomes.Count} outcomes for a batch of {finalWrites.Length} writes.");
                for (var index = 0; index < groupOutcomes.Count; index++)
                {
                    // ApplyBatch outcomes are positionally aligned with finalWrites. Ordinal
                    // correlation remains stable when an adapter physicalizes a RowWrite and
                    // when a generated key has not yet been assigned to the declaration.
                    var item = groupItems[index];
                    var outcome = groupOutcomes[index];
                    if (!string.Equals(outcome.Write.Fingerprint, finalWrites[index].Fingerprint, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"GW-BATCH-FINGERPRINT-001: provider outcome {index} does not identify the submitted row write.");
                    }
                    foreach (var original in item.Originals)
                        outcomes[original] = outcome.Outcome;
                }
            }

            var finalByOriginal = coalesced
                .SelectMany(item => item.Originals.Select(original => (original, item.Final)))
                .ToDictionary(
                    item => item.original,
                    item => item.Final,
                    (IEqualityComparer<RowWrite>)ReferenceEqualityComparer.Instance);
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
            var failedOutcomes = ordered
                .Where(item => item.Disposition == RowWriteDisposition.Applied && !item.Outcome.Succeeded)
                .ToArray();
            if (failedOutcomes.Length != 0)
            {
                var failures = failedOutcomes
                    .Select(item => $"{item.Write.Unit.Id.Value}/{KeyDescription(item.Write)}: {item.Outcome.Status}");
                throw new BatchWriteException(
                    $"A staged row write failed ({string.Join(", ", failures)}); the unit of work must be rolled back.", failedOutcomes);
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

/// <summary>Runtime wrapper that makes staged-key reads flush before delegating.</summary>
internal class BatchStorageSession : IStorageSession, IExactAppendStorageSession, IConcurrencyStorageSession, IBatchedStorageSession, IRetentionStorageSession, IStorageInspectionSession, IExactRetentionStorageSession
{
    protected readonly IStorageSession inner;
    protected readonly BatchContext context;

    internal static BatchStorageSession Create(IStorageSession inner, BatchContext context) =>
        inner is ICompareAndDeleteStorageSession
            ? new BatchCompareAndDeleteStorageSession(inner, context)
            : new BatchStorageSession(inner, context);

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

    public AggregationResult Aggregate(AggregationQuery query)
    {
        context.FlushAll();
        return inner.Aggregate(query);
    }

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) => inner.Insert(values, options);

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null) => inner.Update(values, options);

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) => inner.Upsert(values, options);

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) => inner.Delete(key, options);

    public RetentionResult ApplyRetention(RetentionExecutionOptions? options = null)
    {
        context.FlushAll();
        return inner is IRetentionStorageSession native
            ? native.ApplyRetention(options)
            : RetentionSessionExtensions.ApplyRetention(inner, options);
    }

    public StorageInspection Inspect()
    {
        StorageInspectionSessionExtensions.EnsureProviderSequence(Unit);
        context.FlushAll();
        return inner is IStorageInspectionSession inspection
            ? inspection.Inspect()
            : throw new NotSupportedException("GW-INSPECT-001: this provider session does not advertise durable high-water inspection.");
    }

    public RetentionOperationResult ApplyRetention(OperationId operationId, RetentionExecutionOptions? options = null)
    {
        context.FlushAll();
        return inner is IExactRetentionStorageSession exact
            ? exact.ApplyRetention(operationId, options)
            : throw new NotSupportedException("GW-RETENTION-003: this provider session does not advertise exact retention operations.");
    }

    public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values) =>
        inner.Append(operationId, values);

    public AppendOutcomeReport AppendWithOutcomes(OperationId operationId, IReadOnlyList<StorageValues> values) =>
        inner is IExactAppendStorageSession exact
            ? exact.AppendWithOutcomes(operationId, values)
            : throw new NotSupportedException("GW-APPEND-003: this provider session does not advertise exact append outcomes.");

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null) =>
        inner is IConcurrencyStorageSession concurrency
            ? concurrency.ConditionalUpsert(values, options)
            : throw new NotSupportedException("The provider session does not support conditional upsert.");

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes)
        => ApplyBatch(writes, exactOutcomes: false);

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes, bool exactOutcomes)
    {
        var outcomes = inner is IBatchedStorageSession batched
            ? batched.ApplyBatch(writes, exactOutcomes)
            : writes.Select(write => new RowWriteOutcome(write, write.Mode switch
        {
            RowWriteMode.Insert => inner.Insert(write.Values!, write.Options),
            RowWriteMode.Update => inner.Update(write.Values!, write.Options),
            RowWriteMode.Upsert when write.Options.Precondition.Kind == WritePreconditionKind.IfVersion => inner is IConcurrencyStorageSession expectedConcurrency
                ? expectedConcurrency.ConditionalUpsert(write.Values!, write.Options)
                : throw new NotSupportedException("The provider session does not support conditional upsert."),
            RowWriteMode.Upsert => inner.Upsert(write.Values!, write.Options),
            RowWriteMode.ConditionalUpsert => inner is IConcurrencyStorageSession concurrency
                ? concurrency.ConditionalUpsert(write.Values!, write.Options)
                : throw new NotSupportedException("The provider session does not support conditional upsert."),
            RowWriteMode.Delete => inner.Delete(write.Key!, write.Options),
            RowWriteMode.CompareAndDelete => inner is ICompareAndDeleteStorageSession compareAndDelete
                ? compareAndDelete.CompareAndDelete(write.Key!, write.ExpectedValues, write.Options)
                : throw new NotSupportedException("The provider session does not support compare-and-delete."),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        })).ToArray();
        return outcomes;
    }
}

internal sealed class BatchCompareAndDeleteStorageSession : BatchStorageSession, ICompareAndDeleteStorageSession
{
    internal BatchCompareAndDeleteStorageSession(IStorageSession inner, BatchContext context)
        : base(inner, context)
    {
    }

    public WriteOutcome CompareAndDelete(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null)
    {
        if (inner is not ICompareAndDeleteStorageSession compareAndDelete)
            throw new NotSupportedException(
                "GW-COMPARE-DELETE-001: this provider does not advertise atomic compare-and-delete.");

        context.FlushFor(Unit, key);
        return compareAndDelete.CompareAndDelete(key, expectedValues, options);
    }
}
