using System.Collections.Immutable;
using System.Globalization;

namespace Groundwork.Kernel.Schema;

/// <summary>Refusal codes raised by the expand–contract workflow.</summary>
public static class ExpandContractCodes
{
    /// <summary>The applied ledger does not record the expand half of this supersession.</summary>
    public const string ExpandNotApplied = "GW-EXPAND-001";

    /// <summary>The data migration that populates the replacement column is not recorded complete.</summary>
    public const string BackfillIncomplete = "GW-EXPAND-002";

    /// <summary>The declared dual-presence window has not elapsed yet.</summary>
    public const string WindowNotElapsed = "GW-EXPAND-003";

    /// <summary>A contract plan was requested without readiness established from durable state.</summary>
    public const string ReadinessNotEstablished = "GW-EXPAND-004";

    /// <summary>Readiness was established against a different target or a different applied state.</summary>
    public const string ReadinessMismatched = "GW-EXPAND-005";

    /// <summary>A declaration withdrew a supersession whose column is still retained.</summary>
    public const string RetainedSupersessionWithdrawn = "GW-EXPAND-006";
}

/// <summary>
/// Which half of an expand–contract evolution a plan describes. Expand is additive: it adds the
/// replacement column and retains the superseded one, so an application version that predates the
/// change keeps reading and writing exactly what it did before. Contract removes the superseded
/// column, and only once its readiness is established from durable state.
/// </summary>
public enum SchemaEvolutionPhase
{
    Expand,
    Contract
}

/// <summary>What the applied ledger records about one superseded column.</summary>
public enum ColumnSupersessionState
{
    /// <summary>The column is still physically present and deliberately not planned for removal.</summary>
    Retained,

    /// <summary>The column has been removed. The state is terminal: nothing re-adds a superseded column.</summary>
    Contracted
}

/// <summary>
/// One column being replaced by another across a dual-presence window. The superseded column is
/// deliberately <em>not</em> a declared column of the subject: an undeclared column cannot be read,
/// written, altered, or renamed by the declaration that supersedes it, which is what makes the
/// expand plan invisible to the application version that still owns it.
/// </summary>
public sealed record ColumnSupersession
{
    public ColumnSupersession(ColumnDefinition supersededColumn, string replacementColumn)
    {
        ArgumentNullException.ThrowIfNull(supersededColumn);
        ArgumentException.ThrowIfNullOrWhiteSpace(supersededColumn.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementColumn);
        if (string.Equals(supersededColumn.Name, replacementColumn, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Column '{replacementColumn}' cannot supersede itself; a column that keeps its name is renamed " +
                "or altered in place, not superseded.",
                nameof(replacementColumn));
        }

        SupersededColumn = AddColumnOperation.Snapshot(supersededColumn);
        ReplacementColumn = replacementColumn;
    }

    /// <summary>
    /// The column being retired, as it stands in the catalog. It is deliberately carried in full
    /// rather than by name: it is not a declared column of the subject, so nothing else describes
    /// its portable type — and the backfill that populates the replacement has to read it with that
    /// type, on every provider, exactly as it would read a declared column.
    /// </summary>
    public ColumnDefinition SupersededColumn { get; }

    /// <summary>The declared column that replaces it.</summary>
    public string ReplacementColumn { get; }

    /// <summary>The physical name being retired.</summary>
    public string Name => SupersededColumn.Name;

    internal string Canonical => SchemaFingerprint.Canonicalize(
        [AddColumnOperation.CanonicalColumn(SupersededColumn), ReplacementColumn]);
}

/// <summary>What durable state says about one supersession at the moment it was assessed.</summary>
public sealed record ColumnSupersessionStatus(
    string SupersededColumn,
    string ReplacementColumn,
    ColumnSupersessionState State,
    DateTimeOffset? RetainedSince,
    DateTimeOffset? BackfillCompletedAt,
    DateTimeOffset? ContractableAt)
{
    /// <summary>Whether the contract half of this supersession is gated open.</summary>
    public bool IsContractable => State == ColumnSupersessionState.Contracted || ContractableAt is not null;
}

/// <summary>
/// Evidence that every active supersession on one target may be contracted, established from the
/// applied schema ledger and the data-migration ledger rather than asserted by a caller.
/// </summary>
/// <remarks>
/// It is a sealed class whose only constructor is internal to the kernel, so an application or a
/// provider assembly cannot manufacture one: the sole way to obtain an instance is
/// <see cref="ExpandContractWorkflow.AssessContractReadiness"/>, which reads durable state. This is
/// the same discipline as <see cref="DataMigrationExhaustion"/> — "ready" is unrepresentable
/// without the evidence that establishes it — and it is why a positional record would be wrong
/// here: a record's public constructor would let a caller write <c>new(refusals: [])</c>.
/// </remarks>
public sealed class ContractReadinessAssessment
{
    internal ContractReadinessAssessment(
        PhysicalSchemaTargetIdentity target,
        string? appliedSnapshotFingerprint,
        ImmutableArray<ColumnSupersessionStatus> supersessions,
        ImmutableArray<SchemaRefusal> refusals)
    {
        Target = target;
        AppliedSnapshotFingerprint = appliedSnapshotFingerprint;
        Supersessions = supersessions;
        Refusals = refusals;
    }

    public PhysicalSchemaTargetIdentity Target { get; }

    /// <summary>The applied snapshot this evidence was established against; null when nothing is applied.</summary>
    public string? AppliedSnapshotFingerprint { get; }

    public ImmutableArray<ColumnSupersessionStatus> Supersessions { get; }

    public ImmutableArray<SchemaRefusal> Refusals { get; }

    /// <summary>True only when every supersession the declaration still names is contractable.</summary>
    public bool IsReady => Refusals.Length == 0;

    /// <summary>
    /// Whether this evidence describes exactly the target and applied state being planned. Evidence
    /// gathered against a different applied snapshot proves nothing about this one.
    /// </summary>
    internal bool Describes(PhysicalSchemaTarget target, PhysicalSchemaAppliedState? applied) =>
        Target == target.Identity &&
        string.Equals(AppliedSnapshotFingerprint, applied?.Snapshot.Fingerprint, StringComparison.Ordinal);
}

/// <summary>
/// The one rule that decides whether the contract half of an expand–contract evolution may run.
/// Both the deployment tool's read-only report and <see cref="PhysicalSchemaApplication"/> call it,
/// so a gate that opens in a report cannot close differently under apply.
/// </summary>
public static class ExpandContractWorkflow
{
    /// <summary>
    /// Establishes, from the applied schema ledger and the data-migration ledger, whether each
    /// declared supersession may be contracted. Three facts have to hold, and each of the three is
    /// read from durable state rather than supplied:
    /// <list type="number">
    /// <item>the applied ledger records the column as retained beside its replacement, which only
    /// an applied expand plan writes;</item>
    /// <item>the data migration that populates the replacement is recorded complete, which per the
    /// data-migration facility only an exhausted source produces;</item>
    /// <item>the declared dual-presence window has elapsed since the later of those two instants.</item>
    /// </list>
    /// </summary>
    public static ContractReadinessAssessment AssessContractReadiness(
        PhysicalSchemaTarget target,
        PhysicalSchemaHistoryState history,
        IReadOnlyList<DataMigrationLedgerEntry>? dataMigrations,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(history);
        var applied = history.AppliedState;
        // Null means the provider offers no data-migration execution at all, which is a different
        // fact from an empty ledger and is named as one rather than reported as "not started".
        var entries = dataMigrations ?? [];
        var hasLedger = dataMigrations is not null;
        var evolution = target.Subject.Evolution;
        var window = evolution.DualPresenceWindow;
        var migrationId = evolution.SemanticMigrationId;
        var markers = AppliedMarkers(applied);
        var statuses = ImmutableArray.CreateBuilder<ColumnSupersessionStatus>();
        var refusals = ImmutableArray.CreateBuilder<SchemaRefusal>();

        foreach (var supersession in evolution.Supersessions)
        {
            var superseded = supersession.Name;
            var replacement = supersession.ReplacementColumn;
            var path = $"schema.supersessions.{superseded}";
            if (markers.TryGetValue(superseded, out var marker) &&
                marker.State == ColumnSupersessionState.Contracted)
            {
                // Terminal: the column is gone and the ledger says so. Nothing left to gate.
                statuses.Add(new ColumnSupersessionStatus(
                    superseded, replacement, ColumnSupersessionState.Contracted, marker.AppliedAt, null, null));
                continue;
            }

            // The recorded marker has to be this supersession, not a different one on the same
            // column: re-pointing a supersession at another replacement restarts the window rather
            // than inheriting the retention instant of the one it abandoned.
            if (applied is null || marker is null ||
                !string.Equals(marker.ReplacementColumn, replacement, StringComparison.Ordinal) ||
                !applied.Snapshot.Subject.Columns.Any(column =>
                    string.Equals(column.Name, replacement, StringComparison.Ordinal)))
            {
                refusals.Add(new SchemaRefusal(
                    ExpandContractCodes.ExpandNotApplied,
                    $"Column '{superseded}' cannot be contracted: the applied ledger does not record it as retained " +
                    $"beside replacement column '{replacement}'. Apply the expand plan first.",
                    path));
                statuses.Add(new ColumnSupersessionStatus(
                    superseded, replacement, ColumnSupersessionState.Retained, marker?.AppliedAt, null, null));
                continue;
            }

            var entry = entries.FirstOrDefault(item =>
                string.Equals(item.MigrationId, migrationId, StringComparison.Ordinal));
            if (entry is null || !entry.IsComplete)
            {
                refusals.Add(new SchemaRefusal(
                    ExpandContractCodes.BackfillIncomplete,
                    $"Column '{superseded}' cannot be contracted until data migration '{migrationId}' is recorded " +
                    (hasLedger
                        ? $"complete; the ledger records it as {(entry is null ? "not started" : "running")}."
                        : "complete; this provider records no data migrations, so nothing can establish that it finished."),
                    path));
                statuses.Add(new ColumnSupersessionStatus(
                    superseded, replacement, ColumnSupersessionState.Retained, marker.AppliedAt, null, null));
                continue;
            }

            // The window opens once the replacement column both exists and holds every backfilled
            // value, so it is measured from the later of the two instants rather than from whichever
            // one happens to be more convenient.
            var completedAt = entry.CompletedAt!.Value;
            var opensAt = marker.AppliedAt > completedAt ? marker.AppliedAt : completedAt;
            var contractableAt = opensAt + window;
            if (now < contractableAt)
            {
                refusals.Add(new SchemaRefusal(
                    ExpandContractCodes.WindowNotElapsed,
                    $"Column '{superseded}' cannot be contracted until its dual-presence window elapses at " +
                    $"{contractableAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}; " +
                    $"{contractableAt - now} of {window} remains.",
                    path));
                statuses.Add(new ColumnSupersessionStatus(
                    superseded, replacement, ColumnSupersessionState.Retained, marker.AppliedAt, completedAt, null));
                continue;
            }

            statuses.Add(new ColumnSupersessionStatus(
                superseded, replacement, ColumnSupersessionState.Retained, marker.AppliedAt, completedAt, contractableAt));
        }

        return new ContractReadinessAssessment(
            target.Identity,
            applied?.Snapshot.Fingerprint,
            statuses.ToImmutable(),
            refusals.ToImmutable());
    }

    /// <summary>The supersession markers the applied ledger holds, keyed on the superseded column.</summary>
    internal static IReadOnlyDictionary<string, AppliedColumnSupersession> AppliedMarkers(
        PhysicalSchemaAppliedState? applied)
    {
        var markers = new Dictionary<string, AppliedColumnSupersession>(StringComparer.Ordinal);
        if (applied is null)
            return markers;
        foreach (var operation in applied.AppliedOperations)
        {
            if (operation.Kind != PhysicalSchemaOperationKind.ColumnSupersession ||
                !ColumnSupersessionOperation.TryReadPayload(operation.CanonicalPayload, out var replacement, out var state))
            {
                continue;
            }
            markers[operation.SubjectIdentity] = new AppliedColumnSupersession(
                operation.SubjectIdentity, replacement, state, operation.AppliedAt);
        }
        return markers;
    }
}

/// <summary>One supersession marker as the applied ledger recorded it.</summary>
internal sealed record AppliedColumnSupersession(
    string SupersededColumn,
    string ReplacementColumn,
    ColumnSupersessionState State,
    DateTimeOffset AppliedAt);

/// <summary>
/// Resolves the declared supersessions against what the applied ledger already records, and decides
/// which columns this plan must leave alone. One place decides it, so the expand plan's suppression
/// and the contract plan's removal cannot disagree about which columns are superseded.
/// </summary>
internal sealed class ColumnSupersessionPlan
{
    private ColumnSupersessionPlan(
        ImmutableArray<ResolvedColumnSupersession> supersessions,
        IReadOnlySet<string> withheldColumns)
    {
        Supersessions = supersessions;
        WithheldColumns = withheldColumns;
    }

    public static ColumnSupersessionPlan Empty { get; } =
        new([], new HashSet<string>(StringComparer.Ordinal));

    public ImmutableArray<ResolvedColumnSupersession> Supersessions { get; }

    /// <summary>
    /// Columns the ordinary removal rules must not plan. Every superseded column is withheld in
    /// both phases: the expand plan leaves it in place, and the contract plan removes it through
    /// its own operation, which works whether or not the applied subject still describes it.
    /// </summary>
    public IReadOnlySet<string> WithheldColumns { get; }

    /// <summary>
    /// The still-present superseded columns, as declarations the rest of the kernel can treat like
    /// any other column. A backfill reads its source through the same portable typing a declared
    /// column gets, so the unit it runs against carries these alongside the declared columns.
    /// </summary>
    public ImmutableArray<ColumnDefinition> RetainedColumns =>
        [.. Active.Select(item => item.Declaration.SupersededColumn)];

    /// <summary>Supersessions this plan still has work for; a contracted one is finished.</summary>
    public IEnumerable<ResolvedColumnSupersession> Active =>
        Supersessions.Where(item => item.AppliedState != ColumnSupersessionState.Contracted);

    public static ColumnSupersessionPlan Resolve(
        PhysicalSchemaTarget target,
        PhysicalSchemaAppliedState? applied)
    {
        var declared = target.Subject.Evolution.Supersessions;
        if (declared.IsDefaultOrEmpty)
            return Empty;
        var markers = ExpandContractWorkflow.AppliedMarkers(applied);
        return new ColumnSupersessionPlan(
            [.. declared.Select(supersession => new ResolvedColumnSupersession(
                supersession,
                markers.TryGetValue(supersession.Name, out var marker)
                    ? marker.State
                    : ColumnSupersessionState.Retained))],
            declared.Select(supersession => supersession.Name).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// The operations this phase contributes: a ledger marker for every declared supersession, plus
    /// the removal itself in the contract phase. The marker is what a later contract plan reads to
    /// learn that the column is still there, and what makes the contracted state terminal.
    /// </summary>
    public IEnumerable<PhysicalSchemaOperation> Operations(SchemaSubject subject, SchemaEvolutionPhase phase)
    {
        foreach (var resolved in Supersessions)
        {
            var state = resolved.AppliedState == ColumnSupersessionState.Contracted ||
                        phase == SchemaEvolutionPhase.Contract
                ? ColumnSupersessionState.Contracted
                : ColumnSupersessionState.Retained;
            yield return new ColumnSupersessionOperation(subject, resolved.Declaration, state);
            if (phase == SchemaEvolutionPhase.Contract &&
                resolved.AppliedState != ColumnSupersessionState.Contracted)
            {
                yield return new DropColumnOperation(subject, resolved.Declaration.SupersededColumn);
            }
        }
    }

    /// <summary>
    /// Refuses a contract plan whose readiness was not established, or was established elsewhere.
    /// A caller cannot construct <see cref="ContractReadinessAssessment"/>, so the only way past
    /// this is durable state that actually says the columns may go.
    /// </summary>
    public ImmutableArray<SchemaRefusal> ValidateReadiness(
        PhysicalSchemaTarget target,
        PhysicalSchemaAppliedState? applied,
        ContractReadinessAssessment? readiness)
    {
        var active = Active.ToArray();
        if (active.Length == 0)
            return [];
        if (readiness is null)
        {
            return
            [
                new SchemaRefusal(
                    ExpandContractCodes.ReadinessNotEstablished,
                    $"A contract plan for '{target.Identity}' requires contract readiness established from the " +
                    "applied schema ledger and the data-migration ledger; none was supplied.",
                    "schema.supersessions")
            ];
        }
        if (!readiness.Describes(target, applied))
        {
            return
            [
                new SchemaRefusal(
                    ExpandContractCodes.ReadinessMismatched,
                    $"Contract readiness was established for '{readiness.Target}' against applied snapshot " +
                    $"'{readiness.AppliedSnapshotFingerprint ?? "<none>"}', which is not the state being planned.",
                    "schema.supersessions")
            ];
        }
        if (!readiness.IsReady)
            return readiness.Refusals;

        var contractable = readiness.Supersessions
            .Where(status => status.IsContractable)
            .Select(status => status.SupersededColumn)
            .ToHashSet(StringComparer.Ordinal);
        return
        [
            .. active
                .Where(item => !contractable.Contains(item.Declaration.Name))
                .Select(item => new SchemaRefusal(
                    ExpandContractCodes.ReadinessMismatched,
                    $"Contract readiness does not cover superseded column '{item.Declaration.Name}'.",
                    $"schema.supersessions.{item.Declaration.Name}"))
        ];
    }
}

internal sealed record ResolvedColumnSupersession(
    ColumnSupersession Declaration,
    ColumnSupersessionState AppliedState);
