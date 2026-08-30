using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Groundwork.Schema;

/// <summary>A declared portable default. A null <see cref="Value"/> declares a NULL default.</summary>
public sealed record SchemaDefault(object? Value);

public sealed record SchemaColumn
{
    public SchemaColumn(
        string name,
        SchemaValueType type,
        bool isNullable = true,
        int? length = null,
        int? precision = null,
        int? scale = null,
        TextFolding folding = TextFolding.None,
        SchemaGeneration generation = SchemaGeneration.Supplied,
        SchemaDefault? defaultValue = null,
        string? id = null)
    {
        Name = Require(name, nameof(name));
        Type = type;
        IsNullable = isNullable;
        Length = length;
        Precision = precision;
        Scale = scale;
        Folding = folding;
        Generation = generation;
        Default = defaultValue;
        Id = string.IsNullOrWhiteSpace(id) ? null : id;
    }

    public string Name { get; }
    public SchemaValueType Type { get; }
    public bool IsNullable { get; }
    public int? Length { get; }
    public int? Precision { get; }
    public int? Scale { get; }
    public TextFolding Folding { get; }
    public SchemaGeneration Generation { get; }
    public SchemaDefault? Default { get; }

    /// <summary>
    /// The stable logical identity of this column, spelled only once the physical name changes.
    /// Schema planning keys its slots on it, so keeping the original id across a renamed
    /// <see cref="Name"/> is what makes the change deploy as a rename instead of a drop and an add.
    /// </summary>
    public string? Id { get; }

    /// <summary>The logical id this column is planned under; <see cref="Name"/> when none is declared.</summary>
    public string LogicalId => Id ?? Name;

    private static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty value is required.", parameterName) : value;
}

public sealed record SchemaIndexColumn
{
    public SchemaIndexColumn(string name, bool descending = false)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A non-empty value is required.", nameof(name)) : name;
        Descending = descending;
    }

    public string Name { get; }
    public bool Descending { get; }
}

public sealed record SchemaIndex
{
    public SchemaIndex(string name, IEnumerable<SchemaIndexColumn> columns, bool includeNulls = true, bool unique = false)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A non-empty value is required.", nameof(name)) : name;
        Columns = Snapshot(columns, nameof(columns));
        IncludeNulls = includeNulls;
        Unique = unique;
    }

    public string Name { get; }
    public IReadOnlyList<SchemaIndexColumn> Columns { get; }
    public bool IncludeNulls { get; }
    public bool Unique { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values, string parameterName) =>
        new ReadOnlyCollection<T>((values ?? throw new ArgumentNullException(parameterName)).ToArray());
}

/// <summary>Declares a system-owned optimistic concurrency token.</summary>
public sealed record SchemaConcurrency
{
    public SchemaConcurrency(string tokenColumn) =>
        TokenColumn = string.IsNullOrWhiteSpace(tokenColumn)
            ? throw new ArgumentException("A non-empty value is required.", nameof(tokenColumn))
            : tokenColumn;

    public string TokenColumn { get; }
}

/// <summary>Declares how many newest rows survive, optionally independently per partition.</summary>
public sealed record SchemaRetention
{
    public SchemaRetention(
        int keepNewest,
        string orderBy,
        SchemaRetentionTrigger trigger = SchemaRetentionTrigger.Explicit,
        IEnumerable<string>? partitionBy = null)
    {
        KeepNewest = keepNewest;
        OrderBy = string.IsNullOrWhiteSpace(orderBy) ? throw new ArgumentException("A non-empty value is required.", nameof(orderBy)) : orderBy;
        Trigger = trigger;
        PartitionBy = new ReadOnlyCollection<string>((partitionBy ?? Array.Empty<string>()).ToArray());
    }

    public int KeepNewest { get; }
    public string OrderBy { get; }
    public SchemaRetentionTrigger Trigger { get; }
    public IReadOnlyList<string> PartitionBy { get; }
}

/// <summary>
/// Declares a durable replay window and, when overridden, the ledger that records it. A null
/// ledger name keeps the kernel-owned default rather than restating it in the canonical document.
/// </summary>
public sealed record SchemaIdempotency
{
    public SchemaIdempotency(TimeSpan window, string? ledgerName = null)
    {
        Window = window;
        LedgerName = string.IsNullOrWhiteSpace(ledgerName) ? null : ledgerName;
    }

    public TimeSpan Window { get; }
    public string? LedgerName { get; }
}

/// <summary>One grouping term of a declared aggregation profile.</summary>
public sealed record SchemaAggregationGroup
{
    private SchemaAggregationGroup(string alias, string? sourceColumn, SchemaTimeBucket bucket, TimeSpan width)
    {
        Alias = string.IsNullOrWhiteSpace(alias) ? throw new ArgumentException("A non-empty value is required.", nameof(alias)) : alias;
        SourceColumn = sourceColumn;
        Bucket = bucket;
        Width = width;
    }

    public static SchemaAggregationGroup Column(string alias) =>
        new(alias, null, SchemaTimeBucket.None, TimeSpan.Zero);

    public static SchemaAggregationGroup FixedUtcBucket(string alias, string sourceColumn, TimeSpan width) =>
        new(alias, RequireSource(sourceColumn), SchemaTimeBucket.FixedUtc, width);

    public static SchemaAggregationGroup LocalCalendarDayBucket(string alias, string sourceColumn) =>
        new(alias, RequireSource(sourceColumn), SchemaTimeBucket.LocalCalendarDay, TimeSpan.Zero);

    public string Alias { get; }
    public string? SourceColumn { get; }
    public SchemaTimeBucket Bucket { get; }
    public TimeSpan Width { get; }

    private static string RequireSource(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", nameof(value))
            : value;
}

/// <summary>One reduction of a declared aggregation profile.</summary>
public sealed record SchemaAggregate
{
    private SchemaAggregate(SchemaAggregateKind kind, string alias, string? column, string? orderBy, bool descending, int maxValues)
    {
        Kind = kind;
        Alias = string.IsNullOrWhiteSpace(alias) ? throw new ArgumentException("A non-empty value is required.", nameof(alias)) : alias;
        Column = column;
        OrderBy = orderBy;
        Descending = descending;
        MaxValues = maxValues;
    }

    public static SchemaAggregate Count(string alias) => new(SchemaAggregateKind.Count, alias, null, null, false, 0);

    public static SchemaAggregate Min(string alias, string column) => new(SchemaAggregateKind.Min, alias, column, null, false, 0);

    public static SchemaAggregate Max(string alias, string column) => new(SchemaAggregateKind.Max, alias, column, null, false, 0);

    public static SchemaAggregate Sum(string alias, string column) => new(SchemaAggregateKind.Sum, alias, column, null, false, 0);

    public static SchemaAggregate SetUnion(string alias, string column, int maxValues) =>
        new(SchemaAggregateKind.SetUnion, alias, column, null, false, maxValues);

    public static SchemaAggregate FirstBy(string alias, string column, string orderBy, bool descending = false) =>
        new(SchemaAggregateKind.FirstBy, alias, column, orderBy, descending, 0);

    internal static SchemaAggregate Create(SchemaAggregateKind kind, string alias, string? column, string? orderBy, bool descending, int maxValues) =>
        new(kind, alias, column, orderBy, descending, maxValues);

    public SchemaAggregateKind Kind { get; }
    public string Alias { get; }
    public string? Column { get; }
    public string? OrderBy { get; }
    public bool Descending { get; }
    public int MaxValues { get; }
}

/// <summary>A named, closed aggregation shape available to callers of a declared table.</summary>
public sealed record SchemaAggregation
{
    public SchemaAggregation(
        string name,
        IEnumerable<SchemaAggregate> aggregates,
        IEnumerable<string>? groupByColumns = null,
        IEnumerable<SchemaAggregationGroup>? groupBy = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A non-empty value is required.", nameof(name)) : name;
        Aggregates = new ReadOnlyCollection<SchemaAggregate>((aggregates ?? throw new ArgumentNullException(nameof(aggregates))).ToArray());
        GroupByColumns = new ReadOnlyCollection<string>((groupByColumns ?? Array.Empty<string>()).ToArray());
        GroupBy = new ReadOnlyCollection<SchemaAggregationGroup>((groupBy ?? Array.Empty<SchemaAggregationGroup>()).ToArray());
    }

    public string Name { get; }
    public IReadOnlyList<string> GroupByColumns { get; }
    public IReadOnlyList<SchemaAggregationGroup> GroupBy { get; }
    public IReadOnlyList<SchemaAggregate> Aggregates { get; }
}

/// <summary>The declaration-level tolerance for deployed columns a table does not declare.</summary>
public enum SchemaForeignColumns
{
    /// <summary>Any undeclared deployed column is drift.</summary>
    Refuse,

    /// <summary>
    /// An undeclared deployed column the database supplies a value for is a warning rather than
    /// drift. One it does not — not nullable, not defaulted, not generated — stays drift.
    /// </summary>
    TolerateDatabaseSupplied
}

/// <summary>One mapping from source columns to another table's declared key.</summary>
public sealed record SchemaReference
{
    public SchemaReference(string name, string target, IEnumerable<string> columns, bool physical = false)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A non-empty value is required.", nameof(name))
            : name;
        Target = string.IsNullOrWhiteSpace(target)
            ? throw new ArgumentException("A non-empty value is required.", nameof(target))
            : target;
        Columns = new ReadOnlyCollection<string>((columns ?? throw new ArgumentNullException(nameof(columns)))
            .Select(column => column ?? throw new ArgumentException("Reference column names cannot be null.", nameof(columns)))
            .ToArray());
        Physical = physical;
    }

    public string Name { get; }
    public string Target { get; }
    public IReadOnlyList<string> Columns { get; }
    public bool Physical { get; }
}

public enum SchemaCheckOperator
{
    Equal,
    NotEqual,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual
}

/// <summary>One named portable comparison check over a table column.</summary>
public sealed record SchemaCheck
{
    public SchemaCheck(string name, string column, SchemaCheckOperator @operator, SchemaDefault value)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("A non-empty value is required.", nameof(name))
            : name;
        Column = string.IsNullOrWhiteSpace(column)
            ? throw new ArgumentException("A non-empty value is required.", nameof(column))
            : column;
        Operator = @operator;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string Name { get; }
    public string Column { get; }
    public SchemaCheckOperator Operator { get; }
    public SchemaDefault Value { get; }
}

/// <summary>One deployed column replaced across an expand-contract dual-presence window.</summary>
public sealed record SchemaColumnSupersession
{
    public SchemaColumnSupersession(SchemaColumn supersededColumn, string replacementColumn)
    {
        SupersededColumn = supersededColumn ?? throw new ArgumentNullException(nameof(supersededColumn));
        ReplacementColumn = string.IsNullOrWhiteSpace(replacementColumn)
            ? throw new ArgumentException("A non-empty value is required.", nameof(replacementColumn))
            : replacementColumn;
        if (string.Equals(SupersededColumn.Name, ReplacementColumn, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Column '{replacementColumn}' cannot supersede itself.", nameof(replacementColumn));
        }
    }

    public SchemaColumn SupersededColumn { get; }
    public string ReplacementColumn { get; }
}

/// <summary>Optional operator-authored evolution metadata for one canonical schema table.</summary>
public sealed record SchemaEvolution
{
    public SchemaEvolution(
        bool isDestructive = false,
        string? semanticMigrationId = null,
        bool retiresPrimaryStorage = false,
        IEnumerable<SchemaColumnSupersession>? supersessions = null,
        TimeSpan dualPresenceWindow = default)
    {
        if (semanticMigrationId is not null && string.IsNullOrWhiteSpace(semanticMigrationId))
            throw new ArgumentException("A semantic migration id cannot be empty.", nameof(semanticMigrationId));
        if (dualPresenceWindow < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(dualPresenceWindow), "A dual-presence window cannot run backwards.");

        IsDestructive = isDestructive;
        SemanticMigrationId = semanticMigrationId;
        RetiresPrimaryStorage = retiresPrimaryStorage;
        Supersessions = new ReadOnlyCollection<SchemaColumnSupersession>((supersessions ?? [])
            .Select(item => item ?? throw new ArgumentException(
                "A column supersession cannot be null.", nameof(supersessions)))
            .OrderBy(item => item.SupersededColumn.Name, StringComparer.Ordinal)
            .ToArray());
        DualPresenceWindow = dualPresenceWindow;

        if (Supersessions.Select(item => item.SupersededColumn.Name)
                .Distinct(StringComparer.Ordinal).Count() != Supersessions.Count)
        {
            throw new ArgumentException(
                "A column can be superseded only once in one declaration.", nameof(supersessions));
        }
        if (Supersessions.Count != 0 && string.IsNullOrWhiteSpace(SemanticMigrationId))
        {
            throw new ArgumentException(
                "A declaration that supersedes a column requires a semantic migration id.",
                nameof(semanticMigrationId));
        }
        if (Supersessions.Count != 0 && RetiresPrimaryStorage)
        {
            throw new ArgumentException(
                "A retired table cannot also supersede one of its columns.", nameof(supersessions));
        }
    }

    public bool IsDestructive { get; }
    public string? SemanticMigrationId { get; }
    public bool RetiresPrimaryStorage { get; }
    public IReadOnlyList<SchemaColumnSupersession> Supersessions { get; }
    public TimeSpan DualPresenceWindow { get; }

    internal bool IsDefault =>
        !IsDestructive &&
        SemanticMigrationId is null &&
        !RetiresPrimaryStorage &&
        Supersessions.Count == 0 &&
        DualPresenceWindow == TimeSpan.Zero;
}

public sealed record SchemaTable
{
    public SchemaTable(
        string name,
        IEnumerable<SchemaColumn> columns,
        IEnumerable<string> key,
        IEnumerable<SchemaIndex>? indexes = null,
        SchemaScope scope = SchemaScope.Global,
        SchemaConcurrency? concurrency = null,
        SchemaTimestamps timestamps = SchemaTimestamps.None,
        SchemaRetention? retention = null,
        SchemaIdempotency? appendIdempotency = null,
        SchemaIdempotency? retentionIdempotency = null,
        IEnumerable<SchemaAggregation>? aggregations = null,
        string? id = null,
        SchemaForeignColumns foreignColumns = SchemaForeignColumns.Refuse,
        IEnumerable<SchemaReference>? references = null,
        IEnumerable<SchemaCheck>? checks = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A non-empty value is required.", nameof(name)) : name;
        Id = string.IsNullOrWhiteSpace(id) ? null : id;
        Columns = Snapshot(columns, nameof(columns));
        Key = Snapshot(key, nameof(key));
        Indexes = Ordered(indexes ?? Array.Empty<SchemaIndex>(), nameof(indexes), index => index.Name);
        Scope = scope;
        Concurrency = concurrency;
        Timestamps = timestamps;
        Retention = retention;
        AppendIdempotency = appendIdempotency;
        RetentionIdempotency = retentionIdempotency;
        Aggregations = Ordered(aggregations ?? Array.Empty<SchemaAggregation>(), nameof(aggregations), aggregation => aggregation.Name);
        ForeignColumns = foreignColumns;
        References = Ordered(references ?? Array.Empty<SchemaReference>(), nameof(references), reference => reference.Name);
        Checks = Ordered(checks ?? Array.Empty<SchemaCheck>(), nameof(checks), check => check.Name);
    }

    public string Name { get; }

    /// <summary>
    /// The stable logical identity of this table, spelled only once the physical name changes. It
    /// is what a deployed catalog's history is keyed on, so keeping the original id across a
    /// renamed <see cref="Name"/> is what makes the change deploy as a rename.
    /// </summary>
    public string? Id { get; }

    /// <summary>The logical id this table is planned under; <see cref="Name"/> when none is declared.</summary>
    public string LogicalId => Id ?? Name;

    public IReadOnlyList<SchemaColumn> Columns { get; }
    public IReadOnlyList<string> Key { get; }
    /// <summary>Held in canonical name order, which the schema fingerprint depends on.</summary>
    public IReadOnlyList<SchemaIndex> Indexes { get; }
    public SchemaScope Scope { get; }
    public SchemaConcurrency? Concurrency { get; }
    public SchemaTimestamps Timestamps { get; }
    public SchemaRetention? Retention { get; }
    public SchemaIdempotency? AppendIdempotency { get; }
    public SchemaIdempotency? RetentionIdempotency { get; }
    /// <summary>Held in canonical name order, which the schema fingerprint depends on.</summary>
    public IReadOnlyList<SchemaAggregation> Aggregations { get; }

    /// <summary>Logical and physically enforced references held in canonical name order.</summary>
    public IReadOnlyList<SchemaReference> References { get; }

    /// <summary>Portable checks held in canonical name order.</summary>
    public IReadOnlyList<SchemaCheck> Checks { get; }

    /// <summary>
    /// How a deployed column this table does not declare is treated. Declared here so the
    /// deployment tool and the host reach the same verdict from one document rather than from a
    /// switch each of them sets independently.
    /// </summary>
    public SchemaForeignColumns ForeignColumns { get; }

    /// <summary>Evolution metadata emitted only when it differs from the safe default.</summary>
    public SchemaEvolution? Evolution { get; init; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values, string parameterName) =>
        new ReadOnlyCollection<T>((values ?? throw new ArgumentNullException(parameterName)).ToArray());

    private static IReadOnlyList<T> Ordered<T>(IEnumerable<T> values, string parameterName, Func<T, string> name) =>
        new ReadOnlyCollection<T>((values ?? throw new ArgumentNullException(parameterName))
            .OrderBy(name, StringComparer.Ordinal).ToArray());
}

public sealed record SchemaDocument
{
    public SchemaDocument(IEnumerable<SchemaTable> tables)
    {
        Tables = new ReadOnlyCollection<SchemaTable>((tables ?? throw new ArgumentNullException(nameof(tables)))
            .OrderBy(table => table.Name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>Held in canonical name order.</summary>
    public IReadOnlyList<SchemaTable> Tables { get; }
}
