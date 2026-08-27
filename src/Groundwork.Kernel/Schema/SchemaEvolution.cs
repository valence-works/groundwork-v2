using System.Collections.Immutable;

namespace Groundwork.Kernel.Schema;

/// <summary>
/// Classifies one in-place column redefinition. Widening keeps every already-stored value
/// representable; anything else can refuse or truncate rows and is therefore narrowing.
/// </summary>
internal static class ColumnEvolution
{
    /// <summary>
    /// Returns how <paramref name="declared"/> redefines <paramref name="applied"/>, or
    /// <see langword="null"/> when the two describe the same column. The physical name and logical
    /// id are deliberately not compared: a pure rename is its own operation, not an alteration.
    /// </summary>
    public static ColumnAlterationKind? Classify(ColumnDefinition applied, ColumnDefinition declared)
    {
        ArgumentNullException.ThrowIfNull(applied);
        ArgumentNullException.ThrowIfNull(declared);
        var normalized = applied with { Name = declared.Name, Id = declared.Id };
        if (string.Equals(
                AddColumnOperation.CanonicalColumn(normalized),
                AddColumnOperation.CanonicalColumn(declared),
                StringComparison.Ordinal))
        {
            return null;
        }

        return IsWidening(applied, declared) ? ColumnAlterationKind.Widening : ColumnAlterationKind.Narrowing;
    }

    private static bool IsWidening(ColumnDefinition applied, ColumnDefinition declared) =>
        applied.Type == declared.Type &&
        applied.Generation == declared.Generation &&
        applied.Collation == declared.Collation &&
        applied.LogicalCollation == declared.LogicalCollation &&
        // Relaxing a required column to optional accepts strictly more; the reverse can refuse rows
        // that are already null.
        (!applied.IsNullable || declared.IsNullable) &&
        IsWiderOrEqualLength(applied, declared) &&
        IsWiderOrEqualNumeric(applied, declared);

    private static bool IsWiderOrEqualLength(ColumnDefinition applied, ColumnDefinition declared) =>
        declared.MaxLength is null || (applied.MaxLength is { } current && declared.MaxLength >= current);

    private static bool IsWiderOrEqualNumeric(ColumnDefinition applied, ColumnDefinition declared)
    {
        if (applied.Precision is null && declared.Precision is null)
            return applied.Scale == declared.Scale;
        // A decimal keeps its stored values only when the integral room grows and the fractional
        // digits stay exactly where they were.
        return applied.Precision is { } currentPrecision &&
            declared.Precision is { } targetPrecision &&
            targetPrecision >= currentPrecision &&
            applied.Scale == declared.Scale;
    }
}

/// <summary>
/// The staged evolution rules that replace the additive-only refusal. Every applied definition that
/// the target changed, renamed, or removed is classified into an explicit operation that carries its
/// own authorization; only evolutions with no portable meaning are refused.
/// </summary>
internal sealed class SchemaEvolutionAnalysis
{
    private SchemaEvolutionAnalysis(
        ImmutableArray<SchemaRefusal> refusals,
        ImmutableArray<PhysicalSchemaOperation> operations,
        IReadOnlySet<string> satisfiedIdentities,
        IReadOnlyDictionary<string, PhysicalSchemaAppliedOperation> appliedByLogicalSlot,
        IReadOnlySet<string> rebuiltIndexNames)
    {
        Refusals = refusals;
        Operations = operations;
        SatisfiedIdentities = satisfiedIdentities;
        AppliedByLogicalSlot = appliedByLogicalSlot;
        RebuiltIndexNames = rebuiltIndexNames;
    }

    public ImmutableArray<SchemaRefusal> Refusals { get; }

    /// <summary>The rename, alter, and drop operations the evolution requires.</summary>
    public ImmutableArray<PhysicalSchemaOperation> Operations { get; }

    /// <summary>Desired operations already realized by the applied schema or by an evolution operation.</summary>
    public IReadOnlySet<string> SatisfiedIdentities { get; }

    /// <summary>Applied operations keyed on the logical slot they occupy.</summary>
    public IReadOnlyDictionary<string, PhysicalSchemaAppliedOperation> AppliedByLogicalSlot { get; }

    /// <summary>Indexes this evolution takes out of the way and has to put back.</summary>
    public IReadOnlySet<string> RebuiltIndexNames { get; }

    public static SchemaEvolutionAnalysis Empty { get; } = new(
        [],
        [],
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, PhysicalSchemaAppliedOperation>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// The logical slot an operation occupies. Column work is keyed on the stable logical column id
    /// and primary storage on the stable storage-unit id, so a changed physical name lands in the
    /// slot it already occupied and is planned as a rename instead of a drop and a create.
    /// </summary>
    public static string LogicalSlot(
        PhysicalSchemaOperationKind kind,
        StorageUnitId? subjectId,
        string subjectIdentity,
        string slotIdentity,
        IReadOnlyDictionary<string, string> logicalColumnIds) => kind switch
    {
        PhysicalSchemaOperationKind.CreatePrimaryStorage => $"primary-storage:{subjectId?.Value}",
        PhysicalSchemaOperationKind.AddColumn or
        PhysicalSchemaOperationKind.BackfillColumn or
        PhysicalSchemaOperationKind.FinalizeColumn =>
            $"{kind}:{subjectId?.Value}:{Resolve(logicalColumnIds, subjectIdentity)}",
        // A rebuild deliberately shares the create slot, so both spellings of an index compare as
        // one logical index.
        PhysicalSchemaOperationKind.CreatePhysicalIndex or
        PhysicalSchemaOperationKind.RebuildPhysicalIndex => $"index:{subjectId?.Value}:{subjectIdentity}",
        _ => slotIdentity
    };

    private static string Resolve(IReadOnlyDictionary<string, string> logicalColumnIds, string physicalName) =>
        logicalColumnIds.TryGetValue(physicalName, out var logicalId) ? logicalId : physicalName;

    public static SchemaEvolutionAnalysis Analyze(
        PhysicalSchemaTarget target,
        PhysicalSchemaAppliedState? applied,
        IReadOnlyList<PhysicalSchemaOperation> desired)
    {
        if (applied is null)
            return Empty;

        var appliedSubject = applied.Snapshot.Subject;
        var appliedColumnIds = appliedSubject.Columns.ToDictionary(
            column => column.Name,
            column => column.LogicalId,
            StringComparer.Ordinal);
        var desiredColumnIds = target.Subject.Columns.ToDictionary(
            column => column.Name,
            column => column.LogicalId,
            StringComparer.Ordinal);
        var appliedByLogicalSlot = new Dictionary<string, PhysicalSchemaAppliedOperation>(StringComparer.Ordinal);
        foreach (var operation in applied.Snapshot.SemanticOperations)
        {
            appliedByLogicalSlot[LogicalSlot(
                operation.Kind,
                operation.SubjectId,
                operation.SubjectIdentity,
                operation.SlotIdentity,
                appliedColumnIds)] = operation;
        }

        var refusals = new List<SchemaRefusal>();
        var operations = new List<PhysicalSchemaOperation>();
        var satisfied = new HashSet<string>(StringComparer.Ordinal);
        var appliedColumns = appliedSubject.Columns.ToDictionary(column => column.LogicalId, StringComparer.Ordinal);
        var desiredColumns = target.Subject.Columns.ToDictionary(column => column.LogicalId, StringComparer.Ordinal);

        var rebuiltIndexes = new HashSet<string>(StringComparer.Ordinal);
        PlanPrimaryStorageRename(target, appliedSubject, operations);
        PlanColumnEvolution(target, appliedSubject, appliedColumns, desiredColumns, operations, refusals, rebuiltIndexes);
        PlanRemovals(target, appliedSubject, desiredColumns, operations, refusals);

        MarkSatisfied(appliedSubject, target.Subject, desired, desiredColumnIds, operations, satisfied);
        ReportUnevolvedApplied(desired, appliedByLogicalSlot, desiredColumnIds, refusals);
        return new SchemaEvolutionAnalysis(
            [.. refusals],
            [.. operations],
            satisfied,
            appliedByLogicalSlot,
            rebuiltIndexes);
    }

    /// <summary>
    /// A rename or an alteration already performs the physical work for the column or primary
    /// storage it names, so the desired create, add, backfill, and finalize operations in that slot
    /// must not run again — their new spelling is what the applied ledger goes on to record. A
    /// derived column whose projection algorithm also changed still needs its backfill.
    /// </summary>
    private static void MarkSatisfied(
        SchemaSubject appliedSubject,
        SchemaSubject desiredSubject,
        IReadOnlyList<PhysicalSchemaOperation> desired,
        IReadOnlyDictionary<string, string> desiredColumnIds,
        IReadOnlyList<PhysicalSchemaOperation> evolution,
        HashSet<string> satisfied)
    {
        var renamedStorage = evolution.Any(operation => operation is RenamePrimaryStorageOperation);
        var evolvedColumns = evolution
            .Select(operation => operation switch
            {
                RenameColumnOperation rename => rename.Column.LogicalId,
                AlterColumnOperation alter => alter.Column.LogicalId,
                _ => null
            })
            .Where(logicalId => logicalId is not null)
            .ToHashSet(StringComparer.Ordinal)!;
        foreach (var operation in desired)
        {
            switch (operation.Kind)
            {
                case PhysicalSchemaOperationKind.CreatePrimaryStorage when renamedStorage:
                    satisfied.Add(operation.Identity);
                    break;
                case PhysicalSchemaOperationKind.AddColumn:
                case PhysicalSchemaOperationKind.FinalizeColumn:
                case PhysicalSchemaOperationKind.BackfillColumn:
                    var logicalId = desiredColumnIds.TryGetValue(operation.SubjectIdentity, out var resolved)
                        ? resolved
                        : operation.SubjectIdentity;
                    if (!evolvedColumns.Contains(logicalId))
                        break;
                    if (operation.Kind == PhysicalSchemaOperationKind.BackfillColumn &&
                        ProjectionChanged(appliedSubject, desiredSubject, operation.SubjectIdentity))
                    {
                        break;
                    }
                    satisfied.Add(operation.Identity);
                    break;
            }
        }
    }

    private static bool ProjectionChanged(
        SchemaSubject appliedSubject,
        SchemaSubject desiredSubject,
        string physicalName)
    {
        var desiredProjection = desiredSubject.DerivedColumns.FirstOrDefault(derived => derived.Name == physicalName);
        if (desiredProjection is null)
            return false;
        var appliedProjection = appliedSubject.DerivedColumns.FirstOrDefault(derived => derived.Name == physicalName);
        return appliedProjection is null ||
            appliedProjection.SourceColumn != desiredProjection.SourceColumn ||
            appliedProjection.Projection != desiredProjection.Projection ||
            appliedProjection.AlgorithmId != desiredProjection.AlgorithmId;
    }

    private static void PlanPrimaryStorageRename(
        PhysicalSchemaTarget target,
        SchemaSubject appliedSubject,
        List<PhysicalSchemaOperation> operations)
    {
        // The history row is already keyed on the logical storage-unit id, so a changed physical
        // name arrives as the same subject wearing a new name.
        if (!string.Equals(appliedSubject.Name, target.Subject.Name, StringComparison.Ordinal))
            operations.Add(new RenamePrimaryStorageOperation(target.Subject, appliedSubject.Name, appliedSubject.Indexes));
    }

    private static void PlanColumnEvolution(
        PhysicalSchemaTarget target,
        SchemaSubject appliedSubject,
        IReadOnlyDictionary<string, ColumnDefinition> appliedColumns,
        IReadOnlyDictionary<string, ColumnDefinition> desiredColumns,
        List<PhysicalSchemaOperation> operations,
        List<SchemaRefusal> refusals,
        HashSet<string> rebuiltIndexes)
    {
        var appliedNames = appliedSubject.Columns
            .ToDictionary(column => column.Name, column => column.LogicalId, StringComparer.Ordinal);
        foreach (var (logicalId, desiredColumn) in desiredColumns.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (!appliedColumns.TryGetValue(logicalId, out var appliedColumn))
                continue;
            if (!string.Equals(appliedColumn.Name, desiredColumn.Name, StringComparison.Ordinal))
            {
                // Renaming onto a physical name another applied column still occupies has no
                // single-statement meaning; the two renames would have to be ordered by hand.
                if (appliedNames.TryGetValue(desiredColumn.Name, out var occupant) &&
                    !string.Equals(occupant, logicalId, StringComparison.Ordinal))
                {
                    refusals.Add(new SchemaRefusal(
                        "GW-SCHEMA-003",
                        $"Column '{appliedColumn.Name}' cannot be renamed to '{desiredColumn.Name}' while applied column " +
                        $"'{desiredColumn.Name}' still holds that name. Rename through a free name, in two applies.",
                        $"schema.columns.{desiredColumn.Name}.name"));
                    continue;
                }
                operations.Add(new RenameColumnOperation(target.Subject, appliedColumn.Name, desiredColumn));
            }

            if (ColumnEvolution.Classify(appliedColumn, desiredColumn) is not { } alteration)
                continue;
            if (target.Subject.Key.Columns.Contains(desiredColumn.Name, StringComparer.Ordinal) &&
                appliedColumn.Type != desiredColumn.Type)
            {
                refusals.Add(new SchemaRefusal(
                    "GW-SCHEMA-003",
                    $"Key column '{desiredColumn.Name}' cannot change its portable type from " +
                    $"{appliedColumn.Type} to {desiredColumn.Type}. Declare a new unit and migrate its rows.",
                    $"schema.columns.{desiredColumn.Name}.type"));
                continue;
            }
            operations.Add(new AlterColumnOperation(target.Subject, appliedColumn, desiredColumn, alteration));
            PlanIndexRebuildsFor(target.Subject, appliedColumn.Name, desiredColumn.Name, operations, rebuiltIndexes);
        }
    }

    /// <summary>
    /// An index over a column being redefined is dropped before the alteration and recreated after
    /// it. Providers disagree about whether an indexed column can be altered in place at all, so
    /// the plan takes the index out of the way rather than depending on the most permissive one.
    /// </summary>
    private static void PlanIndexRebuildsFor(
        SchemaSubject subject,
        string appliedName,
        string declaredName,
        List<PhysicalSchemaOperation> operations,
        HashSet<string> rebuiltIndexes)
    {
        foreach (var index in subject.Indexes.OrderBy(index => index.Name, StringComparer.Ordinal))
        {
            if (!index.Columns.Any(column =>
                    string.Equals(column.Column, declaredName, StringComparison.Ordinal) ||
                    string.Equals(column.Column, appliedName, StringComparison.Ordinal)))
            {
                continue;
            }
            if (!rebuiltIndexes.Add(index.Name))
                continue;
            operations.Add(new DropPhysicalIndexOperation(subject, index));
        }
    }

    private static void PlanRemovals(
        PhysicalSchemaTarget target,
        SchemaSubject appliedSubject,
        IReadOnlyDictionary<string, ColumnDefinition> desiredColumns,
        List<PhysicalSchemaOperation> operations,
        List<SchemaRefusal> refusals)
    {
        foreach (var column in appliedSubject.Columns.OrderBy(column => column.Name, StringComparer.Ordinal))
        {
            if (desiredColumns.ContainsKey(column.LogicalId))
                continue;
            if (appliedSubject.Key.Columns.Contains(column.Name, StringComparer.Ordinal))
            {
                refusals.Add(new SchemaRefusal(
                    "GW-SCHEMA-004",
                    $"Key column '{column.Name}' cannot be dropped; it is what identifies a row. " +
                    "Declare a new unit and migrate its rows.",
                    $"schema.key.{column.Name}"));
                continue;
            }
            operations.Add(new DropColumnOperation(target.Subject, column));
        }

        var desiredIndexes = target.Subject.Indexes.Select(index => index.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var index in appliedSubject.Indexes.OrderBy(index => index.Name, StringComparer.Ordinal))
        {
            if (!desiredIndexes.Contains(index.Name))
                operations.Add(new DropPhysicalIndexOperation(target.Subject, index));
        }
    }

    /// <summary>
    /// Applied work that no rule above accounted for. Declared columns and indexes are fully covered
    /// by the rename, alter, and drop rules, so anything left is provider-owned bookkeeping whose
    /// disappearance the kernel cannot describe as an operation.
    /// </summary>
    private static void ReportUnevolvedApplied(
        IReadOnlyList<PhysicalSchemaOperation> desired,
        IReadOnlyDictionary<string, PhysicalSchemaAppliedOperation> appliedByLogicalSlot,
        IReadOnlyDictionary<string, string> desiredColumnIds,
        List<SchemaRefusal> refusals)
    {
        var desiredSlots = desired
            .Select(operation => LogicalSlot(
                operation.Kind,
                operation.SubjectId,
                operation.SubjectIdentity,
                operation.SlotIdentity,
                desiredColumnIds))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (slot, operation) in appliedByLogicalSlot.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (desiredSlots.Contains(slot) ||
                operation.Kind is PhysicalSchemaOperationKind.CreatePrimaryStorage or
                    PhysicalSchemaOperationKind.AddColumn or
                    PhysicalSchemaOperationKind.BackfillColumn or
                    PhysicalSchemaOperationKind.FinalizeColumn or
                    PhysicalSchemaOperationKind.CreatePhysicalIndex or
                    PhysicalSchemaOperationKind.RebuildPhysicalIndex)
            {
                continue;
            }

            refusals.Add(new SchemaRefusal(
                "GW-SCHEMA-004",
                $"Applied operation '{operation.Identity}' is absent from the desired target and has no " +
                "portable removal operation. Rebuild the target from the current declaration.",
                $"schema.operations.{operation.SubjectIdentity}"));
        }
    }
}
