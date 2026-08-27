using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Schema;

namespace Groundwork.SchemaTool;

public static class SchemaCompilation
{
    public static IReadOnlyList<StorageUnit> Compile(SchemaDocument schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var units = schema.Tables.Select(Compile).ToArray();
        SchemaSubject.ValidateManifest(units);
        return units;
    }

    public static StorageUnit Compile(SchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var columns = table.Columns.Select(column => new ColumnDefinition
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
        }).ToList();
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
            AggregationProfiles = table.Aggregations.Select(Compile).ToArray(),
            Scope = table.Scope == SchemaScope.Scoped ? ScopePolicy.Scoped : ScopePolicy.Global,
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
        ArgumentNullException.ThrowIfNull(targets);
        return Compile(schema).Select(targets.Compile).ToArray();
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
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };
}
