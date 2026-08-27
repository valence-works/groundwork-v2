using System.Collections.Concurrent;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Startup drift admission shared by the relational providers. Every storage unit is compared
/// read-only against the deployed catalog before its first session on a connection: column drift
/// is fatal (GW-RUNTIME-001) while index drift degrades. The verdict is cached per unit for the
/// connection lifetime — including the no-applied-state outcome. <see cref="Invalidate"/> bumps a
/// per-unit invalidation stamp after a schema apply; every cached verdict carries the stamp it was
/// inspected under and is ignored once the stamp moves, so an inspection that raced an apply can
/// never satisfy a later session open.
/// </summary>
public sealed class RelationalRuntimeAdmission
{
    private readonly string observerOperation;
    private readonly Func<StorageUnit, PhysicalSchemaTarget> createTarget;
    private readonly Func<PhysicalSchemaTarget, PhysicalSchemaInspectionResult> inspect;
    private readonly ConcurrentDictionary<StorageUnitId, Admission> admitted = new();
    private readonly ConcurrentDictionary<StorageUnitId, long> invalidations = new();

    private sealed record Admission(StorageUnit Desired, string TargetFingerprint, long Stamp);

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
        var stamp = invalidations.GetValueOrDefault(desired.Id);
        if (admitted.TryGetValue(desired.Id, out var existing) && existing.Stamp != stamp)
            existing = null;
        if (existing is not null && ReferenceEquals(existing.Desired, desired))
            return;
        var target = createTarget(desired);
        if (existing is not null &&
            string.Equals(existing.TargetFingerprint, target.Fingerprint, StringComparison.Ordinal))
        {
            admitted[desired.Id] = new Admission(desired, target.Fingerprint, stamp);
            return;
        }
        var inspection = inspect(target);
        observer?.Observe(new ProviderCommandEvent(
            observerOperation,
            $"Runtime schema admission inspection for '{target.Subject.Name}'",
            ProviderCommandKind.Read,
            IsProbe: false));
        var applied = inspection.History.AppliedState;
        if (applied is not null)
        {
            var fingerprintMismatch = !string.Equals(applied.TargetFingerprint, target.Fingerprint, StringComparison.Ordinal);
            if (fingerprintMismatch || !inspection.IsAppliedSchemaValid || inspection.HasColumnDrift)
            {
                var remedy = fingerprintMismatch
                    ? "Apply the declared schema before opening a session."
                    : "Restore the deployed catalog to the applied schema — rebuild derived search-key columns rather than reapplying — before opening a session.";
                throw new InvalidOperationException(
                    $"GW-RUNTIME-001: Storage unit '{desired.Name}' has physical schema drift. " + remedy +
                    (inspection.ColumnDrift.IsDefaultOrEmpty
                        ? string.Empty
                        : " " + string.Join(" ", inspection.ColumnDrift.Select(refusal => $"{refusal.Code} at {refusal.Path}: {refusal.Message}"))));
            }
        }
        admitted[desired.Id] = new Admission(desired, target.Fingerprint, stamp);
    }

    public void Invalidate(StorageUnitId id)
    {
        invalidations.AddOrUpdate(id, 1, static (_, stamp) => stamp + 1);
        admitted.TryRemove(id, out _);
    }
}
