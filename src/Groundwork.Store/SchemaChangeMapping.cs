using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Store;

/// <summary>
/// The one mapping from planned physical schema operations to the public <see cref="SchemaChange"/>
/// vocabulary. Every provider coordinator uses it, so a new operation kind is described the same way
/// everywhere or not at all.
/// </summary>
/// <remarks>
/// The switch is deliberately total: an unmapped kind throws rather than falling into a default
/// bucket. A default is how six operation kinds — including dropping a column — came to describe
/// themselves as adding a derived column. Adding a kind must break here; runtime admission uses the
/// kernel result rather than this display vocabulary.
/// </remarks>
public static class SchemaChangeMapping
{
    public static IReadOnlyList<SchemaChange> Describe(IEnumerable<PhysicalSchemaOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations
            .Where(operation => operation.Kind is not PhysicalSchemaOperationKind.ValidatePhysicalSchema and
                                not PhysicalSchemaOperationKind.PublishAppliedState and
                                not PhysicalSchemaOperationKind.BackfillColumn and
                                not PhysicalSchemaOperationKind.FinalizeColumn and
                                // A supersession marker performs no physical work at all: it records
                                // that a column is deliberately retained, or that the DropColumn
                                // beside it in the contract plan removed it. That removal is already
                                // described; describing the marker too would double-count it.
                                not PhysicalSchemaOperationKind.ColumnSupersession)
            .Select(operation => new SchemaChange(Describe(operation), operation.SubjectIdentity))
            .ToArray();
    }

    /// <summary>
    /// Describes physical work and declaration-only aggregation-profile changes. Profiles are part
    /// of the kernel target fingerprint but have no physical operation, so providers that expose
    /// schema diffs must supply the prior declaration to retain that public change vocabulary.
    /// </summary>
    public static IReadOnlyList<SchemaChange> Describe(
        IEnumerable<PhysicalSchemaOperation> operations,
        StorageUnit? previous,
        StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var changes = Describe(operations).ToList();
        var previousProfiles = previous?.AggregationProfiles
            .ToDictionary(profile => profile.Name, StringComparer.Ordinal) ?? [];
        var desiredProfiles = desired.AggregationProfiles.ToDictionary(profile => profile.Name, StringComparer.Ordinal);
        changes.AddRange(desiredProfiles.Keys
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => desiredProfiles[name])
            .Where(profile =>
                !previousProfiles.TryGetValue(profile.Name, out var prior) ||
                !string.Equals(
                    AggregationProfileCanonicalization.Canonicalize(prior),
                    AggregationProfileCanonicalization.Canonicalize(profile),
                    StringComparison.Ordinal))
            .Select(profile => new SchemaChange(SchemaChangeKind.UpdateAggregationProfile, profile.Name)));
        changes.AddRange(previousProfiles.Keys
            .OrderBy(name => name, StringComparer.Ordinal)
            .Where(name => !desiredProfiles.ContainsKey(name))
            .Select(name => new SchemaChange(SchemaChangeKind.UpdateAggregationProfile, name)));
        return changes;
    }

    private static SchemaChangeKind Describe(PhysicalSchemaOperation operation) => operation switch
    {
        // An index taken out of the way of a column alteration is put back by the same plan, so it
        // is a rebuild rather than a removal.
        DropPhysicalIndexOperation { IsRebuild: true } => SchemaChangeKind.RebuildIndex,
        _ => operation.Kind switch
        {
            PhysicalSchemaOperationKind.CreatePrimaryStorage => SchemaChangeKind.CreateStorageUnit,
            PhysicalSchemaOperationKind.AddColumn =>
                operation.SubjectIdentity.StartsWith("__groundwork_", StringComparison.Ordinal)
                    ? SchemaChangeKind.AddDerivedColumn
                    : SchemaChangeKind.AddColumn,
            PhysicalSchemaOperationKind.CreatePhysicalIndex => SchemaChangeKind.CreateIndex,
            PhysicalSchemaOperationKind.RebuildPhysicalIndex => SchemaChangeKind.RebuildIndex,
            PhysicalSchemaOperationKind.RenamePrimaryStorage => SchemaChangeKind.RenameStorageUnit,
            PhysicalSchemaOperationKind.RenameColumn => SchemaChangeKind.RenameColumn,
            PhysicalSchemaOperationKind.AlterColumn => SchemaChangeKind.AlterColumn,
            PhysicalSchemaOperationKind.DropColumn => SchemaChangeKind.DropColumn,
            PhysicalSchemaOperationKind.DropIndex => SchemaChangeKind.DropIndex,
            PhysicalSchemaOperationKind.DropPrimaryStorage => SchemaChangeKind.DropStorageUnit,
            // A provider-owned definition has described itself as a derived column since it was
            // introduced. It remains a display-vocabulary compromise; runtime admission uses the
            // provider's kernel result rather than this mapping.
            PhysicalSchemaOperationKind.ApplyProviderDefinition => SchemaChangeKind.AddDerivedColumn,
            _ => throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation.Kind,
                "No public schema-change description is defined for this physical schema operation kind.")
        }
    };
}
