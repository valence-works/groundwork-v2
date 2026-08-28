using System.Collections.Immutable;
using System.Diagnostics;

namespace Groundwork.Kernel.Schema;

/// <summary>Provider execution boundary for one exact schema target.</summary>
public interface IPhysicalSchemaExecutor
{
    IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target);

    PhysicalSchemaHistoryState ReadHistory(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock);

    PhysicalSchemaOperationAcknowledgement ApplyOperation(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        IPhysicalSchemaApplicationLock applicationLock);

    /// <summary>
    /// Applies one exact ordered batch. Providers may override this default to use one durable
    /// transaction while preserving one acknowledgement per operation.
    /// </summary>
    IReadOnlyList<PhysicalSchemaOperationAcknowledgement> ApplyOperationBatch(
        PhysicalSchemaTargetIdentity target,
        IReadOnlyList<PhysicalSchemaOperation> operations,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations.Select(operation => ApplyOperation(target, operation, applicationLock)).ToArray();
    }

    void PublishAppliedState(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock);
}

/// <summary>Optional non-mutating inspection seam used by runtime admission.</summary>
public interface IPhysicalSchemaHistoryInspector
{
    PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target);
}

public interface IPhysicalSchemaApplicationLock : IDisposable
{
    PhysicalSchemaTargetIdentity Target { get; }
}

public sealed record PhysicalSchemaInspectionResult(
    PhysicalSchemaHistoryState History,
    bool IsAppliedSchemaValid,
    ImmutableArray<SchemaRefusal> ColumnDrift = default,
    ImmutableArray<SchemaRefusal> IndexDrift = default)
{
    /// <summary>
    /// Drift a declaration's opt-in <see cref="ForeignColumnPolicy"/> downgraded from a refusal to a
    /// warning. It is kept apart from <see cref="ColumnDrift"/> rather than dropped, so tolerating a
    /// foreign column still names it everywhere drift is reported.
    /// </summary>
    public ImmutableArray<SchemaRefusal> ToleratedDrift { get; init; } = [];

    public bool HasColumnDrift => !ColumnDrift.IsDefaultOrEmpty;

    public bool HasIndexDrift => !IndexDrift.IsDefaultOrEmpty;

    public bool HasToleratedDrift => !ToleratedDrift.IsDefaultOrEmpty;
}

public enum PhysicalSchemaApplicationOutcome
{
    Applied,
    NoChanges,
    Rejected,
    AuthorizationRequired,

    /// <summary>
    /// The exact target was applied and published, but a data migration attached to its semantic
    /// migration identity stopped with rows left. The ledger carries the resume cursor; the target
    /// is not migrated until a further pass completes it.
    /// </summary>
    DataMigrationIncomplete
}

public sealed record PhysicalSchemaPlanAuthorization(
    bool IsAuthorized,
    ImmutableArray<SchemaRefusal> Refusals)
{
    public static PhysicalSchemaPlanAuthorization Allow { get; } = new(true, []);

    public static PhysicalSchemaPlanAuthorization Deny(IEnumerable<SchemaRefusal> refusals) =>
        new(false, (refusals ?? throw new ArgumentNullException(nameof(refusals))).ToImmutableArray());
}

public sealed record PhysicalSchemaApplicationResult(
    PhysicalSchemaApplicationOutcome Outcome,
    PhysicalSchemaDiffPlan Plan,
    PhysicalSchemaAppliedState? AppliedState)
{
    public ImmutableArray<SchemaRefusal> AuthorizationRefusals { get; init; } = [];

    /// <summary>Evidence for every data migration attached to this target's semantic migration.</summary>
    public ImmutableArray<DataMigrationRunResult> DataMigrations { get; init; } = [];

    /// <summary>
    /// The contract readiness established for this application, or null when it planned the expand
    /// phase. It is what a refused contract reports, and what an accepted one was admitted by.
    /// </summary>
    public ContractReadinessAssessment? ContractReadiness { get; init; }
}

/// <summary>Coordinates an exact plan with CAS-recorded provider history.</summary>
public static class PhysicalSchemaApplication
{
    /// <summary>
    /// Applies everything the plan contains except work that destroys data re-applying cannot
    /// restore, which is refused by name. This is what an unauthenticated convenience apply — a
    /// provider's <c>Schema.Apply</c> — performs, so that path cannot quietly drop a column.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The plan contains irrecoverable work. The message carries <c>GW-SCHEMA-010</c> and names
    /// every operation, so a caller learns what to authorize rather than seeing a silent no-op.
    /// </exception>
    public static PhysicalSchemaApplicationResult ApplyRecoverableWork(
        PhysicalSchemaTarget target,
        IPhysicalSchemaExecutor executor,
        DateTimeOffset? now = null)
    {
        var result = Apply(target, executor, now, PhysicalSchemaPlanProtection.RefuseIrrecoverableWork);
        if (result.Outcome != PhysicalSchemaApplicationOutcome.AuthorizationRequired)
            return result;
        throw new InvalidOperationException(string.Join(
            Environment.NewLine,
            result.AuthorizationRefusals.Select(refusal => $"{refusal.Code} at {refusal.Path}: {refusal.Message}")));
    }

    public static PhysicalSchemaApplicationResult Apply(
        PhysicalSchemaTarget target,
        IPhysicalSchemaExecutor executor,
        DateTimeOffset? now = null,
        Func<PhysicalSchemaDiffPlan, PhysicalSchemaPlanAuthorization>? planAuthorization = null,
        DataMigrationCatalog? dataMigrations = null,
        DataMigrationBudget? dataMigrationBudget = null,
        IProgress<DataMigrationProgress>? dataMigrationProgress = null,
        SchemaEvolutionPhase phase = SchemaEvolutionPhase.Expand,
        IDataMigrationExecutor? dataMigrationExecutor = null) =>
        ApplyCore(
            target,
            executor,
            now,
            planAuthorization,
            dataMigrations,
            dataMigrationBudget,
            dataMigrationProgress,
            phase,
            dataMigrationExecutor,
            DataMigrationExecution.Synchronous).GetAwaiter().GetResult();

    /// <summary>
    /// The asynchronous counterpart. Schema operations run on <see cref="IPhysicalSchemaExecutor"/>,
    /// which declares one surface; the data-migration phase runs on the provider's asynchronous
    /// <see cref="IDataMigrationExecutor"/> surface and observes the token.
    /// </summary>
    public static ValueTask<PhysicalSchemaApplicationResult> ApplyAsync(
        PhysicalSchemaTarget target,
        IPhysicalSchemaExecutor executor,
        DateTimeOffset? now = null,
        Func<PhysicalSchemaDiffPlan, PhysicalSchemaPlanAuthorization>? planAuthorization = null,
        DataMigrationCatalog? dataMigrations = null,
        DataMigrationBudget? dataMigrationBudget = null,
        IProgress<DataMigrationProgress>? dataMigrationProgress = null,
        SchemaEvolutionPhase phase = SchemaEvolutionPhase.Expand,
        IDataMigrationExecutor? dataMigrationExecutor = null,
        CancellationToken cancellationToken = default) =>
        ApplyCore(
            target,
            executor,
            now,
            planAuthorization,
            dataMigrations,
            dataMigrationBudget,
            dataMigrationProgress,
            phase,
            dataMigrationExecutor,
            DataMigrationExecution.Asynchronous(cancellationToken));

    private static async ValueTask<PhysicalSchemaApplicationResult> ApplyCore(
        PhysicalSchemaTarget target,
        IPhysicalSchemaExecutor executor,
        DateTimeOffset? now,
        Func<PhysicalSchemaDiffPlan, PhysicalSchemaPlanAuthorization>? planAuthorization,
        DataMigrationCatalog? dataMigrations,
        DataMigrationBudget? dataMigrationBudget,
        IProgress<DataMigrationProgress>? dataMigrationProgress,
        SchemaEvolutionPhase phase,
        IDataMigrationExecutor? dataMigrationExecutor,
        DataMigrationExecution mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(executor);
        mode.CancellationToken.ThrowIfCancellationRequested();
        var plannedAt = now ?? DateTimeOffset.UtcNow;
        using var applicationLock = executor.AcquireApplicationLock(target.Identity);
        if (applicationLock.Target != target.Identity)
            throw new InvalidOperationException(
                $"Executor returned lock '{applicationLock.Target}' for requested target '{target.Identity}'.");

        var history = executor.ReadHistory(target.Identity, applicationLock);
        // One lookup decides where the data-migration ledger comes from, and both the contract gate
        // and the migration phase below use it. A provider whose schema executor and migration
        // executor are two objects passes the second one in; most pass one object that is both.
        var migrationExecutor = dataMigrationExecutor ?? executor as IDataMigrationExecutor;
        // A contract plan establishes its readiness here, inside the same lock and from the same
        // history it is planned against, so nothing can change between the gate and the removal.
        var readiness = phase == SchemaEvolutionPhase.Contract
            ? ExpandContractWorkflow.AssessContractReadiness(
                target,
                history,
                migrationExecutor is null
                    ? null
                    : await mode.ReadLedgerEntries(migrationExecutor, target.Identity).ConfigureAwait(false),
                now ?? plannedAt)
            : null;
        var plan = PhysicalSchemaDiffPlanner.Plan(target, history, plannedAt, phase: phase, readiness: readiness);
        var supersessions = ColumnSupersessionPlan.Resolve(target, history.AppliedState);
        if (!plan.IsApplicable)
            return new(PhysicalSchemaApplicationOutcome.Rejected, plan, history.AppliedState)
            {
                ContractReadiness = readiness
            };

        var authorization = planAuthorization?.Invoke(plan) ?? PhysicalSchemaPlanAuthorization.Allow;
        if (!authorization.IsAuthorized)
        {
            return new(PhysicalSchemaApplicationOutcome.AuthorizationRequired, plan, history.AppliedState)
            {
                AuthorizationRefusals = authorization.Refusals,
                ContractReadiness = readiness
            };
        }

        if (plan.Operations.Length == 0)
        {
            var validation = new ValidatePhysicalSchemaOperation(target);
            var acknowledgement = executor.ApplyOperation(target.Identity, validation, applicationLock);
            EnsureAcknowledges(validation, acknowledgement);
            // A target whose schema is already applied can still owe a data migration that an
            // earlier pass left running, so the resume runs here too rather than only after DDL.
            var resumed = await RunDataMigrations(
                target, migrationExecutor, supersessions, dataMigrations, dataMigrationBudget, dataMigrationProgress, now, mode)
                .ConfigureAwait(false);
            return new(
                resumed.Any(result => !result.IsComplete)
                    ? PhysicalSchemaApplicationOutcome.DataMigrationIncomplete
                    : PhysicalSchemaApplicationOutcome.NoChanges,
                plan,
                history.AppliedState)
            {
                DataMigrations = resumed,
                ContractReadiness = readiness
            };
        }

        var operations = plan.Operations
            .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
            .ToArray();
        var acknowledgements = executor.ApplyOperationBatch(target.Identity, operations, applicationLock).ToArray();
        if (acknowledgements.Length != operations.Length)
            throw new InvalidOperationException("The executor did not acknowledge every schema operation.");
        for (var index = 0; index < operations.Length; index++)
            EnsureAcknowledges(operations[index], acknowledgements[index]);

        var applied = plan.Complete(acknowledgements, now ?? DateTimeOffset.UtcNow);
        executor.PublishAppliedState(applied, plan.ExpectedAppliedTargetFingerprint, applicationLock);
        // The DDL is durably applied, so applied state is published before the data phase: replaying
        // CREATE TABLE or ADD COLUMN is not idempotent, while the data-migration ledger is. An
        // unfinished data migration is reported by the outcome and by that ledger, not by pretending
        // the schema was never applied.
        var migrations = await RunDataMigrations(
            target, migrationExecutor, supersessions, dataMigrations, dataMigrationBudget, dataMigrationProgress, now, mode)
            .ConfigureAwait(false);
        return new(
            migrations.Any(result => !result.IsComplete)
                ? PhysicalSchemaApplicationOutcome.DataMigrationIncomplete
                : PhysicalSchemaApplicationOutcome.Applied,
            plan,
            applied)
        {
            DataMigrations = migrations,
            ContractReadiness = readiness
        };
    }

    /// <summary>
    /// Runs every transform attached to this target's semantic migration identity. It runs inside the
    /// same authorized application, so a data migration is executed under exactly the authorization
    /// that admitted its semantic schema change.
    /// </summary>
    private static async ValueTask<ImmutableArray<DataMigrationRunResult>> RunDataMigrations(
        PhysicalSchemaTarget target,
        IDataMigrationExecutor? migrationExecutor,
        ColumnSupersessionPlan supersessions,
        DataMigrationCatalog? catalog,
        DataMigrationBudget? budget,
        IProgress<DataMigrationProgress>? progress,
        DateTimeOffset? now,
        DataMigrationExecution mode)
    {
        if (catalog is null ||
            !catalog.TryGet(target.Subject.Evolution.SemanticMigrationId, target.Subject.Id, out var migration))
        {
            return [];
        }

        if (migrationExecutor is null)
        {
            throw new DataMigrationRefusedException(
                DataMigrationCodes.NotSupported,
                $"semantic migration '{migration.Id}' attaches a data transform, but provider " +
                $"'{target.Provider.Name}' offers no data-migration execution.");
        }

        // The backfill of an expand–contract evolution reads the superseded column, which the
        // declaration deliberately no longer declares. It is not an undeclared column to the
        // migration — the supersession declares it, in full — so it is carried into the unit the
        // migration runs against and typed exactly like every other column.
        var unit = MigrationUnit(target, supersessions);
        var result = await (mode.IsAsync
            ? DataMigrationRunner.RunAsync(
                migrationExecutor, target.Identity, unit, migration,
                budget, now, progress, mode.CancellationToken)
            : new ValueTask<DataMigrationRunResult>(DataMigrationRunner.Run(
                migrationExecutor, target.Identity, unit, migration,
                budget, now, progress))).ConfigureAwait(false);
        return [result];
    }

    private static StorageUnit MigrationUnit(
        PhysicalSchemaTarget target,
        ColumnSupersessionPlan supersessions)
    {
        var unit = target.Subject.Definition;
        var retained = supersessions.RetainedColumns;
        return retained.IsEmpty ? unit : unit with { Columns = [.. unit.Columns, .. retained] };
    }

    private static void EnsureAcknowledges(
        PhysicalSchemaOperation operation,
        PhysicalSchemaOperationAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (!string.Equals(acknowledgement.Identity, operation.Identity, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Executor acknowledged operation '{acknowledgement.Identity}' while '{operation.Identity}' was expected.");
        if (!string.Equals(acknowledgement.Fingerprint, operation.Fingerprint, StringComparison.Ordinal))
            throw new PhysicalSchemaFingerprintConflictException(
                operation.Identity,
                operation.Fingerprint,
                acknowledgement.Fingerprint);
    }
}

public sealed class GroundworkRuntimeSchemaAdmissionOptions
{
    public bool AutoApplyOnStartup { get; set; }
}

public enum GroundworkRuntimeSchemaAdmissionLogLevel
{
    Information,
    Warning
}

public sealed record GroundworkRuntimeSchemaAdmissionLogEntry(
    GroundworkRuntimeSchemaAdmissionLogLevel Level,
    string Message);

/// <summary>Whether a runtime schema can serve the declared storage unit.</summary>
public enum GroundworkRuntimeSchemaAdmissionStatus
{
    /// <summary>The declaration is serviceable as deployed.</summary>
    Ready,

    /// <summary>The application can serve, but one or more dependent shapes may be unavailable.</summary>
    Degraded,

    /// <summary>The declaration cannot safely serve until physical schema work is completed.</summary>
    Blocked
}

public sealed record GroundworkRuntimeSchemaAdmissionResult(
    PhysicalSchemaInspectionResult Inspection,
    PhysicalSchemaDiffPlan Plan,
    PhysicalSchemaApplicationResult? Application = null)
{
    public bool IsReady =>
        Inspection.IsAppliedSchemaValid &&
        !Inspection.HasColumnDrift &&
        ((Plan.IsApplicable && Plan.Operations.Length == 0) ||
         Application?.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.NoChanges);

    public ImmutableArray<PhysicalSchemaOperation> PendingOperations => IsReady ? [] : Plan.Operations;

    /// <summary>
    /// Foreign-column drift the declaration's opt-in policy downgraded to a warning. Reported
    /// alongside <see cref="Refusals"/> and never merged into it: these do not block startup, and
    /// a caller that treated them as refusals would undo the opt-in.
    /// </summary>
    public ImmutableArray<SchemaRefusal> Warnings =>
        Inspection.ToleratedDrift.IsDefault ? [] : Inspection.ToleratedDrift;

    public ImmutableArray<SchemaRefusal> Refusals =>
        (Inspection.ColumnDrift.IsDefault ? [] : Inspection.ColumnDrift)
            .Concat(Inspection.IndexDrift.IsDefault ? [] : Inspection.IndexDrift)
            .Concat(Plan.Refusals)
            .Concat(Application?.AuthorizationRefusals ?? [])
            .ToImmutableArray();

    public int AppliedOperationCount =>
        Application?.Outcome == PhysicalSchemaApplicationOutcome.Applied
            ? Application.Plan.Operations.Count(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
            : 0;

    /// <summary>
    /// The kernel's hosting-neutral serviceability verdict. Physical index drift is degrading
    /// because dependent query shapes can refuse while the rest of the application serves; any
    /// result that is not runtime-ready is blocked. Safe auto-apply can turn a previously pending
    /// plan back into <see cref="GroundworkRuntimeSchemaAdmissionStatus.Ready"/>.
    /// </summary>
    public GroundworkRuntimeSchemaAdmissionStatus Status
    {
        get
        {
            if (IsReady)
                return Inspection.HasIndexDrift
                    ? GroundworkRuntimeSchemaAdmissionStatus.Degraded
                    : GroundworkRuntimeSchemaAdmissionStatus.Ready;

            return GroundworkRuntimeSchemaAdmissionStatus.Blocked;
        }
    }

    public GroundworkRuntimeSchemaAdmissionResult EnsureReady()
    {
        if (!IsReady)
            throw new GroundworkRuntimeSchemaAdmissionException(this);
        return this;
    }
}

public sealed class GroundworkRuntimeSchemaAdmissionException : InvalidOperationException
{
    public GroundworkRuntimeSchemaAdmissionException(GroundworkRuntimeSchemaAdmissionResult result, string? detail = null)
        : base(CreateMessage(result, detail)) => Result = result;

    public GroundworkRuntimeSchemaAdmissionResult Result { get; }

    private static string CreateMessage(GroundworkRuntimeSchemaAdmissionResult result, string? detail)
    {
        var reason = result.Inspection.HasColumnDrift || !result.Inspection.IsAppliedSchemaValid
            ? "found column drift in the applied schema"
            : result.Application?.Outcome == PhysicalSchemaApplicationOutcome.AuthorizationRequired
                ? "found pending operations that require explicit authorization"
                : "requires the exact target to be applied before startup can continue";
        var refusals = string.Join("; ", result.Refusals.Select(refusal =>
            $"{refusal.Code}: {refusal.Message}"));
        return $"Groundwork runtime schema admission {reason}." +
               (detail is null ? string.Empty : " " + detail) +
               (refusals.Length == 0 ? string.Empty : Environment.NewLine + refusals);
    }
}

/// <summary>Inspect-only-by-default runtime admission with opt-in safe auto-apply.</summary>
public static class GroundworkRuntimeSchemaAdmission
{
    public static GroundworkRuntimeSchemaAdmissionResult InspectRuntimeAdmission(
        IPhysicalSchemaExecutor executor,
        PhysicalSchemaTarget target,
        GroundworkRuntimeSchemaAdmissionOptions? options = null,
        Action<GroundworkRuntimeSchemaAdmissionLogEntry>? log = null,
        Func<PhysicalSchemaDiffPlan, PhysicalSchemaPlanAuthorization>? authorization = null,
        PhysicalSchemaInspectionResult? inspected = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(target);
        options ??= new GroundworkRuntimeSchemaAdmissionOptions();
        log ??= entry =>
        {
            if (entry.Level == GroundworkRuntimeSchemaAdmissionLogLevel.Information)
                Trace.TraceInformation("{0}", entry.Message);
            else
                Trace.TraceWarning("{0}", entry.Message);
        };
        if (inspected is null && executor is not IPhysicalSchemaHistoryInspector)
            throw new ArgumentException(
                "Runtime schema admission requires a non-mutating history inspector.",
                nameof(executor));

        var inspection = inspected ?? ((IPhysicalSchemaHistoryInspector)executor).InspectHistory(target);
        foreach (var tolerated in inspection.HasToleratedDrift ? inspection.ToleratedDrift : [])
        {
            log(new GroundworkRuntimeSchemaAdmissionLogEntry(
                GroundworkRuntimeSchemaAdmissionLogLevel.Warning,
                $"{tolerated.Code}: {tolerated.Message}"));
        }
        var plan = PhysicalSchemaDiffPlanner.Plan(target, inspection.History, DateTimeOffset.UtcNow);
        if (!options.AutoApplyOnStartup || !inspection.IsAppliedSchemaValid || !plan.IsApplicable || plan.Operations.Length == 0)
            return new GroundworkRuntimeSchemaAdmissionResult(inspection, plan);

        Func<PhysicalSchemaDiffPlan, PhysicalSchemaPlanAuthorization> safeAuthorization = currentPlan =>
        {
            var protection = PhysicalSchemaPlanProtection.Inspect(currentPlan.Operations);
            if (protection.IsSafe)
            {
                log(new GroundworkRuntimeSchemaAdmissionLogEntry(
                    GroundworkRuntimeSchemaAdmissionLogLevel.Information,
                    $"Groundwork runtime schema auto-apply is executing for {target.Identity}."));
                return authorization?.Invoke(currentPlan) ?? PhysicalSchemaPlanAuthorization.Allow;
            }

            // These name work that is planned and valid but unauthorized, which is a different
            // verdict from schema drift; GW-RUNTIME-002 means an index no longer matches its
            // declaration and would misreport this as a broken catalog.
            var refusals = protection.DestructiveOperations
                .Select(operation => new SchemaRefusal(
                    "GW-SCHEMA-007",
                    $"Startup auto-apply requires explicit authorization for destructive operation " +
                    $"'{operation.Address ?? operation.Identity}'.",
                    "runtime-schema-admission"))
                .Concat(protection.SemanticMigrationIdentities.Select(identity => new SchemaRefusal(
                    "GW-SCHEMA-008",
                    $"Startup auto-apply requires explicit authorization for semantic migration '{identity}'.",
                    "runtime-schema-admission")))
                .ToArray();
            log(new GroundworkRuntimeSchemaAdmissionLogEntry(
                GroundworkRuntimeSchemaAdmissionLogLevel.Warning,
                $"Groundwork runtime schema auto-apply was blocked for {target.Identity}."));
            return PhysicalSchemaPlanAuthorization.Deny(refusals);
        };

        var application = PhysicalSchemaApplication.Apply(target, executor, planAuthorization: safeAuthorization);
        return new GroundworkRuntimeSchemaAdmissionResult(inspection, application.Plan, application);
    }
}
