using System.Collections.Concurrent;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Startup drift admission shared by the relational providers. Every storage unit is compared
/// read-only against the deployed catalog before its first session on a connection: column drift
/// is fatal (GW-RUNTIME-001) while index drift degrades. The verdict is cached per unit for the
/// connection lifetime — including the no-applied-state outcome — and <see cref="Invalidate"/>
/// clears one unit after a schema apply so the next session open re-verifies the catalog.
/// </summary>
public sealed class RelationalRuntimeAdmission
{
    private readonly string observerOperation;
    private readonly Func<StorageUnit, PhysicalSchemaTarget> createTarget;
    private readonly Func<PhysicalSchemaTarget, PhysicalSchemaInspectionResult> inspect;
    private readonly ConcurrentDictionary<StorageUnitId, Admission> admitted = new();

    private sealed record Admission(StorageUnit Desired, string TargetFingerprint);

    public RelationalRuntimeAdmission(
        string observerOperation,
        Func<StorageUnit, PhysicalSchemaTarget> createTarget,
        Func<PhysicalSchemaTarget, PhysicalSchemaInspectionResult> inspect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observerOperation);
        this.observerOperation = observerOperation;
        this.createTarget = createTarget ?? throw new ArgumentNullException(nameof(createTarget));
        this.inspect = inspect ?? throw new ArgumentNullException(nameof(inspect));
    }

    public void EnsureAdmitted(StorageUnit desired, IProviderCommandObserver? observer)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (admitted.TryGetValue(desired.Id, out var existing) && ReferenceEquals(existing.Desired, desired))
            return;
        var target = createTarget(desired);
        if (existing is not null &&
            string.Equals(existing.TargetFingerprint, target.Fingerprint, StringComparison.Ordinal))
        {
            admitted[desired.Id] = new Admission(desired, target.Fingerprint);
            return;
        }
        var inspection = inspect(target);
        observer?.Observe(new ProviderCommandEvent(
            observerOperation,
            $"Runtime schema admission inspection for '{target.Subject.Name}'",
            ProviderCommandKind.Read,
            IsProbe: false));
        var applied = inspection.History.AppliedState;
        if (applied is not null &&
            (!string.Equals(applied.TargetFingerprint, target.Fingerprint, StringComparison.Ordinal) ||
             !inspection.IsAppliedSchemaValid || inspection.HasColumnDrift))
        {
            throw new InvalidOperationException(
                $"GW-RUNTIME-001: Storage unit '{desired.Name}' has physical schema drift. Apply the exact schema before opening a session." +
                (inspection.ColumnDrift.IsDefaultOrEmpty
                    ? string.Empty
                    : " " + string.Join(" ", inspection.ColumnDrift.Select(refusal => $"{refusal.Code} at {refusal.Path}: {refusal.Message}"))));
        }
        admitted[desired.Id] = new Admission(desired, target.Fingerprint);
    }

    public void Invalidate(StorageUnitId id) => admitted.TryRemove(id, out _);
}
