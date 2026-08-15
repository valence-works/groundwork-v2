using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Testing;

/// <summary>Bounds one resumable retention pass.</summary>
public sealed record RetentionExecutionOptions
{
    public int MaxRowsPerBatch { get; init; } = 512;

    public IWritePathObserver? Observer { get; init; }

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
}

/// <summary>Public retention entry point shared by all provider-neutral sessions.</summary>
public static class RetentionSessionExtensions
{
    public static RetentionResult ApplyRetention(
        this IStorageSession session,
        RetentionExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        options ??= new RetentionExecutionOptions();
        ValidateOptions(options);
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
        RetentionExecutionOptions? options = null) =>
        ValueTask.FromResult(ApplyRetention(session, options));

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
        var victims = RetentionRows.OrderVictims(session.Unit, declaration, rows);
        return DeleteVictims(session, victims, options);
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

    private static void ValidateOptions(RetentionExecutionOptions options)
    {
        if (options.MaxRowsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxRowsPerBatch));
    }
}

internal static class RetentionRows
{
    internal static IReadOnlyList<IReadOnlyDictionary<string, object?>> OrderVictims(
        StorageUnit unit,
        RetentionDeclaration declaration,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var partitionColumns = declaration.PartitionColumns ?? [];
        return rows
            .GroupBy(row => CanonicalPartition(row, partitionColumns), StringComparer.Ordinal)
            .SelectMany(group => group
                .OrderByDescending(row => row.GetValueOrDefault(declaration.OrderColumn), RetentionValueComparer.Instance)
                .ThenBy(row => CanonicalKey(unit, row), StringComparer.Ordinal)
                .Skip(declaration.KeepNewest))
            .ToArray();
    }

    private static string CanonicalPartition(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<string> columns) =>
        string.Join("\u001f", columns.Select(column => Canonical(row.GetValueOrDefault(column))));

    private static string CanonicalKey(StorageUnit unit, IReadOnlyDictionary<string, object?> row) =>
        string.Join("\u001f", unit.Key.Columns.Select(column => Canonical(row.GetValueOrDefault(column))));

    private static string Canonical(object? value) => value switch
    {
        null => "null",
        DateTimeOffset instant => "t:" + instant.UtcTicks.ToString(CultureInfo.InvariantCulture),
        byte[] bytes => "b:" + Convert.ToBase64String(bytes),
        IFormattable formattable => value.GetType().FullName + ":" + formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.GetType().FullName + ":" + value
    };

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
            return ((IComparable)left).CompareTo(right);
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

    internal static bool TryGetObserver(
        IReadOnlyList<RowWriteOutcome> outcomes,
        out IWritePathObserver? observer)
    {
        foreach (var outcome in outcomes)
        {
            if (!outcome.Outcome.Succeeded || outcome.Write.Mode is not (RowWriteMode.Insert or RowWriteMode.Upsert))
                continue;
            observer = outcome.Write.Options.Observer;
            return true;
        }
        observer = null;
        return false;
    }

    internal static void Run(
        object owner,
        StorageUnit unit,
        string? scope,
        Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(cleanup);
        var identity = $"{unit.Id.Value.Length}:{unit.Id.Value}|{scope?.Length ?? -1}:{scope}";
        var state = Owners.GetValue(owner, static _ => new OwnerState()).States
            .GetOrAdd(identity, static _ => new DrainState());
        Interlocked.Exchange(ref state.Pending, 1);

        while (true)
        {
            if (Interlocked.CompareExchange(ref state.Running, 1, 0) != 0)
                return;
            try
            {
                while (Interlocked.Exchange(ref state.Pending, 0) != 0)
                    cleanup();
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

    private sealed class OwnerState
    {
        internal ConcurrentDictionary<string, DrainState> States { get; } = new(StringComparer.Ordinal);
    }

    private sealed class DrainState
    {
        internal int Pending;
        internal int Running;
    }
}
