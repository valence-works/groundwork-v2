using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Schema;

namespace Groundwork.SchemaTool;

public static class SchemaCompilation
{
    public static IReadOnlyList<StorageUnit> Compile(SchemaDocument schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var units = EnrichReferenceTargetScopes(schema.Tables.Select(Compile).ToArray());
        SchemaSubject.ValidateManifest(units);
        var unitsById = units.ToDictionary(unit => unit.Id);
        foreach (var table in schema.Tables)
        {
            // Offline verification has no provider compiler to construct a subject, but evolution
            // still has declaration-level invariants: the replacement must exist and the retired
            // column must no longer be part of the target shape.
            _ = new SchemaSubject(unitsById[new StorageUnitId(table.LogicalId)], Compile(table.Evolution));
        }
        return units;
    }

    public static StorageUnit Compile(SchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = table.Columns.Select(Compile).ToList();
        return new StorageUnit
        {
            Id = new StorageUnitId(table.LogicalId),
            Name = table.Name,
            Columns = ApplyConcurrencyToken(columns, table.Concurrency?.TokenColumn),
            Key = new KeyDefinition { Columns = table.Key.ToArray() },
            Indexes = table.Indexes.Select(index => new IndexDefinition
            {
                Name = index.Name,
                Columns = index.Columns.Select(column => new IndexColumn(
                    column.Name,
                    column.Descending ? SortDirection.Descending : SortDirection.Ascending)).ToArray(),
                IsUnique = index.Unique,
                MissingValues = index.IncludeNulls
                    ? MissingValueBehavior.Included
                    : MissingValueBehavior.Excluded
            }).ToArray(),
            References = table.References.Select(reference => new ReferenceDefinition
            {
                Name = reference.Name,
                Columns = reference.Columns.ToArray(),
                TargetUnitId = new StorageUnitId(reference.Target),
                Enforcement = reference.Physical
                    ? ReferenceEnforcement.Physical
                    : ReferenceEnforcement.LogicalOnly
            }).ToArray(),
            CheckConstraints = table.Checks.Select(check => new CheckConstraintDefinition
            {
                Name = check.Name,
                Column = check.Column,
                Operator = check.Operator switch
                {
                    SchemaCheckOperator.Equal => CheckConstraintOperator.Equal,
                    SchemaCheckOperator.NotEqual => CheckConstraintOperator.NotEqual,
                    SchemaCheckOperator.GreaterThan => CheckConstraintOperator.GreaterThan,
                    SchemaCheckOperator.GreaterThanOrEqual => CheckConstraintOperator.GreaterThanOrEqual,
                    SchemaCheckOperator.LessThan => CheckConstraintOperator.LessThan,
                    SchemaCheckOperator.LessThanOrEqual => CheckConstraintOperator.LessThanOrEqual,
                    _ => throw new ArgumentOutOfRangeException(nameof(check.Operator), check.Operator, null)
                },
                Value = new PortableDefault(check.Value.Value)
            }).ToArray(),
            AggregationProfiles = table.Aggregations.Select(Compile).ToArray(),
            InteropView = table.InteropView is null ? null : new InteropViewDeclaration(table.InteropView),
            Scope = table.Scope == SchemaScope.Scoped ? ScopePolicy.Scoped : ScopePolicy.Global,
            ForeignColumns = table.ForeignColumns == SchemaForeignColumns.TolerateDatabaseSupplied
                ? ForeignColumnPolicy.TolerateDatabaseSupplied
                : ForeignColumnPolicy.Refuse,
            Concurrency = table.Concurrency is null
                ? ConcurrencyDeclaration.None
                : ConcurrencyDeclaration.Optimistic(table.Concurrency.TokenColumn),
            Timestamps = TimestampDeclaration.None,
            Retention = table.Retention is null ? null : new RetentionDeclaration
            {
                KeepNewest = table.Retention.KeepNewest,
                OrderColumn = table.Retention.OrderBy,
                Trigger = table.Retention.Trigger == SchemaRetentionTrigger.OnAppend
                    ? RetentionTrigger.OnAppend
                    : RetentionTrigger.Explicit,
                PartitionColumns = table.Retention.PartitionBy.ToArray()
            },
            AppendIdempotency = table.AppendIdempotency is null ? null : Named(
                new AppendIdempotencyDeclaration { Window = table.AppendIdempotency.Window },
                table.AppendIdempotency.LedgerName),
            RetentionIdempotency = table.RetentionIdempotency is null ? null : Named(
                new RetentionIdempotencyDeclaration { Window = table.RetentionIdempotency.Window },
                table.RetentionIdempotency.LedgerName)
        };
    }

    private static ColumnDefinition Compile(SchemaColumn column) => new()
    {
        Name = column.Name,
        Id = column.Id,
        Type = Map(column.Type),
        IsNullable = column.IsNullable,
        MaxLength = column.Length,
        Precision = column.Precision,
        Scale = column.Scale,
        Collation = column.Folding switch
        {
            TextFolding.None => null,
            TextFolding.AsciiIgnoreCase => PortableCollation.OrdinalIgnoreCase,
            TextFolding.UnicodeOrdinalIgnoreCase => PortableCollation.UnicodeOrdinalIgnoreCase,
            _ => throw new ArgumentOutOfRangeException(nameof(column.Folding), column.Folding, null)
        },
        Default = column.Default is null ? null : new PortableDefault(column.Default.Value),
        Generation = column.Generation == SchemaGeneration.ProviderSequence
            ? ColumnGeneration.ProviderSequence
            : ColumnGeneration.Supplied
    };

    private static SchemaEvolutionMetadata Compile(SchemaEvolution? evolution) => evolution is null
        ? new SchemaEvolutionMetadata()
        : new SchemaEvolutionMetadata(
            evolution.IsDestructive,
            evolution.SemanticMigrationId,
            evolution.RetiresPrimaryStorage,
            [.. evolution.Supersessions.Select(item => new ColumnSupersession(
                Compile(item.SupersededColumn), item.ReplacementColumn))],
            evolution.DualPresenceWindow);

    /// <summary>
    /// Mirrors the fluent builder, which supplies the system-owned token column when a declaration
    /// opts into optimistic concurrency without spelling it out.
    /// </summary>
    private static IReadOnlyList<ColumnDefinition> ApplyConcurrencyToken(List<ColumnDefinition> columns, string? token)
    {
        if (token is null)
            return columns;
        var index = columns.FindIndex(column => string.Equals(column.Name, token, StringComparison.Ordinal));
        if (index < 0)
            columns.Add(new ColumnDefinition { Name = token, Type = PortableType.Int64, IsNullable = false, Default = new PortableDefault(0L) });
        else if (columns[index] is { Type: PortableType.Int64, IsNullable: false, Default: null } declared)
            columns[index] = declared with { Default = new PortableDefault(0L) };
        return columns;
    }

    private static AppendIdempotencyDeclaration Named(AppendIdempotencyDeclaration declaration, string? ledgerName) =>
        ledgerName is null ? declaration : declaration with { LedgerName = ledgerName };

    private static RetentionIdempotencyDeclaration Named(RetentionIdempotencyDeclaration declaration, string? ledgerName) =>
        ledgerName is null ? declaration : declaration with { LedgerName = ledgerName };

    private static AggregationProfile Compile(SchemaAggregation aggregation) => new()
    {
        Name = aggregation.Name,
        GroupByColumns = aggregation.GroupByColumns.ToArray(),
        GroupByExpressions = aggregation.GroupBy.Select(group => (AggregationGroup)(group.Bucket switch
        {
            SchemaTimeBucket.None => new AggregationGroup.Column(group.Alias),
            SchemaTimeBucket.FixedUtc => AggregationGroup.TimeBucket.FixedUtc(group.Alias, group.SourceColumn!, group.Width),
            _ => AggregationGroup.TimeBucket.LocalCalendarDay(group.Alias, group.SourceColumn!)
        })).ToArray(),
        Aggregates = aggregation.Aggregates.Select(aggregate => (Aggregate)(aggregate.Kind switch
        {
            SchemaAggregateKind.Min => new Aggregate.Min(aggregate.Alias, aggregate.Column!),
            SchemaAggregateKind.Max => new Aggregate.Max(aggregate.Alias, aggregate.Column!),
            SchemaAggregateKind.Count => new Aggregate.Count(aggregate.Alias),
            SchemaAggregateKind.Sum => new Aggregate.Sum(aggregate.Alias, aggregate.Column!),
            SchemaAggregateKind.SetUnion => new Aggregate.SetUnion(aggregate.Alias, aggregate.Column!, aggregate.MaxValues),
            _ => new Aggregate.FirstBy(
                aggregate.Alias,
                aggregate.Column!,
                aggregate.OrderBy!,
                aggregate.Descending ? SortDirection.Descending : SortDirection.Ascending)
        })).ToArray()
    };

    /// <summary>
    /// Compiles the declared schema through the provider's own physicalization, so a deployed
    /// target is byte-identical to the target its runtime coordinator expects.
    /// </summary>
    public static IReadOnlyList<PhysicalSchemaTarget> CompileTargets(
        SchemaDocument schema,
        IPhysicalSchemaTargetCompiler targets)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(targets);
        var units = EnrichReferenceTargetScopes(schema.Tables.Select(Compile).ToArray());
        var evolutionById = schema.Tables.ToDictionary(
            table => new StorageUnitId(table.LogicalId),
            table => table.Evolution);
        SchemaSubject.ValidateManifestWithoutCrossUnitReferences(units);
        var refusals = StorageReferenceValidation.ValidateManifestBySource(units)
            .GroupBy(result => result.SourceUnitId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(result => new SchemaRefusal(
                    result.Finding.Code,
                    result.Finding.Message,
                    result.Finding.Path)).ToArray());
        return OrderForPhysicalConstraintDeployment(units).Select(unit =>
        {
            var evolution = evolutionById[unit.Id];
            // Preserve metadata supplied by existing in-process compilers unless the document
            // explicitly takes authority for evolution.
            var target = evolution is null
                ? targets.Compile(unit)
                : targets.Compile(unit, Compile(evolution));
            return refusals.TryGetValue(unit.Id, out var unitRefusals)
                ? target.WithPlanningRefusals(unitRefusals)
                : target;
        }).ToArray();
    }

    private static IReadOnlyList<StorageUnit> OrderForPhysicalConstraintDeployment(
        IReadOnlyList<StorageUnit> units)
    {
        var knownIds = units.Select(unit => unit.Id).ToHashSet();
        var emittedIds = new HashSet<StorageUnitId>();
        var pending = units.ToList();
        var ordered = new List<StorageUnit>(units.Count);
        while (pending.Count != 0)
        {
            var next = pending.FindIndex(unit => (unit.References ?? [])
                .Where(reference => reference.Enforcement == ReferenceEnforcement.Physical)
                .Select(reference => reference.TargetUnitId)
                .Where(targetId => targetId != unit.Id && knownIds.Contains(targetId))
                .All(emittedIds.Contains));
            if (next < 0)
            {
                // Cyclic references can still be added to tables that already exist. Preserve the
                // manifest order so planning and reporting stay deterministic; a fresh relational
                // deployment will surface the provider's native cycle limitation.
                ordered.AddRange(pending);
                break;
            }

            var unit = pending[next];
            pending.RemoveAt(next);
            ordered.Add(unit);
            emittedIds.Add(unit.Id);
        }

        return ordered;
    }

    private static IReadOnlyList<StorageUnit> EnrichReferenceTargetScopes(IReadOnlyList<StorageUnit> units)
    {
        var targets = units
            .GroupBy(unit => unit.Id.Value, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count() == 1 ? group.Single() : null,
                StringComparer.Ordinal);

        return units.Select(unit => unit with
        {
            References = (unit.References ?? []).Select(reference =>
            {
                if (!targets.TryGetValue(reference.TargetUnitId.Value, out var target) || target is null)
                    return reference;
                return reference with
                {
                    TargetScope = reference.TargetScope ?? target.Scope,
                    TargetName = reference.Enforcement == ReferenceEnforcement.Physical
                        ? target.Name
                        : reference.TargetName,
                    TargetKeyColumns = reference.Enforcement == ReferenceEnforcement.Physical
                        ? target.Key.Columns.ToArray()
                        : reference.TargetKeyColumns,
                    TargetKeyHasProviderSequence = reference.Enforcement == ReferenceEnforcement.Physical
                        ? target.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence)
                        : reference.TargetKeyHasProviderSequence
                };
            }).ToArray()
        }).ToArray();
    }

    private static PortableType Map(SchemaValueType type) => type switch
    {
        SchemaValueType.String => PortableType.String,
        SchemaValueType.Int32 => PortableType.Int32,
        SchemaValueType.Int64 => PortableType.Int64,
        SchemaValueType.Decimal => PortableType.Decimal,
        SchemaValueType.Boolean => PortableType.Boolean,
        SchemaValueType.DateTimeOffset => PortableType.DateTimeOffset,
        SchemaValueType.Guid => PortableType.Guid,
        SchemaValueType.Binary => PortableType.Binary,
        SchemaValueType.Json => PortableType.Json,
        SchemaValueType.Double => PortableType.Double,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}
