namespace Groundwork.Kernel;

/// <summary>A provider-neutral refusal emitted by the portability validator.</summary>
public sealed record PortabilityRefusal(string Code, string Message, string Path);

/// <summary>The accumulated result of validating one storage declaration.</summary>
public sealed class PortabilityValidationResult
{
    public PortabilityValidationResult(IEnumerable<PortabilityRefusal>? refusals)
    {
        Refusals = Array.AsReadOnly((refusals ?? []).ToArray());
    }

    public IReadOnlyList<PortabilityRefusal> Refusals { get; }

    public bool IsPortable => Refusals.Count == 0;
}

/// <summary>The count-based retention policy attached to a storage unit.</summary>
public enum RetentionTrigger
{
    Explicit,
    OnAppend
}

/// <summary>
/// Declares how many newest rows are retained, optionally independently for each partition.
/// The order column is deliberately a logical column name; providers bind it only after the
/// declaration has passed the provider-neutral portability validator.
/// </summary>
public sealed record RetentionDeclaration
{
    /// <summary>Compatibility constructor for the original K2 validation-only shape.</summary>
    [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
    public RetentionDeclaration(string orderColumn)
    {
        KeepNewest = 1;
        OrderColumn = orderColumn;
    }

    public RetentionDeclaration()
    {
    }

    /// <summary>
    /// The number of newest rows retained per partition. Zero is valid and deletes every
    /// retained row while leaving ProviderSequence lifetime high-water evidence intact.
    /// </summary>
    public required int KeepNewest { get; init; }

    public required string OrderColumn { get; init; }

    public IReadOnlyList<string> PartitionColumns { get; init; } = [];

    public RetentionTrigger Trigger { get; init; } = RetentionTrigger.Explicit;
}

/// <summary>
/// Provider-neutral information supplied by later builder, manifest, and schema-target slices.
/// This is validation context only; it is not a storage declaration or a runtime target.
/// </summary>
public sealed class PortabilityValidationContext
{
    public PortabilityValidationContext(
        IEnumerable<string>? targetIdentities = null,
        RetentionDeclaration? retention = null,
        IEnumerable<string>? priorAppliedMongoCompositeKeyOrder = null)
    {
        TargetIdentities = Array.AsReadOnly((targetIdentities ?? []).ToArray());
        Retention = retention;
        PriorAppliedMongoCompositeKeyOrder = Array.AsReadOnly((priorAppliedMongoCompositeKeyOrder ?? []).ToArray());
    }

    public IReadOnlyList<string> TargetIdentities { get; }

    public RetentionDeclaration? Retention { get; }

    public IReadOnlyList<string> PriorAppliedMongoCompositeKeyOrder { get; }
}

/// <summary>
/// Accumulates the provider-neutral portability refusals. The builder, manifest, and schema-target
/// classes below are deliberately small invocation seams for later slices, not finished subsystems.
/// </summary>
public static class PortabilityValidator
{
    public const int StrictIndexKeyByteBudget = 1700;
    /// <summary>
    /// The maximum UTF-8 byte length for a portable physical identifier. The grammar below is
    /// ASCII-only, so this is also its maximum character count. It is the shared ceiling for
    /// provider-rendered names, including PostgreSQL's native identifier limit.
    /// </summary>
    public const int MaximumPortableIdentifierLength = 63;

    private const int MinimumPortableDecimalPrecision = 1;
    private const int MaximumPortableDecimalPrecision = 38;

    public static PortabilityValidationResult Validate(
        StorageUnit? unit,
        PortabilityValidationContext? context = null)
    {
        var diagnostics = new List<PortabilityRefusal>();
        if (unit is null)
        {
            diagnostics.Add(new(
                "GW-PORT-000",
                "A storage unit is required for portability validation.",
                "storageUnit"));
            return new(diagnostics);
        }

        context ??= new PortabilityValidationContext();
        var columns = unit.Columns ?? [];
        var indexes = unit.Indexes ?? [];
        var byName = columns
            .Where(column => column is not null && column.Name is not null)
            .GroupBy(column => column.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        ValidatePhysicalIdentifiers(unit, columns, indexes, diagnostics);
        ValidateDuplicatePhysicalIndexSignatures(indexes, diagnostics);
        ValidateUniqueNullability(indexes, byName, diagnostics);
        ValidateDecimalShape(columns, diagnostics);
        ValidateBoundedIndexKeys(indexes, byName, diagnostics);
        ValidateIndexBudget(indexes, byName, diagnostics);
        ValidateGeneration(unit, columns, diagnostics);
        ValidateCollation(columns, diagnostics);
        ValidateRetention(unit.Retention ?? context.Retention, byName, diagnostics);
        ValidateMongoKeyOrder(unit, context, diagnostics);

        return new(diagnostics);
    }

    internal static PortabilityValidationResult ValidatePhysicalIdentifiers(StorageUnit? unit)
    {
        if (unit is null)
        {
            return new([new(
                "GW-PORT-000",
                "A storage unit is required for portability validation.",
                "storageUnit")]);
        }

        var diagnostics = new List<PortabilityRefusal>();
        ValidatePhysicalIdentifiers(unit, unit.Columns ?? [], unit.Indexes ?? [], diagnostics);
        return new(diagnostics);
    }

    /// <summary>
    /// Fails before provider work when a declaration contains a non-portable rendered unit or
    /// index identifier. Other portability rules remain available to provider-specific schema
    /// validation and are intentionally not applied by this pre-I/O name boundary.
    /// </summary>
    public static void EnsurePhysicalIdentifiers(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ThrowIfInvalid(ValidatePhysicalIdentifiers(unit));
    }

    /// <summary>
    /// Validates one provider-rendered identifier using the same grammar and budget applied to a
    /// complete storage declaration. The path is included in the stable diagnostic so provider
    /// adapters can fail before emitting native DDL or opening a physical collection.
    /// </summary>
    public static PortabilityValidationResult ValidatePhysicalIdentifier(
        string? identifier,
        string path,
        int maximumByteLength = MaximumPortableIdentifierLength,
        bool allowProviderOwnedPrefix = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumByteLength);
        var diagnostics = new List<PortabilityRefusal>();
        ValidateIdentifier(identifier, path, diagnostics, maximumByteLength, allowProviderOwnedPrefix);
        return new(diagnostics);
    }

    /// <summary>Fails when a provider-composed physical identifier exceeds its native budget.</summary>
    public static void EnsurePhysicalIdentifier(
        string identifier,
        string path,
        int maximumByteLength = MaximumPortableIdentifierLength,
        bool allowProviderOwnedPrefix = false)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        ThrowIfInvalid(ValidatePhysicalIdentifier(identifier, path, maximumByteLength, allowProviderOwnedPrefix));
    }

    internal static PortabilityValidationResult ValidateDuplicatePhysicalIndexSignatures(StorageUnit? unit)
    {
        if (unit is null)
        {
            return new([new(
                "GW-PORT-000",
                "A storage unit is required for portability validation.",
                "storageUnit")]);
        }

        var diagnostics = new List<PortabilityRefusal>();
        ValidateDuplicatePhysicalIndexSignatures(unit.Indexes ?? [], diagnostics);
        return new(diagnostics);
    }

    internal static PortabilityValidationResult ValidateRetention(StorageUnit? unit)
    {
        if (unit is null)
            return new([new(
                "GW-PORT-000",
                "A storage unit is required for portability validation.",
                "storageUnit")]);

        var byName = (unit.Columns ?? [])
            .Where(column => column is not null && column.Name is not null)
            .GroupBy(column => column.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var diagnostics = new List<PortabilityRefusal>();
        ValidateRetention(unit.Retention, byName, diagnostics);
        return new(diagnostics);
    }

    private static void ValidateDuplicatePhysicalIndexSignatures(
        IReadOnlyList<IndexDefinition> indexes,
        ICollection<PortabilityRefusal> diagnostics)
    {
        var firstBySignature = new Dictionary<PhysicalIndexSignature, IndexDefinition>();
        foreach (var index in indexes.Where(index => index is not null))
        {
            var signature = new PhysicalIndexSignature(index);
            if (!firstBySignature.TryGetValue(signature, out var first))
            {
                firstBySignature.Add(signature, index);
                continue;
            }

            diagnostics.Add(new(
                "GW-PORT-009",
                $"Indexes '{first.Name}' and '{index.Name}' have the same physical signature; " +
                "consolidate their query purposes onto one physical index.",
                $"indexes.{first.Name}|{index.Name}"));
        }
    }

    private static void ValidatePhysicalIdentifiers(
        StorageUnit unit,
        IReadOnlyList<ColumnDefinition> columns,
        IReadOnlyList<IndexDefinition> indexes,
        ICollection<PortabilityRefusal> diagnostics)
    {
        ValidateIdentifier(unit.Name, "name", diagnostics);

        var declaredPhysicalNames = columns
            .Where(column => column is not null)
            .Select(column => column.Name)
            .Concat((unit.DerivedColumns ?? []).Where(column => column is not null).Select(column => column.Name))
            .ToHashSet(StringComparer.Ordinal);
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            var column = columns[columnIndex];
            if (column is not null)
            {
                ValidateIdentifier(column.Name, $"columns.{column.Name}", diagnostics);

                if (column.Type == PortableType.String && column.Name is { } sourceName &&
                    !string.IsNullOrWhiteSpace(sourceName) &&
                    SearchKeyProjection.IsFolded(SearchKeyProjection.LogicalCollation(column)) &&
                    !declaredPhysicalNames.Contains(SearchKeyProjection.ColumnName(sourceName)))
                {
                    var searchKey = SearchKeyProjection.ColumnName(sourceName);
                    ValidateIdentifier(searchKey, $"derivedColumns.{searchKey}.name", diagnostics);
                }
            }
        }

        var keyColumns = unit.Key?.Columns ?? [];
        for (var keyIndex = 0; keyIndex < keyColumns.Count; keyIndex++)
            ValidateIdentifier(keyColumns[keyIndex], $"key.columns[{keyIndex}]", diagnostics);

        var indexNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var index in indexes.Where(index => index is not null))
        {
            ValidateIdentifier(index.Name, $"indexes.{index.Name}.name", diagnostics);
            if (index.Name is not null && indexNames.ContainsKey(index.Name))
            {
                diagnostics.Add(new(
                    "GW-PORT-011",
                    $"Physical index name '{index.Name}' is declared more than once; " +
                    "choose a unique name for each physical index.",
                    $"indexes.{index.Name}.name"));
            }
            else if (index.Name is not null)
            {
                indexNames.Add(index.Name, index.Name);
            }
            var indexColumns = index.Columns ?? [];
            for (var columnIndex = 0; columnIndex < indexColumns.Count; columnIndex++)
            {
                var column = indexColumns[columnIndex];
                ValidateIdentifier(
                    column?.Column,
                    $"indexes.{index.Name}.columns[{columnIndex}]",
                    diagnostics);
            }
        }

        foreach (var derived in (unit.DerivedColumns ?? []).Where(column => column is not null))
        {
            ValidateIdentifier(derived.Name, $"derivedColumns.{derived.Name}.name", diagnostics);
            ValidateIdentifier(derived.SourceColumn, $"derivedColumns.{derived.Name}.sourceColumn", diagnostics);
        }

        if (unit.Concurrency?.TokenColumn is { } tokenColumn)
            ValidateIdentifier(tokenColumn, "concurrency.tokenColumn", diagnostics);

        if (unit.Retention is { } retention)
        {
            ValidateIdentifier(retention.OrderColumn, "retention.orderColumn", diagnostics);
            foreach (var partition in retention.PartitionColumns ?? [])
                ValidateIdentifier(partition, "retention.partitionColumns", diagnostics);
        }

        foreach (var profile in (unit.AggregationProfiles ?? []).Where(profile => profile is not null))
        {
            var profilePath = "aggregationProfiles." + profile.Name;
            foreach (var groupBy in profile.GroupByColumns ?? [])
                ValidateIdentifier(groupBy, profilePath + ".groupByColumns", diagnostics);
            foreach (var aggregate in (profile.Aggregates ?? []).Where(aggregate => aggregate is not null))
            {
                ValidateIdentifier(aggregate.Alias, profilePath + ".aggregates", diagnostics);
                switch (aggregate)
                {
                    case Aggregate.Min min:
                        ValidateIdentifier(min.Column, profilePath + ".aggregates." + min.Alias, diagnostics);
                        break;
                    case Aggregate.Max max:
                        ValidateIdentifier(max.Column, profilePath + ".aggregates." + max.Alias, diagnostics);
                        break;
                    case Aggregate.Sum sum:
                        ValidateIdentifier(sum.Column, profilePath + ".aggregates." + sum.Alias, diagnostics);
                        break;
                    case Aggregate.SetUnion set:
                        ValidateIdentifier(set.Column, profilePath + ".aggregates." + set.Alias, diagnostics);
                        break;
                    case Aggregate.FirstBy first:
                        ValidateIdentifier(first.Column, profilePath + ".aggregates." + first.Alias, diagnostics);
                        ValidateIdentifier(first.OrderColumn, profilePath + ".aggregates." + first.Alias, diagnostics);
                        break;
                }
            }

            foreach (var allowance in (profile.AllowedPredicates ?? []).Where(allowance => allowance is not null))
                ValidateIdentifier(allowance.Alias, profilePath + ".allowedPredicates", diagnostics);
        }
    }

    private static void ValidateIdentifier(
        string? identifier,
        string path,
        ICollection<PortabilityRefusal> diagnostics,
        int maximumByteLength = MaximumPortableIdentifierLength,
        bool allowProviderOwnedPrefix = false)
    {
        if (IsPortableIdentifier(identifier, maximumByteLength, allowProviderOwnedPrefix))
            return;

        var display = identifier switch
        {
            null => "<null>",
            "" => "<empty>",
            _ => $"'{identifier}'"
        };
        diagnostics.Add(new(
            "GW-PORT-010",
            $"Physical identifier {display} is invalid; use ASCII letters, digits, and underscores, " +
            $"starting with a letter or underscore, keep it at most {maximumByteLength} ASCII bytes, and do not use the " +
            "'__groundwork_' provider-owned prefix; choose a shorter identifier when necessary.",
            path));
    }

    private static bool IsPortableIdentifier(
        string? identifier,
        int maximumByteLength = MaximumPortableIdentifierLength,
        bool allowProviderOwnedPrefix = false)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > maximumByteLength ||
            !IsIdentifierStart(identifier[0]))
            return false;
        for (var index = 1; index < identifier.Length; index++)
        {
            if (!IsIdentifierPart(identifier[index]))
                return false;
        }

        return allowProviderOwnedPrefix ||
            !identifier.StartsWith("__groundwork_", StringComparison.Ordinal) ||
            IsKnownProviderOwnedColumn(identifier);
    }

    private static void ThrowIfInvalid(PortabilityValidationResult result)
    {
        if (result.IsPortable)
            return;

        throw new InvalidOperationException(string.Join(
            Environment.NewLine,
            result.Refusals.Select(refusal =>
                $"{refusal.Code} at {refusal.Path}: {refusal.Message}")));
    }

    private static bool IsIdentifierStart(char value) =>
        value is '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');

    private static bool IsIdentifierPart(char value) =>
        IsIdentifierStart(value) || value is (>= '0' and <= '9');

    private static bool IsKnownProviderOwnedColumn(string identifier) =>
        ProviderOwnedColumns.IsAllowedPhysicalColumn(identifier);

    private sealed class PhysicalIndexSignature : IEquatable<PhysicalIndexSignature>
    {
        public PhysicalIndexSignature(IndexDefinition index)
        {
            IsUnique = index.IsUnique;
            MissingValues = index.MissingValues;
            Columns = (index.Columns ?? []).ToArray();
        }

        private bool IsUnique { get; }
        private MissingValueBehavior MissingValues { get; }
        private IReadOnlyList<IndexColumn> Columns { get; }

        public bool Equals(PhysicalIndexSignature? other) =>
            other is not null &&
            IsUnique == other.IsUnique &&
            MissingValues == other.MissingValues &&
            Columns.SequenceEqual(other.Columns);

        public override bool Equals(object? obj) => Equals(obj as PhysicalIndexSignature);

        public override int GetHashCode()
        {
            var hash = HashCode.Combine(IsUnique, MissingValues);
            foreach (var column in Columns)
                hash = HashCode.Combine(hash, column?.Column, column?.Direction);
            return hash;
        }
    }

    private static void ValidateUniqueNullability(
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyDictionary<string, ColumnDefinition> byName,
        ICollection<PortabilityRefusal> diagnostics)
    {
        foreach (var index in indexes.Where(index => index is not null))
        {
            var nullableColumns = NullableIndexedColumns(index, byName);
            if (!index.IsUnique || index.MissingValues != MissingValueBehavior.Included ||
                nullableColumns.Length == 0 || HasImpliedUniqueness(index, indexes, byName))
            {
                continue;
            }

            diagnostics.Add(new(
                "GW-PORT-001",
                $"Unique index '{index.Name}' includes nullable column(s) '{string.Join("', '", nullableColumns)}' " +
                "with MissingValues.Included; " +
                "cross-provider uniqueness is ambiguous.",
                $"indexes.{index.Name}"));
        }
    }

    private static bool HasImpliedUniqueness(
        IndexDefinition index,
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyDictionary<string, ColumnDefinition> byName)
    {
        var indexedColumns = new HashSet<string>(
            (index.Columns ?? [])
                .Where(column => column is not null && column.Column is not null)
                .Select(column => column.Column),
            StringComparer.Ordinal);

        return indexes.Any(other =>
            other is not null &&
            !string.Equals(other.Name, index.Name, StringComparison.Ordinal) &&
            other.IsUnique &&
            (other.Columns?.Count ?? 0) < (index.Columns?.Count ?? 0) &&
            (other.Columns ?? []).All(column =>
                column is not null && column.Column is not null && indexedColumns.Contains(column.Column)) &&
            (other.MissingValues != MissingValueBehavior.Excluded || !HasNullableIndexedColumn(other, byName)));
    }

    private static bool HasNullableIndexedColumn(
        IndexDefinition index,
        IReadOnlyDictionary<string, ColumnDefinition> byName) =>
        NullableIndexedColumns(index, byName).Length != 0;

    private static string[] NullableIndexedColumns(
        IndexDefinition index,
        IReadOnlyDictionary<string, ColumnDefinition> byName) =>
        (index.Columns ?? [])
            .Where(column =>
                column is not null &&
                column.Column is not null &&
                byName.TryGetValue(column.Column, out var definition) &&
                definition.IsNullable)
            .Select(column => column.Column)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static void ValidateDecimalShape(
        IReadOnlyList<ColumnDefinition> columns,
        ICollection<PortabilityRefusal> diagnostics)
    {
        foreach (var column in columns.Where(column => column is not null && column.Type == PortableType.Decimal))
        {
            if (column.Precision is not null && column.Scale is not null)
                continue;

            diagnostics.Add(new(
                "GW-PORT-002",
                $"Decimal column '{column.Name}' must declare both Precision and Scale.",
                $"columns.{column.Name}"));
        }
    }

    private static void ValidateBoundedIndexKeys(
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyDictionary<string, ColumnDefinition> byName,
        ICollection<PortabilityRefusal> diagnostics)
    {
        foreach (var index in indexes.Where(index => index is not null))
        foreach (var indexColumn in (index.Columns ?? []).Where(column => column is not null))
        {
            if (indexColumn.Column is null || !byName.TryGetValue(indexColumn.Column, out var column) ||
                column.Type is not (PortableType.String or PortableType.Binary) ||
                column.MaxLength is > 0)
            {
                continue;
            }

            diagnostics.Add(new(
                "GW-PORT-003",
                $"Index '{index.Name}' key column '{column.Name}' requires a positive MaxLength for portable sizing.",
                $"indexes.{index.Name}.columns.{column.Name}"));
        }
    }

    private static void ValidateIndexBudget(
        IReadOnlyList<IndexDefinition> indexes,
        IReadOnlyDictionary<string, ColumnDefinition> byName,
        ICollection<PortabilityRefusal> diagnostics)
    {
        foreach (var index in indexes.Where(index => index is not null))
        {
            var terms = new List<string>();
            long bytes = 0;
            var canCalculate = true;
            foreach (var indexColumn in (index.Columns ?? []).Where(column => column is not null))
            {
                if (indexColumn.Column is null || !byName.TryGetValue(indexColumn.Column, out var column))
                {
                    canCalculate = false;
                    continue;
                }

                if (!TryGetKeyBytes(column, out var width, out var formula))
                {
                    if (column.Type == PortableType.Decimal &&
                        column.Precision is int precision &&
                        (precision < MinimumPortableDecimalPrecision || precision > MaximumPortableDecimalPrecision))
                    {
                        diagnostics.Add(new(
                            "GW-PORT-004",
                            $"Index '{index.Name}' cannot calculate key width for decimal column '{column.Name}': " +
                            $"precision {precision} is outside supported range {MinimumPortableDecimalPrecision}-{MaximumPortableDecimalPrecision}; " +
                            "SQL Server decimal storage tiers are 5/9/13/17 bytes and the strict portable budget is 1700 bytes.",
                            $"indexes.{index.Name}"));
                    }

                    canCalculate = false;
                    continue;
                }

                bytes += width;
                terms.Add(formula);
            }

            if (canCalculate && bytes > StrictIndexKeyByteBudget)
            {
                diagnostics.Add(new(
                    "GW-PORT-004",
                    $"Index '{index.Name}' key width is {bytes} bytes ({string.Join(" + ", terms)}); " +
                    $"the strict portable budget is {StrictIndexKeyByteBudget} bytes.",
                    $"indexes.{index.Name}"));
            }
        }
    }

    private static bool TryGetKeyBytes(
        ColumnDefinition column,
        out long bytes,
        out string formula)
    {
        switch (column.Type)
        {
            case PortableType.String when column.MaxLength is > 0:
                bytes = (long)column.MaxLength.Value * 2;
                formula = $"{column.Name}={column.MaxLength.Value}*2";
                return true;
            case PortableType.Binary when column.MaxLength is > 0:
                bytes = column.MaxLength.Value;
                formula = $"{column.Name}={column.MaxLength.Value}";
                return true;
            case PortableType.Int32:
                bytes = 4;
                formula = $"{column.Name}=4";
                return true;
            case PortableType.Int64:
                bytes = 8;
                formula = $"{column.Name}=8";
                return true;
            case PortableType.Decimal when column.Precision is >= 1 and <= 9:
                bytes = 5;
                formula = $"{column.Name}=5(decimal precision {column.Precision})";
                return true;
            case PortableType.Decimal when column.Precision is >= 10 and <= 19:
                bytes = 9;
                formula = $"{column.Name}=9(decimal precision {column.Precision})";
                return true;
            case PortableType.Decimal when column.Precision is >= 20 and <= 28:
                bytes = 13;
                formula = $"{column.Name}=13(decimal precision {column.Precision})";
                return true;
            case PortableType.Decimal when column.Precision is >= 29 and <= 38:
                bytes = 17;
                formula = $"{column.Name}=17(decimal precision {column.Precision})";
                return true;
            case PortableType.Boolean:
                bytes = 1;
                formula = $"{column.Name}=1";
                return true;
            case PortableType.DateTimeOffset:
                bytes = 10;
                formula = $"{column.Name}=10";
                return true;
            case PortableType.Guid:
                bytes = 16;
                formula = $"{column.Name}=16";
                return true;
            default:
                bytes = 0;
                formula = string.Empty;
                return false;
        }
    }

    private static void ValidateGeneration(
        StorageUnit unit,
        IReadOnlyList<ColumnDefinition> columns,
        ICollection<PortabilityRefusal> diagnostics)
    {
        var generated = columns
            .Where(column => column is not null && column.Generation == ColumnGeneration.ProviderSequence)
            .ToArray();
        foreach (var column in generated)
        {
            if (column.Type == PortableType.Int64 && !column.IsNullable &&
                generated.Length == 1 && unit.Key.Columns.Count == 1 && unit.Key.Columns[0] == column.Name)
                continue;

            diagnostics.Add(new(
                "GW-PORT-005",
                $"Column '{column.Name}' uses ProviderSequence but must be the sole non-nullable Int64 primary-key column of its storage unit.",
                $"columns.{column.Name}"));
        }
    }

    private static void ValidateCollation(
        IReadOnlyList<ColumnDefinition> columns,
        ICollection<PortabilityRefusal> diagnostics)
    {
        foreach (var column in columns.Where(column => column is not null && column.Collation is not null))
        {
            if (Enum.IsDefined(typeof(PortableCollation), column.Collation!.Value))
                continue;

            diagnostics.Add(new(
                "GW-PORT-006",
                $"Column '{column.Name}' declares collation '{column.Collation}', outside the portable collation set.",
                $"columns.{column.Name}"));
        }
    }

    private static void ValidateRetention(
        RetentionDeclaration? retention,
        IReadOnlyDictionary<string, ColumnDefinition> byName,
        ICollection<PortabilityRefusal> diagnostics)
    {
        if (retention is null)
            return;

        var name = retention.OrderColumn ?? string.Empty;
        var invalidKeepNewest = retention.KeepNewest < 0;
        var invalidTrigger = !Enum.IsDefined(retention.Trigger);
        var invalidPartition = (retention.PartitionColumns ?? []).Any(partition =>
            string.IsNullOrWhiteSpace(partition) || !byName.ContainsKey(partition));
        if (invalidKeepNewest || invalidTrigger || invalidPartition ||
            !byName.TryGetValue(name, out var column) || column.IsNullable || !IsRetentionOrderable(column.Type))
        {
            diagnostics.Add(new(
                "GW-PORT-007",
                $"Retention requires a non-negative KeepNewest value, a declared non-nullable orderable order column '{name}', and declared partition columns.",
                $"retention.{name}"));
        }
    }

    // Keep retention orderability aligned with #230's portable encoded order keys. Native
    // Boolean, Guid, Binary, and Json ordering waits for an explicit canonical derived projection.
    private static bool IsRetentionOrderable(PortableType type) =>
        type is PortableType.String or
            PortableType.Int32 or
            PortableType.Int64 or
            PortableType.Decimal or
            PortableType.DateTimeOffset;

    private static void ValidateMongoKeyOrder(
        StorageUnit unit,
        PortabilityValidationContext context,
        ICollection<PortabilityRefusal> diagnostics)
    {
        if (!context.TargetIdentities.Any(IsMongoTarget) || context.PriorAppliedMongoCompositeKeyOrder.Count == 0)
            return;

        var current = unit.Key?.Columns ?? [];
        if (current.Count < 2 || current.SequenceEqual(context.PriorAppliedMongoCompositeKeyOrder, StringComparer.Ordinal))
            return;

        diagnostics.Add(new(
            "GW-PORT-008",
            $"Mongo composite key column order changed from [{string.Join(", ", context.PriorAppliedMongoCompositeKeyOrder)}] " +
            $"to [{string.Join(", ", current)}].",
            "key.columns"));
    }

    private static bool IsMongoTarget(string identity) =>
        identity?.IndexOf("mongo", StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>Builder validation seam used by the fluent and typed declaration front-ends.</summary>
public static class BuilderPortabilityValidation
{
    public static PortabilityValidationResult Validate(StorageUnit? unit, PortabilityValidationContext? context = null) =>
        PortabilityValidator.Validate(unit, context);
}

/// <summary>Manifest validation seam reserved for the later manifest slice.</summary>
public static class ManifestPortabilityValidation
{
    public static PortabilityValidationResult Validate(StorageUnit? unit, PortabilityValidationContext? context = null) =>
        PortabilityValidator.Validate(unit, context);
}

/// <summary>Schema-target validation seam reserved for the later schema slice.</summary>
public static class SchemaTargetPortabilityValidation
{
    public static PortabilityValidationResult Validate(StorageUnit? unit, PortabilityValidationContext? context = null) =>
        PortabilityValidator.Validate(unit, context);
}
