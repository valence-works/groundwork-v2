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
    bool IsAppliedSchemaValid);

public enum PhysicalSchemaApplicationOutcome
{
    Applied,
    NoChanges,
    Rejected,
    AuthorizationRequired
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
}

/// <summary>Coordinates an exact plan with CAS-recorded provider history.</summary>
public static class PhysicalSchemaApplication
{
    public static PhysicalSchemaApplicationResult Apply(
        PhysicalSchemaTarget target,
        IPhysicalSchemaExecutor executor,
        DateTimeOffset? now = null,
        Func<PhysicalSchemaDiffPlan, PhysicalSchemaPlanAuthorization>? planAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(executor);
        var plannedAt = now ?? DateTimeOffset.UtcNow;
        using var applicationLock = executor.AcquireApplicationLock(target.Identity);
        if (applicationLock.Target != target.Identity)
            throw new InvalidOperationException(
                $"Executor returned lock '{applicationLock.Target}' for requested target '{target.Identity}'.");

        var history = executor.ReadHistory(target.Identity, applicationLock);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, history, plannedAt);
        if (!plan.IsApplicable)
            return new(PhysicalSchemaApplicationOutcome.Rejected, plan, history.AppliedState);

        var authorization = planAuthorization?.Invoke(plan) ?? PhysicalSchemaPlanAuthorization.Allow;
        if (!authorization.IsAuthorized)
        {
            return new(PhysicalSchemaApplicationOutcome.AuthorizationRequired, plan, history.AppliedState)
            {
                AuthorizationRefusals = authorization.Refusals
            };
        }

        if (plan.Operations.Length == 0)
        {
            var validation = new ValidatePhysicalSchemaOperation(target);
            var acknowledgement = executor.ApplyOperation(target.Identity, validation, applicationLock);
            EnsureAcknowledges(validation, acknowledgement);
            return new(PhysicalSchemaApplicationOutcome.NoChanges, plan, history.AppliedState);
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
        return new(PhysicalSchemaApplicationOutcome.Applied, plan, applied);
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

public sealed record GroundworkRuntimeSchemaAdmissionResult(
    PhysicalSchemaInspectionResult Inspection,
    PhysicalSchemaDiffPlan Plan,
    PhysicalSchemaApplicationResult? Application = null)
{
    public bool IsReady =>
        Inspection.IsAppliedSchemaValid &&
        ((Plan.IsApplicable && Plan.Operations.Length == 0) ||
         Application?.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.NoChanges);

    public ImmutableArray<PhysicalSchemaOperation> PendingOperations => IsReady ? [] : Plan.Operations;

    public ImmutableArray<SchemaRefusal> Refusals =>
        Plan.Refusals.Concat(Application?.AuthorizationRefusals ?? []).ToImmutableArray();

    public int AppliedOperationCount =>
        Application?.Outcome == PhysicalSchemaApplicationOutcome.Applied
            ? Application.Plan.Operations.Count(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
            : 0;

    public GroundworkRuntimeSchemaAdmissionResult EnsureReady()
    {
        if (!IsReady)
            throw new GroundworkRuntimeSchemaAdmissionException(this);
        return this;
    }
}

public sealed class GroundworkRuntimeSchemaAdmissionException : InvalidOperationException
{
    public GroundworkRuntimeSchemaAdmissionException(GroundworkRuntimeSchemaAdmissionResult result)
        : base(CreateMessage(result)) => Result = result;

    public GroundworkRuntimeSchemaAdmissionResult Result { get; }

    private static string CreateMessage(GroundworkRuntimeSchemaAdmissionResult result)
    {
        var reason = !result.Inspection.IsAppliedSchemaValid
            ? "found drift in the applied schema"
            : result.Application?.Outcome == PhysicalSchemaApplicationOutcome.AuthorizationRequired
                ? "found pending operations that require explicit authorization"
                : "requires the exact target to be applied before startup can continue";
        var refusals = string.Join("; ", result.Refusals.Select(refusal =>
            $"{refusal.Code}: {refusal.Message}"));
        return $"Groundwork runtime schema admission {reason}." +
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
        Func<PhysicalSchemaDiffPlan, PhysicalSchemaPlanAuthorization>? authorization = null)
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
        if (executor is not IPhysicalSchemaHistoryInspector inspector)
            throw new ArgumentException(
                "Runtime schema admission requires a non-mutating history inspector.",
                nameof(executor));

        var inspection = inspector.InspectHistory(target);
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

            var refusals = protection.DestructiveOperationIdentities
                .Select(identity => new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Startup auto-apply requires explicit authorization for destructive operation '{identity}'.",
                    "runtime-schema-admission"))
                .Concat(protection.SemanticMigrationIdentities.Select(identity => new SchemaRefusal(
                    "GW-RUNTIME-002",
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
