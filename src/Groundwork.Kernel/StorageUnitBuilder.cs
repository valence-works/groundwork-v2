using System.Collections;
using System.Runtime.CompilerServices;

namespace Groundwork.Kernel;

/// <summary>Mutable authoring state whose Build result is an immutable declaration snapshot.</summary>
public sealed class StorageDeclarationBuilder
{
    private readonly StorageDeclarationState state;

    internal StorageDeclarationBuilder(StorageDeclarationState state) => this.state = state;

    public StorageDeclarationBuilder String(string name, int maxLength, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.String, configure, builder => builder.MaxLength(maxLength));

    public StorageDeclarationBuilder String(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.String, configure);

    public StorageDeclarationBuilder Int32(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Int32, configure);

    public StorageDeclarationBuilder Int64(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Int64, configure);

    public StorageDeclarationBuilder Decimal(string name, int precision, int scale, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Decimal, configure, builder => builder.Precision(precision, scale));

    public StorageDeclarationBuilder Decimal(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Decimal, configure);

    public StorageDeclarationBuilder Boolean(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Boolean, configure);

    public StorageDeclarationBuilder Timestamp(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.DateTimeOffset, configure);

    public StorageDeclarationBuilder DateTimeOffset(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.DateTimeOffset, configure);

    public StorageDeclarationBuilder Guid(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Guid, configure);

    public StorageDeclarationBuilder Binary(string name, int maxLength, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Binary, configure, builder => builder.MaxLength(maxLength));

    public StorageDeclarationBuilder Binary(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Binary, configure);

    public StorageDeclarationBuilder Json(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Json, configure);

    /// <summary>Adds a column from a runtime portable type. Typed helpers remain preferred for readability.</summary>
    public StorageDeclarationBuilder Column(string name, PortableType type, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, type, configure);

    public StorageDeclarationBuilder Key(params string[] columns)
    {
        state.SetKey(columns);
        return this;
    }

    /// <summary>Opts the unit into a system-owned Int64 optimistic-concurrency token.</summary>
    public StorageDeclarationBuilder OptimisticConcurrency(string tokenColumn = "version")
    {
        state.SetOptimisticConcurrency(tokenColumn);
        return this;
    }

    /// <summary>Alias for <see cref="OptimisticConcurrency"/>.</summary>
    public StorageDeclarationBuilder Optimistic(string tokenColumn = "version") =>
        OptimisticConcurrency(tokenColumn);

    public StorageDeclarationBuilder Retention(RetentionDeclaration declaration)
    {
        state.SetRetention(declaration);
        return this;
    }

    public StorageDeclarationBuilder Retention(
        int keepNewest,
        string orderBy,
        RetentionTrigger trigger = RetentionTrigger.Explicit,
        params string[] partitionColumns) =>
        Retention(new RetentionDeclaration
        {
            KeepNewest = keepNewest,
            OrderColumn = orderBy,
            Trigger = trigger,
            PartitionColumns = partitionColumns ?? []
        });

    /// <summary>Compatibility form for declarations that omit an explicit trigger.</summary>
    public StorageDeclarationBuilder Retention(
        int keepNewest,
        string orderBy,
        params string[] partitionColumns) =>
        Retention(keepNewest, orderBy, RetentionTrigger.Explicit, partitionColumns);

    public StorageDeclarationBuilder KeepNewest(
        int keepNewest,
        string orderBy,
        RetentionTrigger trigger = RetentionTrigger.Explicit,
        params string[] partitionColumns) =>
        Retention(keepNewest, orderBy, trigger, partitionColumns);

    public StorageDeclarationBuilder Retain(RetentionDeclaration declaration) => Retention(declaration);

    /// <summary>Opts the unit into replay-stable operation-identified retention.</summary>
    public StorageDeclarationBuilder RetentionIdempotency(TimeSpan window, string ledgerName = "__groundwork_retention_operations")
    {
        state.SetRetentionIdempotency(new RetentionIdempotencyDeclaration { Window = window, LedgerName = ledgerName });
        return this;
    }

    public StorageDeclarationBuilder Scoped()
    {
        state.SetScope(ScopePolicy.Scoped);
        return this;
    }

    public StorageDeclarationBuilder UniqueIndex(string name, params string[] columns)
    {
        state.AddIndex(name, columns.Select(column => new IndexColumn(column)), unique: true);
        return this;
    }

    public StorageDeclarationBuilder UniqueIndex(string name, Action<IndexBuilder> configure) =>
        AddIndex(name, configure, unique: true);

    public StorageDeclarationBuilder Index(string name, params string[] columns)
    {
        state.AddIndex(name, columns.Select(column => new IndexColumn(column)), unique: false);
        return this;
    }

    public StorageDeclarationBuilder Index(string name, Action<IndexBuilder> configure) =>
        AddIndex(name, configure, unique: false);

    public StorageDeclarationBuilder AppendIdempotency(TimeSpan window, string ledgerName = "__groundwork_operations")
    {
        state.SetAppendIdempotency(new AppendIdempotencyDeclaration { Window = window, LedgerName = ledgerName });
        return this;
    }

    public StorageDeclarationBuilder Aggregate(string name, Action<AggregationBuilder> configure)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));
        var builder = new AggregationBuilder(name);
        configure(builder);
        state.AddAggregation(builder.Build());
        return this;
    }

    public StorageUnit Build(PortabilityValidationContext? context = null) => state.Build(context);

    private StorageDeclarationBuilder AddColumn(
        string name,
        PortableType type,
        Action<ColumnBuilder>? configure,
        Action<ColumnBuilder>? initial = null)
    {
        var builder = new ColumnBuilder();
        initial?.Invoke(builder);
        configure?.Invoke(builder);
        state.AddColumn(builder.Build(name, type));
        return this;
    }

    private StorageDeclarationBuilder AddIndex(string name, Action<IndexBuilder> configure, bool unique)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));
        var builder = new IndexBuilder();
        configure(builder);
        state.AddIndex(name, builder.Columns, unique);
        return this;
    }
}

/// <summary>Column policy options shared by all declaration families.</summary>
public sealed class ColumnBuilder
{
    private bool? isNullable;
    private int? maxLength;
    private int? precision;
    private int? scale;
    private PortableCollation? collation;
    private object? defaultValue;
    private bool hasDefault;
    private ColumnGeneration generation = ColumnGeneration.Supplied;

    public ColumnBuilder Required() { isNullable = false; return this; }

    public ColumnBuilder Nullable() { isNullable = true; return this; }

    public ColumnBuilder MaxLength(int value) { maxLength = value; return this; }

    public ColumnBuilder Precision(int value, int scaleValue)
    {
        precision = value;
        scale = scaleValue;
        return this;
    }

    public ColumnBuilder Collation(PortableCollation value) { collation = value; return this; }

    public ColumnBuilder Default(object? value)
    {
        defaultValue = value;
        hasDefault = true;
        return this;
    }

    public ColumnBuilder ProviderSequence() { generation = ColumnGeneration.ProviderSequence; return this; }

    internal ColumnBuilder InferNullable(bool value)
    {
        isNullable ??= value;
        return this;
    }

    internal ColumnDefinition Build(string name, PortableType type) => new()
    {
        Name = RequireName(name),
        Type = type,
        IsNullable = isNullable ?? true,
        MaxLength = maxLength,
        Precision = precision,
        Scale = scale,
        Collation = collation,
        Default = hasDefault ? new PortableDefault(DefaultValueSnapshot.Create(defaultValue, type)) : null,
        Generation = generation
    };

    private static string RequireName(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty column name is required.", nameof(value))
            : value;
}

/// <summary>Sort options for an index declaration.</summary>
public sealed class IndexBuilder
{
    private readonly List<IndexColumn> columns = [];

    internal IReadOnlyList<IndexColumn> Columns => columns;

    public IndexBuilder Ascending(string column)
    {
        columns.Add(new IndexColumn(column, SortDirection.Ascending));
        return this;
    }

    public IndexBuilder Descending(string column)
    {
        columns.Add(new IndexColumn(column, SortDirection.Descending));
        return this;
    }

    /// <summary>Alias for Ascending, useful when composing a neutral index declaration.</summary>
    public IndexBuilder Column(string column) => Ascending(column);
}

/// <summary>Authoring state for one closed aggregation profile.</summary>
public sealed class AggregationBuilder
{
    private readonly string name;
    private readonly List<string> groupBy = [];
    private readonly List<Aggregate> aggregates = [];

    internal AggregationBuilder(string name) => this.name = name;

    public AggregationBuilder GroupBy(params string[] columns)
    {
        groupBy.AddRange(columns ?? throw new ArgumentNullException(nameof(columns)));
        return this;
    }

    public AggregationBuilder Min(string alias, string column)
    {
        aggregates.Add(new Aggregate.Min(alias, column));
        return this;
    }

    public AggregationBuilder Max(string alias, string column)
    {
        aggregates.Add(new Aggregate.Max(alias, column));
        return this;
    }

    public AggregationBuilder Sum(string alias, string column)
    {
        aggregates.Add(new Aggregate.Sum(alias, column));
        return this;
    }

    public AggregationBuilder SetUnion(string alias, string column, int maxValues)
    {
        aggregates.Add(new Aggregate.SetUnion(alias, column, maxValues));
        return this;
    }

    public AggregationBuilder FirstBy(
        string alias,
        string column,
        string orderBy,
        SortDirection direction = SortDirection.Ascending)
    {
        aggregates.Add(new Aggregate.FirstBy(alias, column, orderBy, direction));
        return this;
    }

    internal AggregationProfile Build() => new()
    {
        Name = RequireName(name, nameof(name)),
        GroupByColumns = Array.AsReadOnly(groupBy.ToArray()),
        Aggregates = Array.AsReadOnly(aggregates.ToArray()),
        AllowedPredicates = []
    };

    private static string RequireName(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
}

internal sealed class StorageDeclarationState
{
    private readonly List<ColumnDefinition> columns = [];
    private readonly List<IndexDefinition> indexes = [];
    private readonly List<AggregationProfile> aggregationProfiles = [];
    private readonly string id;
    private readonly string name;
    private KeyDefinition? key;
    private ConcurrencyDeclaration concurrency = ConcurrencyDeclaration.None;
    private ScopePolicy scope = ScopePolicy.Global;
    private RetentionDeclaration? retention;
    private AppendIdempotencyDeclaration? appendIdempotency;
    private RetentionIdempotencyDeclaration? retentionIdempotency;

    public StorageDeclarationState(string id, string name)
    {
        this.id = RequireText(id, nameof(id));
        this.name = RequireText(name, nameof(name));
    }

    public IReadOnlyList<ColumnDefinition> Columns => columns;

    public void AddColumn(ColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (columns.Any(column => string.Equals(column.Name, definition.Name, StringComparison.Ordinal)))
            throw new ArgumentException($"Column '{definition.Name}' is already declared.", nameof(definition));
        columns.Add(definition);
    }

    public void ReplaceColumn(ColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var index = columns.FindIndex(column => string.Equals(column.Name, definition.Name, StringComparison.Ordinal));
        if (index < 0)
            throw new ArgumentException($"Column '{definition.Name}' is not declared.", nameof(definition));
        columns[index] = definition;
    }

    public void SetKey(IEnumerable<string> columnNames) => key = new KeyDefinition { Columns = SnapshotNames(columnNames, "key") };

    public void SetOptimisticConcurrency(string tokenColumn)
    {
        if (string.IsNullOrWhiteSpace(tokenColumn))
            throw new ArgumentException("A concurrency token column must be non-empty.", nameof(tokenColumn));

        var existing = columns.FindIndex(column => string.Equals(column.Name, tokenColumn, StringComparison.Ordinal));
        var token = existing < 0
            ? new ColumnDefinition { Name = tokenColumn, Type = PortableType.Int64, IsNullable = false, Default = new PortableDefault(0L) }
            : columns[existing];
        if (existing < 0)
            columns.Add(token);
        else if (token.Type == PortableType.Int64 && !token.IsNullable && token.Default is null)
            columns[existing] = token with { Default = new PortableDefault(0L) };

        concurrency = ConcurrencyDeclaration.Optimistic(tokenColumn);
    }

    public void AddIndex(string name, IEnumerable<IndexColumn> indexColumns, bool unique)
    {
        var indexName = RequireText(name, nameof(name));
        if (indexes.Any(index => string.Equals(index.Name, indexName, StringComparison.Ordinal)))
            throw new ArgumentException($"Index '{indexName}' is already declared.", nameof(name));
        var columns = (indexColumns ?? throw new ArgumentNullException(nameof(indexColumns))).ToArray();
        if (columns.Length == 0)
            throw new ArgumentException("An index requires at least one column.", nameof(indexColumns));
        indexes.Add(new IndexDefinition { Name = indexName, Columns = Array.AsReadOnly(columns), IsUnique = unique });
    }

    public void AddAggregation(AggregationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (aggregationProfiles.Any(existing => string.Equals(existing.Name, profile.Name, StringComparison.Ordinal)))
            throw new ArgumentException($"Aggregation profile '{profile.Name}' is already declared.", nameof(profile));
        aggregationProfiles.Add(profile);
    }

    public void SetScope(ScopePolicy value) => scope = value;

    public void SetRetention(RetentionDeclaration declaration) => retention = declaration ?? throw new ArgumentNullException(nameof(declaration));

    public void SetAppendIdempotency(AppendIdempotencyDeclaration declaration) =>
        appendIdempotency = declaration ?? throw new ArgumentNullException(nameof(declaration));

    public void SetRetentionIdempotency(RetentionIdempotencyDeclaration declaration) =>
        retentionIdempotency = declaration ?? throw new ArgumentNullException(nameof(declaration));

    public StorageUnit Build(PortabilityValidationContext? context)
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(id),
            Name = name,
            Columns = Array.AsReadOnly(columns.ToArray()),
            Key = new KeyDefinition { Columns = Array.AsReadOnly((key?.Columns ?? []).ToArray()) },
            Indexes = Array.AsReadOnly(indexes.ToArray()),
            AggregationProfiles = Array.AsReadOnly(aggregationProfiles.Select(AggregationProfileSnapshot.Capture).ToArray()),
            Scope = scope,
            Concurrency = concurrency,
            AppendIdempotency = appendIdempotency,
            RetentionIdempotency = retentionIdempotency,
            Retention = retention
        };

        var declarationFindings = ValidateReferences(unit, key is null).ToList();
        try
        {
            ConcurrencyDeclaration.ValidateDeclaration(unit);
        }
        catch (ArgumentException exception)
        {
            declarationFindings.Add(new DeclarationFinding(
                "GW-DECL-CONCURRENCY-001",
                $"The concurrency declaration is invalid: {exception.Message}",
                "concurrency"));
        }
        var validationContext = context is null || context.Retention is not null || retention is null
            ? context
            : new PortabilityValidationContext(
                context.TargetIdentities,
                retention,
                context.PriorAppliedMongoCompositeKeyOrder);
        var diagnostics = declarationFindings
            .Concat(BuilderPortabilityValidation.Validate(unit, validationContext).Refusals.Select(refusal =>
                new DeclarationFinding(refusal.Code, refusal.Message, refusal.Path)))
            .ToArray();
        if (diagnostics.Length != 0)
            throw new DeclarationBuildException(diagnostics);

        AggregationProfileValidator.ValidateUnit(unit);
        appendIdempotency?.Validate(unit);
        return unit;
    }

    private static IReadOnlyList<DeclarationFinding> ValidateReferences(StorageUnit unit, bool missingKey)
    {
        var diagnostics = new List<DeclarationFinding>();
        if (missingKey)
            diagnostics.Add(new("GW-DECL-KEY-001", "A storage declaration requires a key before Build().", "key"));

        var declaredColumns = unit.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var keyColumns = unit.Key.Columns ?? [];
        var seenKeyColumns = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < keyColumns.Count; index++)
        {
            var column = keyColumns[index];
            if (string.IsNullOrWhiteSpace(column) || !declaredColumns.Contains(column))
                diagnostics.Add(new("GW-DECL-KEY-002", $"Key column '{column}' is not declared on the storage unit.", $"key.columns[{index}]"));
            if (!string.IsNullOrWhiteSpace(column) && !seenKeyColumns.Add(column))
                diagnostics.Add(new("GW-DECL-KEY-003", $"Key column '{column}' is listed more than once.", "key.columns"));
        }

        foreach (var index in unit.Indexes)
        {
            var seenIndexColumns = new HashSet<string>(StringComparer.Ordinal);
            for (var columnIndex = 0; columnIndex < index.Columns.Count; columnIndex++)
            {
                var indexColumn = index.Columns[columnIndex];
                var column = indexColumn.Column;
                if (string.IsNullOrWhiteSpace(column) || !declaredColumns.Contains(column))
                    diagnostics.Add(new("GW-DECL-INDEX-001", $"Index '{index.Name}' column '{column}' is not declared on the storage unit.", $"indexes.{index.Name}.columns[{columnIndex}]"));
                if (!string.IsNullOrWhiteSpace(column) && !seenIndexColumns.Add(column))
                    diagnostics.Add(new("GW-DECL-INDEX-002", $"Index '{index.Name}' column '{column}' is listed more than once.", $"indexes.{index.Name}.columns"));
                var declaration = unit.Columns.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, column, StringComparison.Ordinal));
                if (declaration?.Type == PortableType.Json)
                    diagnostics.Add(new(
                        "GW-DECL-INDEX-003",
                        $"Index '{index.Name}' column '{column}' is JSON and cannot be represented as a portable query index key. Leave the JSON column unindexed or index a declared scalar projection instead.",
                        $"indexes.{index.Name}.columns[{columnIndex}]"));
            }
        }

        return diagnostics;
    }

    private static IReadOnlyList<string> SnapshotNames(IEnumerable<string> names, string parameterName)
    {
        var snapshot = (names ?? throw new ArgumentNullException(parameterName)).ToArray();
        if (snapshot.Length == 0 || snapshot.Any(name => string.IsNullOrWhiteSpace(name)))
            throw new ArgumentException("At least one non-empty column name is required.", parameterName);
        return Array.AsReadOnly(snapshot);
    }

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
}

public sealed class DeclarationFinding
{
    public DeclarationFinding(string code, string message, string path)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Code { get; }
    public string Message { get; }
    public string Path { get; }
}

public sealed class DeclarationBuildException : Exception
{
    public DeclarationBuildException(IEnumerable<DeclarationFinding> findings)
        : this(Snapshot(findings))
    {
    }

    private DeclarationBuildException(DeclarationFinding[] findings)
        : base("The storage declaration is not portable: " + string.Join("; ", findings.Select(finding => finding.Code + ": " + finding.Message))) =>
        Findings = Array.AsReadOnly(findings);

    public IReadOnlyList<DeclarationFinding> Findings { get; }

    private static DeclarationFinding[] Snapshot(IEnumerable<DeclarationFinding> findings)
    {
        if (findings is null)
            throw new ArgumentNullException(nameof(findings));
        var snapshot = findings.ToArray();
        if (snapshot.Any(finding => finding is null))
            throw new ArgumentException("Findings cannot contain null references.", nameof(findings));
        return snapshot;
    }
}

internal static class DefaultValueSnapshot
{
    public static object? Create(object? value, PortableType type)
    {
        var active = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Snapshot(value, type, active);
    }

    private static object? Snapshot(object? value, PortableType type, ISet<object> active)
    {
        if (value is null || IsImmutable(value))
            return value;
        if (value is byte[] bytes)
            return (byte[])bytes.Clone();
        if (type != PortableType.Json)
            throw new ArgumentException("Mutable default values are supported only for byte[] and JSON object/array graphs.", nameof(value));
        if (!active.Add(value))
            throw new ArgumentException("JSON default values cannot contain reference cycles.", nameof(value));
        try
        {
            if (value is IDictionary dictionary)
                return SnapshotDictionary(dictionary, type, active);
            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
                return SnapshotReadOnlyDictionary(readOnlyDictionary, type, active);
            if (value is IEnumerable sequence)
                return SnapshotSequence(sequence, type, active);
        }
        finally
        {
            active.Remove(value);
        }
        throw new ArgumentException("JSON defaults must be scalars, byte arrays, dictionaries with string keys, or enumerable arrays/lists.", nameof(value));
    }

    private static Dictionary<string, object?> SnapshotDictionary(IDictionary dictionary, PortableType type, ISet<object> active)
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string key)
                throw new ArgumentException("JSON object default keys must be strings.", nameof(dictionary));
            snapshot[key] = Snapshot(entry.Value, type, active);
        }
        return snapshot;
    }

    private static Dictionary<string, object?> SnapshotReadOnlyDictionary(IReadOnlyDictionary<string, object?> dictionary, PortableType type, ISet<object> active)
    {
        var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in dictionary)
            snapshot[entry.Key] = Snapshot(entry.Value, type, active);
        return snapshot;
    }

    private static List<object?> SnapshotSequence(IEnumerable sequence, PortableType type, ISet<object> active)
    {
        var snapshot = new List<object?>();
        foreach (var item in sequence)
            snapshot.Add(Snapshot(item, type, active));
        return snapshot;
    }

    private static bool IsImmutable(object value) => value is
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or
        float or double or decimal or char or DateTime or DateTimeOffset or Guid;

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();
        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
}
