using System.Data.Common;
using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Query.Model;
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
    private readonly RelationalEvidenceCapture? evidenceCapture;

    internal RelationalSessionPointReads(
        StorageUnit unit,
        StorageAccess access,
        IReadOnlyList<ColumnDefinition> userColumns,
        ColumnDefinition? versionColumn,
        Func<string, DbCommand> createCommand,
        IRelationalPointReadAdapter adapter,
        IProviderCommandObserver? observer,
        string operationPrefix,
        RelationalEvidenceCapture? evidenceCapture = null)
    {
        this.unit = unit;
        this.access = access;
        this.userColumns = userColumns;
        this.versionColumn = versionColumn;
        this.createCommand = createCommand;
        this.adapter = adapter;
        this.observer = observer;
        this.operationPrefix = operationPrefix;
        if (observer is IProviderExecutionObserver)
        {
            this.evidenceCapture = evidenceCapture ?? new();
        }
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
        // Probe correlation belongs to the enclosing write operation, not a separate read invocation.
        var evidenceObserver = !isProbe ? observer as IProviderExecutionObserver : null;
        var evidenceOptions = evidenceObserver is null
            ? null
            : evidenceCapture!.InvokeStructuredObserver(() => evidenceObserver.EvidenceOptions);
        var keyBounds = evidenceObserver is null ? null : new List<ProviderPointReadKeyBound>();
        var shapeCollected = evidenceObserver is not null;
        var keyColumns = unit.Key.Columns.ToList();
        if (unit.Columns.Any(column => column.Name == ProviderOwnedColumns.Scope) &&
            !keyColumns.Contains(ProviderOwnedColumns.Scope, StringComparer.Ordinal))
        {
            keyColumns.Add(ProviderOwnedColumns.Scope);
        }
        var clauses = new List<string>(keyColumns.Count);
        var renderParameters = new List<QueryRenderParameter>(keyColumns.Count);
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
            var predicate = adapter.RenderEquality(unit, column, parameter, exactStringKeys, evidenceObserver is not null);
            clauses.Add(predicate.Sql);
            adapter.Bind(command, parameter, value, column);
            if (RelationalSessionPolicy.QueryTypeOf(column.Type) is { } queryType)
                renderParameters.Add(new QueryRenderParameter(parameter.TrimStart('@'), queryType, value));
            if (keyBounds is not null)
            {
                if (predicate.Evidence is { } bound)
                    keyBounds.Add(bound);
                else
                    shapeCollected = false;
            }
        }

        var columns = userColumns.Concat(versionColumn is null ? [] : [versionColumn]);
        command.CommandText =
            $"SELECT {string.Join(", ", columns.Select(column => adapter.QuoteIdentifier(column.Name)))} " +
            $"FROM {adapter.QuoteIdentifier(unit.Name)} WHERE {string.Join(" AND ", clauses)}" +
            adapter.LockingClause(forUpdate) + ";";
        var identity = evidenceObserver is null ? null : evidenceCapture!.NewInvocation();
        var target = evidenceObserver is null ? null : evidenceCapture!.Target(unit,
            keyColumns.Contains(ProviderOwnedColumns.Scope, StringComparer.Ordinal)
                ? ProviderScopeBindingMode.Predicate : ProviderScopeBindingMode.Unscoped);
        ProviderIdentity? provider = evidenceObserver is null ? null : new(
            adapter.EvidenceProviderName ?? operationPrefix, command.Connection!.ServerVersion);
        // The uniqueness witness and the native plan are provider observations made before the read,
        // the way bounded queries collect theirs; neither is inferred from the declaration (#423).
        var uniqueness = shapeCollected
            ? await adapter.ObserveUniqueness(unit, keyColumns, execution).ConfigureAwait(false)
            : new ProviderPointReadUniqueness(ProviderPointReadUniquenessStatus.NotObserved);
        var pointRead = shapeCollected ? new ProviderPointReadEvidence(
            keyBounds!, uniqueness,
            ProviderNativeBound.Absent, materializerReadsAtMostOne: true,
            lockMode: adapter.EvidenceLockMode(forUpdate)) : null;
        var plan = evidenceOptions?.CollectNativePlans == true
            ? renderParameters.Count == keyColumns.Count
                ? await adapter.InspectPointReadPlan(
                    new RelationalQueryCommand(command.CommandText, renderParameters, includesTotalCount: false,
                        isMatchNone: false, selectedIndex: null, indexHintApplied: false, appliedOrder: []),
                    execution, evidenceCapture!).ConfigureAwait(false)
                : new ProviderPlanEvidence(ProviderEvidenceAvailability.Unsupported)
            : ProviderPlanEvidence.NotRequested;

        observer?.Observe(new ProviderCommandEvent(
            observerOperation ?? operationPrefix + ".write-probe",
            command.CommandText,
            ProviderCommandKind.Read,
            IsProbe: isProbe));

        var outcome = ProviderExecutionOutcome.Failed;
        ProviderExecutionFailureCategory? failure = ProviderExecutionFailureCategory.Provider;
        var issued = false;
        try
        {
            var entry = await ReadEntry(command, execution,
                evidenceObserver is null ? null : () => issued = true).ConfigureAwait(false);
            outcome = ProviderExecutionOutcome.Succeeded;
            failure = null;
            return entry;
        }
        catch (OperationCanceledException)
        {
            outcome = ProviderExecutionOutcome.Cancelled;
            failure = ProviderExecutionFailureCategory.Cancellation;
            throw;
        }
        finally
        {
            if (evidenceObserver is not null && issued)
            {
                try
                {
                    evidenceCapture!.InvokeStructuredObserver(() => evidenceObserver.ObserveExecution(
                        new ProviderExecutionEvidence(
                            provider!, ProviderExecutionOperation.PointRead, ProviderCommandKind.Read,
                            ProviderExecutionRole.Statement, identity!, target!, outcome, failure,
                            shapeCollected ? ProviderEvidenceAvailability.Collected : ProviderEvidenceAvailability.Unsupported,
                            pointRead: pointRead, plan: plan)));
                }
                catch when (outcome != ProviderExecutionOutcome.Succeeded)
                {
                    // An observer failure must not replace the actual provider/cancellation exception.
                }
            }
        }
    }

    private async ValueTask<StoredEntry?> ReadEntry(DbCommand command, RelationalExecution execution, Action? onIssuing)
    {
        execution.CancellationToken.ThrowIfCancellationRequested();
        onIssuing?.Invoke();
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

    internal void ValidatePublicRead() =>
        StorageAccessValidation.EnsurePointOperation(access, "read");

    internal async ValueTask<StoredEntry?> ReadPublic(StorageKey key, RelationalExecution execution)
    {
        return RelationalSessionPolicy.PublicEntry(await Read(
            key,
            execution,
            observerOperation: operationPrefix + ".read",
            isProbe: false).ConfigureAwait(false));
    }
}

internal interface IRelationalPointReadAdapter
{
    string? EvidenceProviderName => null;

    /// <summary>
    /// Collects the provider's native plan for the rendered point read through the same explain seam
    /// as bounded queries. The default reports the plan as unsupported rather than inferring one.
    /// </summary>
    ValueTask<ProviderPlanEvidence> InspectPointReadPlan(
        RelationalQueryCommand query, RelationalExecution execution, RelationalEvidenceCapture capture) =>
        new(new ProviderPlanEvidence(ProviderEvidenceAvailability.Unsupported));

    /// <summary>
    /// Observes from the provider catalog whether a unique index or primary key enforces exactly the
    /// point read's key columns (scope included when the unit is scoped). The default observes nothing.
    /// </summary>
    ValueTask<ProviderPointReadUniqueness> ObserveUniqueness(
        StorageUnit unit, IReadOnlyList<string> keyColumns, RelationalExecution execution) =>
        new(new ProviderPointReadUniqueness(ProviderPointReadUniquenessStatus.NotObserved));

    ProviderPointReadLockMode EvidenceLockMode(bool forUpdate) => ProviderPointReadLockMode.Unknown;

    RelationalPointReadPredicate RenderEquality(StorageUnit unit, ColumnDefinition column,
        string parameter, bool exactStringKeys, bool collectEvidence) =>
        new(Equality(column, parameter, exactStringKeys), null);

    string QuoteIdentifier(string identifier);

    string Equality(ColumnDefinition column, string parameter, bool exactStringKeys);

    void Bind(DbCommand command, string parameter, object? value, ColumnDefinition column);

    object? Decode(object value, ColumnDefinition column);

    string LockingClause(bool forUpdate);
}

/// <summary>
/// Turns provider-observed unique key sets into the point read's uniqueness witness: a unique index or
/// primary key whose columns are exactly the read's key columns (scope included) enforces at most one row.
/// </summary>
internal static class RelationalPointReadUniqueness
{
    internal static ProviderPointReadUniqueness Observe(
        IReadOnlyList<string> keyColumns,
        IEnumerable<IReadOnlyList<string>> uniqueKeySets)
    {
        var wanted = keyColumns.ToHashSet(StringComparer.Ordinal);
        foreach (var uniqueColumns in uniqueKeySets)
        {
            if (uniqueColumns.Count != wanted.Count || !uniqueColumns.All(wanted.Contains))
                continue;
            var includesScope = uniqueColumns.Contains(ProviderOwnedColumns.Scope, StringComparer.Ordinal);
            return new ProviderPointReadUniqueness(
                ProviderPointReadUniquenessStatus.Observed,
                uniqueColumns.Where(column => column != ProviderOwnedColumns.Scope),
                includesScope);
        }
        return new ProviderPointReadUniqueness(ProviderPointReadUniquenessStatus.NotObserved);
    }
}

internal readonly record struct RelationalPointReadPredicate(string Sql, ProviderPointReadKeyBound? Evidence)
{
    // Providers opt in at their actual equality emitter. Sharing value-free binding construction
    // does not opt an unmapped provider into evidence or change its native equality semantics.
    internal static RelationalPointReadPredicate WithKeyBound(string sql, StorageUnit unit,
        ColumnDefinition column, bool collectEvidence)
    {
        ProviderPointReadKeyBound? evidence = null;
        if (collectEvidence && RelationalSessionPolicy.QueryColumn(unit, column.Name) is { } queryColumn)
        {
            var isScope = column.Name == ProviderOwnedColumns.Scope;
            evidence = new(isScope ? null : column.Name, queryColumn.Type,
                isScope ? ProviderPointReadBindingRole.Scope : ProviderPointReadBindingRole.Key,
                new(Guid.NewGuid()));
        }
        return new(sql, evidence);
    }
}
