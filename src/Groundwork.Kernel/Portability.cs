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

/// <summary>The optional retention shape needed by the K2 validation seam.</summary>
public sealed record RetentionDeclaration(string OrderColumn);

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

        ValidateUniqueNullability(indexes, byName, diagnostics);
        ValidateDecimalShape(columns, diagnostics);
        ValidateBoundedIndexKeys(indexes, byName, diagnostics);
        ValidateIndexBudget(indexes, byName, diagnostics);
        ValidateGeneration(columns, diagnostics);
        ValidateCollation(columns, diagnostics);
        ValidateRetention(context.Retention, byName, diagnostics);
        ValidateMongoKeyOrder(unit, context, diagnostics);

        return new(diagnostics);
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
        IReadOnlyList<ColumnDefinition> columns,
        ICollection<PortabilityRefusal> diagnostics)
    {
        foreach (var column in columns.Where(column => column is not null && column.Generation == ColumnGeneration.ProviderSequence))
        {
            if (column.Type == PortableType.Int64 && !column.IsNullable)
                continue;

            diagnostics.Add(new(
                "GW-PORT-005",
                $"Column '{column.Name}' uses ProviderSequence but must be a non-nullable Int64.",
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
        if (!byName.TryGetValue(name, out var column) || column.IsNullable || !IsRetentionOrderable(column.Type))
        {
            diagnostics.Add(new(
                "GW-PORT-007",
                $"Retention order column '{name}' must be declared, non-nullable, and orderable.",
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
