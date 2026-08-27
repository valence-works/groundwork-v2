using System.Collections.Immutable;

namespace Groundwork.Kernel.Schema;

/// <summary>
/// Compares a deployed catalog against one exact compiled target, consulting no history.
/// </summary>
/// <remarks>
/// This is a different question from <see cref="IPhysicalSchemaHistoryInspector"/>, which compares
/// the catalog to the shape history says was applied and therefore has nothing to compare when
/// history is empty — the very case adoption exists for. The application lock is part of the
/// signature because the answer is only worth anything under the lock that will publish it: a
/// catalog proved to match outside the lock can stop matching before the claim is written.
/// </remarks>
public interface IPhysicalSchemaCatalogInspector
{
    PhysicalSchemaInspectionResult InspectDeployedCatalog(
        PhysicalSchemaTarget target,
        IPhysicalSchemaApplicationLock applicationLock);
}

public enum PhysicalSchemaAdoptionOutcome
{
    /// <summary>The catalog was proved to match the target and applied state was published.</summary>
    Adopted,

    /// <summary>History already records this exact target; there was nothing to adopt.</summary>
    AlreadyAdopted,

    /// <summary>The catalog does not match the target, or adoption does not apply here.</summary>
    Refused,

    /// <summary>The adoption plan was not authorized.</summary>
    AuthorizationRequired
}

public sealed class PhysicalSchemaAdoptionResult
{
    private PhysicalSchemaAdoptionResult(
        PhysicalSchemaAdoptionOutcome outcome,
        PhysicalSchemaDiffPlan plan,
        PhysicalSchemaAppliedState? appliedState,
        ImmutableArray<SchemaRefusal> refusals,
        ImmutableArray<SchemaRefusal> toleratedDrift)
    {
        Outcome = outcome;
        Plan = plan;
        AppliedState = appliedState;
        Refusals = refusals;
        ToleratedDrift = toleratedDrift;
    }

    public PhysicalSchemaAdoptionOutcome Outcome { get; }

    public PhysicalSchemaDiffPlan Plan { get; }

    /// <summary>
    /// The published applied state. Non-null exactly when the outcome is
    /// <see cref="PhysicalSchemaAdoptionOutcome.Adopted"/> or
    /// <see cref="PhysicalSchemaAdoptionOutcome.AlreadyAdopted"/>.
    /// </summary>
    public PhysicalSchemaAppliedState? AppliedState { get; }

    /// <summary>Every reason adoption refused, each naming what differs.</summary>
    public ImmutableArray<SchemaRefusal> Refusals { get; }

    /// <summary>Foreign columns the declaration's policy tolerated, reported rather than hidden.</summary>
    public ImmutableArray<SchemaRefusal> ToleratedDrift { get; }

    internal static PhysicalSchemaAdoptionResult Adopted(
        PhysicalSchemaDiffPlan plan,
        PhysicalSchemaAppliedState state,
        ImmutableArray<SchemaRefusal> toleratedDrift) =>
        new(PhysicalSchemaAdoptionOutcome.Adopted, plan, state, [], toleratedDrift);

    internal static PhysicalSchemaAdoptionResult AlreadyAdopted(
        PhysicalSchemaDiffPlan plan,
        PhysicalSchemaAppliedState state) =>
        new(PhysicalSchemaAdoptionOutcome.AlreadyAdopted, plan, state, [], []);

    internal static PhysicalSchemaAdoptionResult Refused(
        PhysicalSchemaDiffPlan plan,
        IEnumerable<SchemaRefusal> refusals,
        ImmutableArray<SchemaRefusal> toleratedDrift = default) =>
        new(
            PhysicalSchemaAdoptionOutcome.Refused,
            plan,
            null,
            [.. refusals],
            toleratedDrift.IsDefault ? [] : toleratedDrift);

    internal static PhysicalSchemaAdoptionResult AuthorizationRequired(
        PhysicalSchemaDiffPlan plan,
        ImmutableArray<SchemaRefusal> refusals) =>
        new(PhysicalSchemaAdoptionOutcome.AuthorizationRequired, plan, null, refusals, []);
}

/// <summary>
/// Records that a catalog Groundwork never applied is nevertheless exactly what applying the
/// compiled target would have produced.
/// </summary>
/// <remarks>
/// <para>
/// This is the opposite of the inference <c>GW-SCHEMA-001</c> refuses. Nothing here reads a
/// deployed column and decides what it probably corresponds to: the target comes from the
/// declaration, the catalog is compared to it in full, and any difference is a refusal that names
/// what differs. Arbitrary legacy-schema mapping stays out of scope.
/// </para>
/// <para>
/// The published row is not assembled here. It is produced by <see cref="PhysicalSchemaDiffPlan.Complete"/>
/// from the same plan a real apply would have run, acknowledging every operation at its own planned
/// fingerprint — so an adopted row and an applied row are the same value, and the next diff against
/// this target reasons from the same premise either way.
/// </para>
/// </remarks>
public static class PhysicalSchemaAdoption
{
    /// <summary>History already holds an applied state, so there is nothing to adopt.</summary>
    public const string ExistingHistoryCode = "GW-SCHEMA-011";

    /// <summary>A retired subject declares no catalog, so adoption has nothing to verify.</summary>
    public const string RetiredSubjectCode = "GW-SCHEMA-012";

    /// <summary>The provider reported the catalog invalid without naming what differs.</summary>
    public const string UnnamedDriftCode = "GW-SCHEMA-013";

    public static PhysicalSchemaAdoptionResult Adopt(
        PhysicalSchemaTarget target,
        IPhysicalSchemaExecutor executor,
        DateTimeOffset? now = null,
        Func<PhysicalSchemaDiffPlan, PhysicalSchemaPlanAuthorization>? planAuthorization = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(executor);
        if (executor is not IPhysicalSchemaCatalogInspector catalogInspector)
        {
            throw new ArgumentException(
                "Adoption requires a provider that can compare a deployed catalog to an exact target.",
                nameof(executor));
        }

        var plannedAt = now ?? DateTimeOffset.UtcNow;
        using var applicationLock = executor.AcquireApplicationLock(target.Identity);
        if (applicationLock.Target != target.Identity)
        {
            throw new InvalidOperationException(
                $"Executor returned lock '{applicationLock.Target}' for requested target '{target.Identity}'.");
        }

        var history = executor.ReadHistory(target.Identity, applicationLock);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, history, plannedAt);
        // The planner's own refusals come first, so legacy history and a mismatched identity keep
        // reporting GW-SCHEMA-001 and GW-SCHEMA-002 rather than being restated as an adoption
        // problem they are not.
        if (!plan.IsApplicable)
            return PhysicalSchemaAdoptionResult.Refused(plan, plan.Refusals);

        if (history.AppliedState is { } existing)
        {
            // Adoption writes the first applied state for a catalog. Overwriting one that a real
            // apply published would replace evidence with a claim, so this refuses and names the
            // recorded fingerprint rather than trying to reconcile the two.
            return plan.Operations.Length == 0
                ? PhysicalSchemaAdoptionResult.AlreadyAdopted(plan, existing)
                : PhysicalSchemaAdoptionResult.Refused(plan, [new SchemaRefusal(
                    ExistingHistoryCode,
                    $"Target '{target.Identity}' already has applied schema history at fingerprint " +
                    $"'{existing.TargetFingerprint}'. Adoption records a catalog Groundwork has never " +
                    "applied; apply the pending plan instead.",
                    "schemaHistory")]);
        }

        if (target.Subject.Evolution.RetiresPrimaryStorage)
        {
            // A retired subject's catalog is meant to be gone, so there is nothing to compare and
            // adopting it would publish an empty ledger on no evidence at all.
            return PhysicalSchemaAdoptionResult.Refused(plan, [new SchemaRefusal(
                RetiredSubjectCode,
                $"Subject '{target.Subject.Name}' is declared retired, so it describes no catalog to " +
                "verify and cannot be adopted. Apply the retirement instead.",
                "schema.evolution.retired")]);
        }

        var authorization = planAuthorization?.Invoke(plan) ?? PhysicalSchemaPlanAuthorization.Allow;
        if (!authorization.IsAuthorized)
            return PhysicalSchemaAdoptionResult.AuthorizationRequired(plan, authorization.Refusals);

        var inspection = catalogInspector.InspectDeployedCatalog(target, applicationLock);
        if (Evidence.Prove(plan, inspection) is not { } evidence)
        {
            return PhysicalSchemaAdoptionResult.Refused(
                plan,
                NameDrift(target, inspection),
                inspection.ToleratedDrift);
        }

        var adopted = evidence.Synthesize(now ?? DateTimeOffset.UtcNow);
        executor.PublishAppliedState(adopted, plan.ExpectedAppliedTargetFingerprint, applicationLock);
        return PhysicalSchemaAdoptionResult.Adopted(plan, adopted, inspection.ToleratedDrift);
    }

    private static ImmutableArray<SchemaRefusal> NameDrift(
        PhysicalSchemaTarget target,
        PhysicalSchemaInspectionResult inspection)
    {
        var named = (inspection.ColumnDrift.IsDefault ? [] : inspection.ColumnDrift)
            .Concat(inspection.IndexDrift.IsDefault ? [] : inspection.IndexDrift)
            .ToImmutableArray();
        return named.Length != 0
            ? named
            : [new SchemaRefusal(
                UnnamedDriftCode,
                $"The provider reported the deployed catalog for '{target.Subject.Name}' invalid without " +
                "naming what differs, so adoption refuses rather than recording an unproved claim.",
                "table")];
    }

    /// <summary>
    /// Proof that one deployed catalog is exactly one compiled target, and the only thing in this
    /// assembly that can turn a plan into an adopted applied state. The constructor is private and
    /// <see cref="Prove"/> is the only factory; it returns nothing when the inspection carries any
    /// drift, so "publish without a proof" has no spelling rather than being a check somebody must
    /// remember to write.
    /// </summary>
    private sealed class Evidence
    {
        private readonly PhysicalSchemaDiffPlan plan;

        private Evidence(PhysicalSchemaDiffPlan plan) => this.plan = plan;

        internal static Evidence? Prove(
            PhysicalSchemaDiffPlan plan,
            PhysicalSchemaInspectionResult inspection) =>
            inspection.IsAppliedSchemaValid && !inspection.HasColumnDrift && !inspection.HasIndexDrift
                ? new Evidence(plan)
                : null;

        /// <summary>
        /// The applied state a real apply of this plan would have published. Every operation is
        /// acknowledged at its own planned fingerprint and the ledger is derived by
        /// <see cref="PhysicalSchemaDiffPlan.Complete"/> — the same call the applier makes from the
        /// same plan — rather than reassembled here, which is what keeps the two indistinguishable.
        /// </summary>
        internal PhysicalSchemaAppliedState Synthesize(DateTimeOffset adoptedAt) => plan.Complete(
            [.. plan.Operations
                .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
                .Select(operation => new PhysicalSchemaOperationAcknowledgement(
                    operation.Identity,
                    operation.Fingerprint,
                    adoptedAt))],
            adoptedAt);
    }
}
