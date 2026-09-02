using Groundwork.Kernel.Schema;

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

    /// <summary>
    /// Adds a storage-only IEEE-754 binary64 column. The value round-trips bit-for-bit on every
    /// supported store, but the column cannot be compared: keys, indexes, predicates, ordering,
    /// and grouping are refused. Declare <see cref="Decimal"/> or <see cref="Int64"/> for values
    /// you query on.
    /// </summary>
    public StorageDeclarationBuilder Double(string name, Action<ColumnBuilder>? configure = null) =>
        AddColumn(name, PortableType.Double, configure);

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
    public StorageDeclarationBuilder RetentionIdempotency(TimeSpan window, string ledgerName = ProviderReservedLedgerNames.DefaultRetentionLedger)
    {
        state.SetRetentionIdempotency(new RetentionIdempotencyDeclaration { Window = window, LedgerName = ledgerName });
        return this;
    }

    public StorageDeclarationBuilder Scoped()
    {
        state.SetScope(ScopePolicy.Scoped);
        return this;
    }

    /// <summary>
    /// Declares one provider-native reporting view. For a scoped unit the view exposes
    /// <see cref="ProviderOwnedColumns.Scope"/> and therefore requires database-level read grants.
    /// </summary>
    public StorageDeclarationBuilder InteropView(string name)
    {
        state.SetInteropView(new InteropViewDeclaration(name));
        return this;
    }

    /// <summary>
    /// Coexists with a catalog another tool extends: a deployed column this declaration does not
    /// describe stops being fatal at admission and is reported as a warning instead — but only when
    /// the database supplies a value for it, so a column Groundwork could never write around stays
    /// a refusal. Nothing else about drift changes.
    /// </summary>
    public StorageDeclarationBuilder TolerateForeignColumns()
    {
        state.SetForeignColumns(ForeignColumnPolicy.TolerateDatabaseSupplied);
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

    /// <summary>
    /// Declares a logical-only relationship to <paramref name="target"/>'s key. The target is
    /// snapshotted so key shape, scope, and portable column types are validated when this builder
    /// is built, together with the required covering key or index.
    /// </summary>
    public StorageDeclarationBuilder Reference(string name, StorageUnit target, params string[] columns)
    {
        state.AddReference(name, target, columns);
        return this;
    }

    /// <summary>
    /// Declares a logical-only relationship by target identity. This form is used when a canonical
    /// schema is assembled before all units exist; target-dependent validation occurs when the
    /// complete manifest is validated. The built reference records this source's scope as the
    /// target policy required by the same-scope invariant.
    /// </summary>
    public StorageDeclarationBuilder Reference(string name, StorageUnitId targetUnitId, params string[] columns)
    {
        state.AddReference(name, targetUnitId, columns);
        return this;
    }

    /// <summary>
    /// Declares a logical-only relationship by target identity and its known scope policy. This
    /// explicit form lets callers preserve independently resolved target metadata; providers
    /// refuse a policy that differs from the source before reading target state.
    /// </summary>
    public StorageDeclarationBuilder Reference(
        string name,
        StorageUnitId targetUnitId,
        ScopePolicy targetScope,
        params string[] columns)
    {
        state.AddReference(name, targetUnitId, targetScope, columns);
        return this;
    }

    /// <summary>
    /// Declares a database-enforced relationship to <paramref name="target"/>'s key. The logical
    /// reference remains available to query planning; relational schema planning additionally
    /// emits a physical foreign-key operation.
    /// </summary>
    public StorageDeclarationBuilder PhysicalReference(string name, StorageUnit target, params string[] columns)
    {
        state.AddPhysicalReference(name, target, columns);
        return this;
    }

    /// <summary>Adds a named portable comparison check over one declared column.</summary>
    public StorageDeclarationBuilder Check(
        string name,
        string column,
        CheckConstraintOperator @operator,
        object? value)
    {
        state.AddCheck(new CheckConstraintDefinition
        {
            Name = name,
            Column = column,
            Operator = @operator,
            Value = new PortableDefault(value)
        });
        return this;
    }

    /// <summary>Adds an explicitly constructed portable check declaration.</summary>
    public StorageDeclarationBuilder Check(CheckConstraintDefinition definition)
    {
        state.AddCheck(definition);
        return this;
    }

    public StorageDeclarationBuilder AppendIdempotency(TimeSpan window, string ledgerName = ProviderReservedLedgerNames.DefaultAppendLedger)
    {
        state.SetAppendIdempotency(new AppendIdempotencyDeclaration { Window = window, LedgerName = ledgerName });
        return this;
    }

    public StorageDeclarationBuilder Aggregate(string name, Action<AggregationBuilder> configure)
    {
        state.AddAggregation(AggregationProfile.Create(name, configure));
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
        state.AddIndex(name, builder.Columns, unique, builder.MissingValues);
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
    private LocaleSortKeyDefinition? localeSortKey;
    private ElementSearchKeyDefinition? elementSearchKey;
    private object? defaultValue;
    private bool hasDefault;
    private ColumnGeneration generation = ColumnGeneration.Supplied;
    private string? logicalId;

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

    /// <summary>
    /// Orders this string through a persisted ICU sort key. The expansion factor is the maximum
    /// encoded key length per source UTF-16 code unit and is enforced during writes and backfills.
    /// </summary>
    public ColumnBuilder LocaleOrder(string cultureName, int maximumExpansionFactor)
    {
        localeSortKey = new LocaleSortKeyDefinition
        {
            CultureName = cultureName,
            MaximumExpansionFactor = maximumExpansionFactor
        };
        return this;
    }

    /// <summary>
    /// Persists a provider-owned parallel JSON search-key array for this JSON column. Each valid
    /// string element is encoded with the declared folded policy; null and non-string elements
    /// retain their position as JSON null.
    /// </summary>
    public ColumnBuilder ElementSearchKey(
        PortableCollation collation,
        int? maximumElementCodeUnits = null)
    {
        elementSearchKey = new ElementSearchKeyDefinition
        {
            Collation = collation,
            MaximumElementCodeUnits = maximumElementCodeUnits
        };
        return this;
    }

    public ColumnBuilder Default(object? value)
    {
        defaultValue = value;
        hasDefault = true;
        return this;
    }

    public ColumnBuilder ProviderSequence() { generation = ColumnGeneration.ProviderSequence; return this; }

    /// <summary>
    /// Pins the column's stable logical identity, which defaults to its physical name. Spell it
    /// only when renaming the column, keeping the original name as the id, so schema planning
    /// recognises the change as a rename rather than a drop and an add.
    /// </summary>
    public ColumnBuilder LogicalId(string value) { logicalId = value; return this; }

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
        LocaleSortKey = localeSortKey,
        ElementSearchKey = elementSearchKey,
        Default = hasDefault ? new PortableDefault(DefaultValueSnapshot.Create(defaultValue, type)) : null,
        Generation = generation,
        Id = logicalId
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
    private MissingValueBehavior missingValues = MissingValueBehavior.Included;

    internal IReadOnlyList<IndexColumn> Columns => columns;
    internal MissingValueBehavior MissingValues => missingValues;

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

    /// <summary>Excludes missing values from this index, enabling sparse uniqueness for nullable columns.</summary>
    public IndexBuilder ExcludeMissingValues()
    {
        missingValues = MissingValueBehavior.Excluded;
        return this;
    }
}

/// <summary>Authoring state for one closed aggregation profile.</summary>
public sealed class AggregationBuilder
{
    private readonly string name;
    private readonly List<string> groupBy = [];
    private readonly List<AggregationGroup> groupByExpressions = [];
    private readonly List<Aggregate> aggregates = [];

    internal AggregationBuilder(string name) => this.name = name;

    public AggregationBuilder GroupBy(params string[] columns)
    {
        groupBy.AddRange(columns ?? throw new ArgumentNullException(nameof(columns)));
        return this;
    }

    public AggregationBuilder GroupBy(AggregationGroup expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        groupByExpressions.Add(expression);
        return this;
    }

    public AggregationBuilder FixedUtcBucket(string alias, string column, TimeSpan width) =>
        GroupBy(AggregationGroup.TimeBucket.FixedUtc(alias, column, width));

    public AggregationBuilder LocalCalendarDayBucket(string alias, string column) =>
        GroupBy(AggregationGroup.TimeBucket.LocalCalendarDay(alias, column));

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

    public AggregationBuilder Count(string alias)
    {
        aggregates.Add(new Aggregate.Count(alias));
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
        GroupByExpressions = Array.AsReadOnly(groupByExpressions.ToArray()),
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
    private readonly List<ReferenceDeclarationState> references = [];
    private readonly List<CheckConstraintDefinition> checkConstraints = [];
    private readonly List<AggregationProfile> aggregationProfiles = [];
    private readonly string id;
    private readonly string name;
    private KeyDefinition? key;
    private ConcurrencyDeclaration concurrency = ConcurrencyDeclaration.None;
    private ScopePolicy scope = ScopePolicy.Global;
    private ForeignColumnPolicy foreignColumns = ForeignColumnPolicy.Refuse;
    private InteropViewDeclaration? interopView;
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

    public void AddIndex(
        string name,
        IEnumerable<IndexColumn> indexColumns,
        bool unique,
        MissingValueBehavior missingValues = MissingValueBehavior.Included)
    {
        var indexName = RequireText(name, nameof(name));
        if (indexes.Any(index => string.Equals(index.Name, indexName, StringComparison.Ordinal)))
            throw new ArgumentException($"Index '{indexName}' is already declared.", nameof(name));
        var columns = (indexColumns ?? throw new ArgumentNullException(nameof(indexColumns))).ToArray();
        if (columns.Length == 0)
            throw new ArgumentException("An index requires at least one column.", nameof(indexColumns));
        indexes.Add(new IndexDefinition
        {
            Name = indexName,
            Columns = Array.AsReadOnly(columns),
            IsUnique = unique,
            MissingValues = missingValues
        });
    }

    public void AddAggregation(AggregationProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (aggregationProfiles.Any(existing => string.Equals(existing.Name, profile.Name, StringComparison.Ordinal)))
            throw new ArgumentException($"Aggregation profile '{profile.Name}' is already declared.", nameof(profile));
        aggregationProfiles.Add(profile);
    }

    public void AddReference(string name, StorageUnit target, IEnumerable<string> columnNames)
    {
        ArgumentNullException.ThrowIfNull(target);
        AddReference(name, target.Id, target.Scope, columnNames, new SchemaSubject(target).Definition);
    }

    public void AddReference(string name, StorageUnitId targetUnitId, IEnumerable<string> columnNames) =>
        AddReference(name, targetUnitId, targetScope: null, columnNames, target: null);

    public void AddReference(
        string name,
        StorageUnitId targetUnitId,
        ScopePolicy targetScope,
        IEnumerable<string> columnNames) =>
        AddReference(name, targetUnitId, targetScope, columnNames, target: null);

    public void AddPhysicalReference(string name, StorageUnit target, IEnumerable<string> columnNames)
    {
        ArgumentNullException.ThrowIfNull(target);
        AddReference(name, target.Id, target.Scope, columnNames, new SchemaSubject(target).Definition, ReferenceEnforcement.Physical);
    }

    private void AddReference(
        string name,
        StorageUnitId targetUnitId,
        ScopePolicy? targetScope,
        IEnumerable<string> columnNames,
        StorageUnit? target,
        ReferenceEnforcement enforcement = ReferenceEnforcement.LogicalOnly)
    {
        var referenceName = RequireText(name, nameof(name));
        if (references.Any(reference => string.Equals(reference.Definition.Name, referenceName, StringComparison.Ordinal)))
            throw new ArgumentException($"Reference '{referenceName}' is already declared.", nameof(name));
        var snapshot = (columnNames ?? throw new ArgumentNullException(nameof(columnNames))).ToArray();
        references.Add(new ReferenceDeclarationState(
            new ReferenceDefinition
            {
                Name = referenceName,
                Columns = Array.AsReadOnly(snapshot),
                TargetUnitId = targetUnitId,
                TargetScope = targetScope,
                Enforcement = enforcement
            },
            target));
    }

    public void AddCheck(CheckConstraintDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var name = RequireText(definition.Name, nameof(definition));
        if (checkConstraints.Any(check => string.Equals(check.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException($"Check constraint '{name}' is already declared.", nameof(definition));
        checkConstraints.Add(definition with
        {
            Name = name,
            Column = RequireText(definition.Column, nameof(definition)),
            Value = definition.Value is null
                ? throw new ArgumentException("A check constraint requires a value wrapper.", nameof(definition))
                : new PortableDefault(definition.Value.Value)
        });
    }

    public void SetScope(ScopePolicy value) => scope = value;

    public void SetForeignColumns(ForeignColumnPolicy value) => foreignColumns = value;

    public void SetInteropView(InteropViewDeclaration declaration) =>
        interopView = declaration ?? throw new ArgumentNullException(nameof(declaration));

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
            References = Array.AsReadOnly(references.Select(reference => reference.SnapshotDefinition(scope)).ToArray()),
            CheckConstraints = Array.AsReadOnly(checkConstraints.Select(check => check with
            {
                Value = new PortableDefault(check.Value.Value)
            }).ToArray()),
            AggregationProfiles = Array.AsReadOnly(aggregationProfiles.Select(AggregationProfileSnapshot.Capture).ToArray()),
            InteropView = interopView is null ? null : new InteropViewDeclaration(interopView.Name),
            Scope = scope,
            ForeignColumns = foreignColumns,
            Concurrency = concurrency,
            AppendIdempotency = appendIdempotency,
            RetentionIdempotency = retentionIdempotency,
            Retention = retention
        };

        var declarationFindings = StorageDeclarationReferenceValidation.Validate(unit, key is null).ToList();
        declarationFindings.AddRange(StorageReferenceValidation.ValidateTargets(
            unit,
            references.Where(reference => reference.Target is not null)
                .ToDictionary(reference => reference.Definition.Name, reference => reference.Target!, StringComparer.Ordinal)));
        try
        {
            ProviderOwnedColumns.ValidateReservedLogicalNames(unit);
        }
        catch (ArgumentException exception)
        {
            declarationFindings.Add(new DeclarationFinding(
                "GW-DECL-COLUMN-001",
                exception.Message,
                "columns"));
        }
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
        try
        {
            if (retentionIdempotency is not null)
                RetentionIdempotencyDeclaration.ValidateOwner(unit);
        }
        catch (ArgumentException exception)
        {
            declarationFindings.Add(new DeclarationFinding(
                RetentionIdempotencyDeclaration.MissingRetentionDiagnosticCode,
                exception.Message,
                "retentionIdempotency"));
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
        PhysicalConstraintValidation.ThrowIfInvalid(unit);
        appendIdempotency?.Validate(unit);
        return unit;
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

internal sealed record ReferenceDeclarationState(ReferenceDefinition Definition, StorageUnit? Target)
{
    public ReferenceDefinition SnapshotDefinition(ScopePolicy sourceScope) => Definition with
    {
        Columns = Array.AsReadOnly((Definition.Columns ?? []).ToArray()),
        TargetScope = Definition.TargetScope ?? sourceScope,
        TargetName = Definition.Enforcement == ReferenceEnforcement.Physical ? Target?.Name : Definition.TargetName,
        TargetKeyColumns = Definition.Enforcement == ReferenceEnforcement.Physical
            ? (Target?.Key.Columns.ToArray() ?? Definition.TargetKeyColumns?.ToArray())
            : Definition.TargetKeyColumns?.ToArray(),
        TargetKeyHasProviderSequence = Definition.Enforcement == ReferenceEnforcement.Physical
            ? Target?.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence) ??
              Definition.TargetKeyHasProviderSequence
            : Definition.TargetKeyHasProviderSequence
    };
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
    public static object? Create(object? value, PortableType type) =>
        PortabilityValidator.IsPortableDefaultValue(type, value)
            ? SchemaValue.Snapshot(value, type)
            : value;
}
