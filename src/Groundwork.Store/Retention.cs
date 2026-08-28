using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>Bounds one resumable retention pass.</summary>
public sealed record RetentionExecutionOptions
{
    public int MaxRowsPerBatch { get; init; } = 512;

    /// <summary>Optional per-pass retention override. Null uses the declaration; zero deletes all rows.</summary>
    public int? KeepNewestOverride { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Evidence returned by one retention pass.</summary>
public sealed record RetentionResult(
    int DeletedRows,
    int Batches,
    bool Completed = true)
{
    public bool IsComplete => Completed;
}

/// <summary>Provider-native retention execution seam.</summary>
public interface IRetentionStorageSession
{
    RetentionResult ApplyRetention(RetentionExecutionOptions? options = null);

    ValueTask<RetentionResult> ApplyRetentionAsync(RetentionExecutionOptions? options = null);
}

/// <summary>Public retention entry point shared by all provider-neutral sessions.</summary>
public static class RetentionSessionExtensions
{
    public static RetentionResult ApplyRetention(
        this IStorageSession session,
        RetentionExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        StorageAccessValidation.EnsurePointOperation(session.Access, "retention");
        options ??= new RetentionExecutionOptions();
        ValidateExecutionOptions(options);
        return session is IRetentionStorageSession native
            ? native.ApplyRetention(options)
            : ApplyReference(session, options);
    }

    public static RetentionResult Retain(
        this IStorageSession session,
        RetentionExecutionOptions? options = null) =>
        ApplyRetention(session, options);

    public static ValueTask<RetentionResult> ApplyRetentionAsync(
        this IStorageSession session,
        RetentionExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        StorageAccessValidation.EnsurePointOperation(session.Access, "retention");
        options ??= new RetentionExecutionOptions();
        ValidateExecutionOptions(options);
        return session is IRetentionStorageSession native
            ? native.ApplyRetentionAsync(options)
            : ApplyReferenceAsync(session, options);
    }

    private static RetentionResult ApplyReference(
        IStorageSession session,
        RetentionExecutionOptions options)
    {
        var declaration = session.Unit.Retention ??
            throw new InvalidOperationException($"Storage unit '{session.Unit.Name}' does not declare retention.");
        var request = new QueryRequest(
            new TableId(session.Unit.Name),
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.None);
        var rows = session.Query(request).Rows;
        var victims = RetentionRows.OrderVictims(
            session.Unit,
            declaration,
            EffectiveKeepNewest(session.Unit, options),
            rows);
        return DeleteVictims(session, victims, options);
    }

    private static async ValueTask<RetentionResult> ApplyReferenceAsync(
        IStorageSession session,
        RetentionExecutionOptions options)
    {
        var declaration = session.Unit.Retention ??
            throw new InvalidOperationException($"Storage unit '{session.Unit.Name}' does not declare retention.");
        var request = new QueryRequest(
            new TableId(session.Unit.Name),
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.None);
        var rows = (await session.QueryAsync(request, cancellationToken: options.CancellationToken)
            .ConfigureAwait(false)).Rows;
        var victims = RetentionRows.OrderVictims(
            session.Unit,
            declaration,
            EffectiveKeepNewest(session.Unit, options),
            rows);
        return await DeleteVictimsAsync(session, victims, options).ConfigureAwait(false);
    }

    internal static async ValueTask<RetentionResult> DeleteVictimsAsync(
        IStorageSession session,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> victims,
        RetentionExecutionOptions options)
    {
        var deleted = 0;
        var batches = 0;
        foreach (var batch in victims.Chunk(options.MaxRowsPerBatch))
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            foreach (var row in batch)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                var key = session.Unit.Key.Columns.ToDictionary(
                    column => column,
                    column => row.GetValueOrDefault(column),
                    StringComparer.Ordinal);
                var outcome = await session.DeleteAsync(new StorageKey(key),
                    cancellationToken: options.CancellationToken).ConfigureAwait(false);
                if (outcome.Status == WriteOutcomeStatus.Deleted)
                    deleted++;
            }
            batches++;
        }

        return new RetentionResult(deleted, batches);
    }

    internal static RetentionResult DeleteVictims(
        IStorageSession session,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> victims,
        RetentionExecutionOptions options)
    {
        var deleted = 0;
        var batches = 0;
        foreach (var batch in victims.Chunk(options.MaxRowsPerBatch))
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            foreach (var row in batch)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                var key = session.Unit.Key.Columns.ToDictionary(
                    column => column,
                    column => row.GetValueOrDefault(column),
                    StringComparer.Ordinal);
                var outcome = session.Delete(new StorageKey(key));
                if (outcome.Status == WriteOutcomeStatus.Deleted)
                    deleted++;
            }
            batches++;
        }

        return new RetentionResult(deleted, batches);
    }

    internal static void ValidateExecutionOptions(RetentionExecutionOptions options)
    {
        if (options.MaxRowsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxRowsPerBatch));
        if (options.KeepNewestOverride is < 0)
            throw new ArgumentOutOfRangeException(nameof(options.KeepNewestOverride),
                "KeepNewestOverride cannot be negative.");
    }

    internal static int EffectiveKeepNewest(StorageUnit unit, RetentionExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(options);
        var declaration = unit.Retention ??
            throw new InvalidOperationException($"Storage unit '{unit.Name}' does not declare retention.");
        ValidateExecutionOptions(options);
        return options.KeepNewestOverride ?? declaration.KeepNewest;
    }
}

internal static class RetentionRows
{
    internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> OrderVictims(
        StorageUnit unit,
        RetentionDeclaration declaration,
        int keepNewest,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var partitionColumns = declaration.PartitionColumns ?? [];
        return rows
            .GroupBy(row => StructuralRetentionKey.From(row, partitionColumns), StructuralRetentionKeyComparer.Instance)
            .SelectMany(group => group
                .OrderByDescending(row => row.GetValueOrDefault(declaration.OrderColumn), RetentionValueComparer.Instance)
                .ThenBy(row => StructuralRetentionKey.From(row, unit.Key.Columns), StructuralRetentionKeyComparer.Instance)
                .Skip(keepNewest))
            .ToArray();
    }

    private sealed class RetentionValueComparer : IComparer<object?>
    {
        internal static RetentionValueComparer Instance { get; } = new();

        public int Compare(object? left, object? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            if (left is DateTimeOffset leftInstant && right is DateTimeOffset rightInstant)
                return leftInstant.UtcTicks.CompareTo(rightInstant.UtcTicks);
            if (left is string leftText && right is string rightText)
                return string.CompareOrdinal(leftText, rightText);
            if (left is byte[] leftBytes && right is byte[] rightBytes)
                return leftBytes.AsSpan().SequenceCompareTo(rightBytes);
            if (left.GetType() != right.GetType())
                return string.CompareOrdinal(left.GetType().FullName, right.GetType().FullName);
            return ((IComparable)left).CompareTo(right);
        }
    }

    private sealed class StructuralRetentionKey
    {
        private StructuralRetentionKey(object?[] values) => Values = values;

        internal object?[] Values { get; }

        internal static StructuralRetentionKey From(
            IReadOnlyDictionary<string, object?> row,
            IReadOnlyList<string> columns) =>
            new(columns.Select(column => row.GetValueOrDefault(column)).ToArray());
    }

    private sealed class StructuralRetentionKeyComparer :
        IEqualityComparer<StructuralRetentionKey>,
        IComparer<StructuralRetentionKey>
    {
        internal static StructuralRetentionKeyComparer Instance { get; } = new();

        public bool Equals(StructuralRetentionKey? left, StructuralRetentionKey? right) => Compare(left, right) == 0;

        public int GetHashCode(StructuralRetentionKey key)
        {
            var hash = new HashCode();
            foreach (var value in key.Values)
            {
                hash.Add(value?.GetType());
                switch (value)
                {
                    case null:
                        break;
                    case string text:
                        hash.Add(text, StringComparer.Ordinal);
                        break;
                    case byte[] bytes:
                        foreach (var item in bytes) hash.Add(item);
                        break;
                    case DateTimeOffset instant:
                        hash.Add(instant.UtcTicks);
                        break;
                    default:
                        hash.Add(value);
                        break;
                }
            }
            return hash.ToHashCode();
        }

        public int Compare(StructuralRetentionKey? left, StructuralRetentionKey? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var count = Math.Min(left.Values.Length, right.Values.Length);
            for (var index = 0; index < count; index++)
            {
                var comparison = RetentionValueComparer.Instance.Compare(left.Values[index], right.Values[index]);
                if (comparison != 0) return comparison;
            }
            return left.Values.Length.CompareTo(right.Values.Length);
        }
    }
}

/// <summary>
/// Coalesces post-append cleanup per provider connection, storage unit, and scope. Appenders
/// arriving while cleanup is active mark the watermark dirty and return; the active owner drains
/// that signal before releasing ownership, so concurrent sessions never wait in a cleanup convoy.
/// </summary>
internal static class OnAppendRetentionCoordinator
{
    private static readonly ConditionalWeakTable<object, OwnerState> Owners = new();

    /// <summary>
    /// True when a batch contains an append that should trigger on-append retention. The observer is no
    /// longer dug out of the first staged write's options — it belongs to the session, which already has it.
    /// </summary>
    internal static bool ContainsAppend(IReadOnlyList<RowWriteOutcome> outcomes)
    {
        foreach (var outcome in outcomes)
        {
            if (outcome.Outcome.Succeeded && outcome.Write.Mode is RowWriteMode.Insert or RowWriteMode.Upsert)
                return true;
        }
        return false;
    }

    internal static void Run(
        object owner,
        StorageUnit unit,
        string? scope,
        Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        Run(owner, unit, scope, () => { cleanup(); return ValueTask.CompletedTask; })
            .GetAwaiter().GetResult();
    }

    internal static ValueTask Run(
        object owner,
        StorageUnit unit,
        string? scope,
        Func<ValueTask> cleanup)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(cleanup);
        var state = State(owner, unit, scope);
        Interlocked.Exchange(ref state.Pending, 1);
        return Drain(state, cleanup);
    }

    /// <summary>
    /// Registers an appender before it enters a provider write gate. The last concurrent
    /// appender drains the shared dirty signal after every registered write has committed.
    /// </summary>
    internal static AppendRegistration Begin(
        object owner,
        StorageUnit unit,
        string? scope,
        Action? onRegistered = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(unit);
        var state = State(owner, unit, scope);
        Interlocked.Increment(ref state.Appenders);
        try
        {
            onRegistered?.Invoke();
            return new AppendRegistration(state);
        }
        catch
        {
            Interlocked.Decrement(ref state.Appenders);
            throw;
        }
    }

    private static DrainState State(object owner, StorageUnit unit, string? scope)
    {
        var identity = $"{unit.Id.Value.Length}:{unit.Id.Value}|{scope?.Length ?? -1}:{scope}";
        return Owners.GetValue(owner, static _ => new OwnerState()).States
            .GetOrAdd(identity, static _ => new DrainState());
    }

    private static async ValueTask Drain(DrainState state, Func<ValueTask> cleanup)
    {
        while (true)
        {
            if (Interlocked.CompareExchange(ref state.Running, 1, 0) != 0)
                return;
            try
            {
                while (Interlocked.Exchange(ref state.Pending, 0) != 0)
                    await cleanup().ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Exchange(ref state.Pending, 1);
                throw;
            }
            finally
            {
                Volatile.Write(ref state.Running, 0);
            }

            // Covers an append that marked the state dirty after the final drain check but
            // before ownership was released. If it acquired ownership itself, this loses the
            // compare-exchange above and returns without waiting.
            if (Volatile.Read(ref state.Pending) == 0)
                return;
        }
    }

    internal sealed class AppendRegistration(DrainState state) : IDisposable
    {
        private int completed;

        internal void Complete(bool cleanupRequired, Action cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            Complete(cleanupRequired, () => { cleanup(); return ValueTask.CompletedTask; })
                .GetAwaiter().GetResult();
        }

        internal ValueTask Complete(bool cleanupRequired, Func<ValueTask> cleanup)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            if (Interlocked.Exchange(ref completed, 1) != 0)
                throw new InvalidOperationException("An OnAppend registration can only be completed once.");
            if (cleanupRequired)
                Interlocked.Exchange(ref state.Pending, 1);
            return Interlocked.Decrement(ref state.Appenders) == 0 && Volatile.Read(ref state.Pending) != 0
                ? Drain(state, cleanup)
                : ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref completed, 1) == 0)
                Interlocked.Decrement(ref state.Appenders);
        }
    }

    private sealed class OwnerState
    {
        internal ConcurrentDictionary<string, DrainState> States { get; } = new(StringComparer.Ordinal);
    }

    internal sealed class DrainState
    {
        internal int Appenders;
        internal int Pending;
        internal int Running;
    }
}
