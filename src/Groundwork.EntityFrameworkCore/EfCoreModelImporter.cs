using System.Collections.ObjectModel;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Groundwork.EntityFrameworkCore;

/// <summary>Severity of one EF model import finding.</summary>
public enum EfCoreImportSeverity
{
    Information,
    Warning,
    Error
}

/// <summary>An explicit mapping for an EF culture collation that Groundwork must persist as an ICU sort key.</summary>
public sealed record EfCoreLocaleOrdering(string CultureName, int MaximumExpansionFactor);

/// <summary>Choices that cannot be inferred safely from an EF model.</summary>
public sealed record EfCoreImportOptions
{
    public IReadOnlyDictionary<string, EfCoreLocaleOrdering> LocaleOrderings { get; init; } =
        new ReadOnlyDictionary<string, EfCoreLocaleOrdering>(new Dictionary<string, EfCoreLocaleOrdering>());

    public IReadOnlyDictionary<string, ScopePolicy> ScopePolicies { get; init; } =
        new ReadOnlyDictionary<string, ScopePolicy>(new Dictionary<string, ScopePolicy>());
}

/// <summary>One named mismatch between EF semantics and the portable Groundwork contract.</summary>
public sealed record EfCoreImportFinding(
    string Code,
    EfCoreImportSeverity Severity,
    string Target,
    string Message,
    string Alternative);

/// <summary>The declarations that were safely inferred and every decision that still needs attention.</summary>
public sealed record EfCoreImportResult(
    IReadOnlyList<StorageUnit> Declarations,
    IReadOnlyList<EfCoreImportFinding> Findings)
{
    public bool IsComplete => Findings.All(finding => finding.Severity != EfCoreImportSeverity.Error);
}

/// <summary>
/// Imports already-created EF metadata. The caller owns design-time context/model creation; this
/// adapter never loads an application assembly or starts its host.
/// </summary>
public static class EfCoreModelImporter
{
    public static EfCoreImportResult Import(DbContext context, EfCoreImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Import(context.Model, options);
    }

    public static EfCoreImportResult Import(IModel model, EfCoreImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new EfCoreImportOptions();
        var findings = new List<EfCoreImportFinding>();
        var allEntities = model.GetEntityTypes()
            .OrderBy(entity => entity.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (var entity in allEntities.Where(entity => entity.GetTableName() is null))
        {
            findings.Add(Error(
                "GW-EF-001",
                entity.Name,
                $"Entity '{entity.Name}' is not mapped to a relational table" +
                (entity.GetViewName() is { } view ? $"; it maps to view '{view}'" : string.Empty) + ".",
                "Keep read-only views outside Groundwork, or map the entity to a keyed table-backed storage declaration."));
        }
        var entities = allEntities
            .Where(entity => entity.GetTableName() is not null)
            .ToArray();
        var modelCollation = model.FindAnnotation(RelationalAnnotationNames.Collation)?.Value as string;
        var inheritedEntities = entities
            .Where(entity => entity.BaseType is not null || entity.GetDirectlyDerivedTypes().Any())
            .ToHashSet();
        foreach (var entity in inheritedEntities.OrderBy(entity => entity.Name, StringComparer.Ordinal))
        {
            findings.Add(Error(
                "GW-EF-001",
                entity.Name,
                $"Entity '{entity.Name}' participates in an EF inheritance hierarchy, whose table and discriminator semantics cannot become an independent Groundwork storage declaration.",
                "Flatten the persisted hierarchy into explicit storage declarations and keep polymorphism in the application layer."));
        }

        var duplicateTables = entities
            .Where(entity => !inheritedEntities.Contains(entity))
            .GroupBy(entity => (Schema: entity.GetSchema(), Table: entity.GetTableName()!), TableIdentityComparer.Instance)
            .Where(group => group.Count() != 1)
            .SelectMany(group => group)
            .ToHashSet();
        foreach (var entity in duplicateTables.OrderBy(entity => entity.Name, StringComparer.Ordinal))
        {
            findings.Add(Error(
                "GW-EF-001",
                entity.Name,
                $"Entity '{entity.Name}' shares table '{entity.GetTableName()}' with another EF entity; table splitting and inheritance cannot be imported without merging column ownership.",
                "Flatten the physical table into one Groundwork storage declaration and keep inheritance in the application layer."));
        }

        var declarations = new List<StorageUnit>();
        foreach (var entity in entities.Where(entity =>
                     !inheritedEntities.Contains(entity) && !duplicateTables.Contains(entity)))
        {
            var declaration = ImportEntity(entity, modelCollation, options, findings);
            if (declaration is not null)
                declarations.Add(declaration);
        }

        if (!findings.Any(finding => finding.Severity == EfCoreImportSeverity.Error))
        {
            try
            {
                SchemaSubject.ValidateManifest(declarations);
            }
            catch (ArgumentException exception)
            {
                findings.Add(Error(
                    "GW-EF-001",
                    "model",
                    $"The inferred declarations do not form a portable Groundwork manifest: {exception.Message}",
                    "Resolve the named declaration mismatch before adopting the scaffold."));
            }
        }

        return new EfCoreImportResult(
            new ReadOnlyCollection<StorageUnit>(declarations),
            new ReadOnlyCollection<EfCoreImportFinding>(findings
                .OrderBy(finding => finding.Target, StringComparer.Ordinal)
                .ThenBy(finding => finding.Code, StringComparer.Ordinal)
                .ToArray()));
    }

    private static StorageUnit? ImportEntity(
        IReadOnlyEntityType entity,
        string? modelCollation,
        EfCoreImportOptions options,
        ICollection<EfCoreImportFinding> findings)
    {
        var tableName = entity.GetTableName()!;
        if (entity.GetMappingFragments(StoreObjectType.Table).Any())
        {
            findings.Add(Error(
                "GW-EF-001",
                entity.Name,
                $"Entity '{entity.Name}' is split across multiple tables, so one portable storage declaration cannot preserve its column ownership.",
                "Flatten the persisted shape into one table per Groundwork storage declaration."));
            return null;
        }
        if (entity.GetComplexProperties().Any())
        {
            findings.Add(Error(
                "GW-EF-001",
                entity.Name,
                $"Entity '{entity.Name}' contains EF complex properties whose nested columns are not independent entity properties.",
                "Flatten the complex value into explicitly named scalar columns before importing."));
            return null;
        }
        if (!string.IsNullOrWhiteSpace(entity.GetSchema()))
        {
            findings.Add(Error(
                "GW-EF-001",
                entity.Name,
                $"Entity '{entity.Name}' maps to schema-qualified table '{entity.GetSchema()}.{tableName}', but Groundwork storage identifiers have no schema component.",
                "Choose a schema-independent portable storage name and manage provider schema placement outside the declaration."));
            return null;
        }
        var store = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
        var unmappedProperties = entity.GetProperties()
            .Where(property => property.GetColumnName(store) is null)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        if (unmappedProperties.Length > 0)
        {
            foreach (var property in unmappedProperties)
            {
                findings.Add(Error(
                    "GW-EF-001",
                    $"{entity.Name}.{property.Name}",
                    $"Property '{property.Name}' has no column in primary table '{tableName}'.",
                    "Map every imported property to the primary table, or flatten the split mapping manually."));
            }
            return null;
        }
        var key = entity.FindPrimaryKey();
        if (key is null)
        {
            findings.Add(Error(
                "GW-EF-001",
                entity.Name,
                $"Keyless EF entity '{entity.Name}' cannot become mutable Groundwork storage.",
                "Keep it as a read model outside Groundwork or declare a stable portable key."));
            return null;
        }

        var hasExplicitScope = options.ScopePolicies.TryGetValue(entity.Name, out var scope);
        if (HasQueryFilter(entity) && !hasExplicitScope)
        {
            findings.Add(Error(
                "GW-EF-006",
                entity.Name,
                $"Entity '{entity.Name}' has a global query filter; the importer cannot prove that it is a tenant boundary.",
                $"Set ScopePolicies[\"{entity.Name}\"] to Scoped only after confirming the filter is the storage scope."));
        }
        scope = hasExplicitScope ? scope : ScopePolicy.Global;

        var columns = new List<ColumnDefinition>();
        foreach (var property in entity.GetProperties().OrderBy(PropertyName, StringComparer.Ordinal))
        {
            var column = ImportProperty(entity, property, store, modelCollation, options, findings);
            if (column is not null)
                columns.Add(column);
        }

        var mappedNames = columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var keyNames = key.Properties.Select(property => PropertyName(property, store)).ToArray();
        if (keyNames.Any(name => !mappedNames.Contains(name)))
            return null;

        var indexes = entity.GetIndexes()
            .OrderBy(index => index.Name, StringComparer.Ordinal)
            .Select((index, ordinal) => ImportIndex(index, store, tableName, ordinal, mappedNames, findings))
            .Where(index => index is not null)
            .Cast<IndexDefinition>()
            .ToList();
        var references = new List<ReferenceDefinition>();
        foreach (var foreignKey in entity.GetForeignKeys().OrderBy(ReferenceName, StringComparer.Ordinal))
        {
            var target = foreignKey.PrincipalEntityType;
            if (!ReferenceEquals(foreignKey.PrincipalKey, target.FindPrimaryKey()))
            {
                findings.Add(Error(
                    "GW-EF-001",
                    $"{entity.Name}.{ReferenceName(foreignKey)}",
                    $"Foreign key '{ReferenceName(foreignKey)}' targets an EF alternate key, while a Groundwork reference targets the complete storage key.",
                    "Make the referenced columns the target storage key, or model the lookup without a Groundwork reference."));
                continue;
            }
            var referenceColumns = foreignKey.Properties.Select(property => PropertyName(property, store)).ToArray();
            if (referenceColumns.Any(name => !mappedNames.Contains(name)))
                continue;
            if (!HasPrefix(keyNames, referenceColumns) && !indexes.Any(index => HasPrefix(
                    index.Columns.Select(column => column.Column), referenceColumns)))
            {
                var generatedName = UniqueIndexName(indexes, $"gw_ref_{ReferenceName(foreignKey)}");
                indexes.Add(new IndexDefinition
                {
                    Name = generatedName,
                    Columns = referenceColumns.Select(name => new IndexColumn(name)).ToArray()
                });
                findings.Add(new EfCoreImportFinding(
                    "GW-EF-004",
                    EfCoreImportSeverity.Information,
                    $"{entity.Name}.{ReferenceName(foreignKey)}",
                    $"Groundwork added covering index '{generatedName}' for the declared reference.",
                    "Keep the generated index, or replace it with another index whose leading columns are the reference columns."));
            }

            references.Add(new ReferenceDefinition
            {
                Name = ReferenceName(foreignKey),
                Columns = referenceColumns,
                TargetUnitId = new StorageUnitId(target.GetTableName() ?? target.ShortName()),
                TargetScope = options.ScopePolicies.TryGetValue(target.Name, out var targetScope)
                    ? targetScope
                    : ScopePolicy.Global,
                Enforcement = ReferenceEnforcement.LogicalOnly
            });
        }

        var declaration = new StorageUnit
        {
            Id = new StorageUnitId(tableName),
            Name = tableName,
            Columns = columns,
            Key = new KeyDefinition { Columns = keyNames },
            Indexes = indexes,
            References = references,
            Scope = scope
        };
        foreach (var refusal in PortabilityValidator.Validate(declaration).Refusals)
        {
            findings.Add(Error(
                "GW-EF-001",
                $"{entity.Name}.{refusal.Path}",
                $"The inferred declaration is not portable ({refusal.Code}): {refusal.Message}",
                PortabilityAlternative(refusal.Code)));
        }
        return declaration;
    }

    private static ColumnDefinition? ImportProperty(
        IReadOnlyEntityType entity,
        IReadOnlyProperty property,
        StoreObjectIdentifier store,
        string? modelCollation,
        EfCoreImportOptions options,
        ICollection<EfCoreImportFinding> findings)
    {
        var name = PropertyName(property, store);
        var target = $"{entity.Name}.{property.Name}";
        if (property.GetValueConverter() is not null || property.GetProviderClrType() is not null)
        {
            findings.Add(Error(
                "GW-EF-002",
                target,
                $"Property '{property.Name}' uses an EF value converter whose persisted representation is application-specific.",
                "Declare the converted portable storage value explicitly and keep the domain conversion in the application layer."));
            return null;
        }
        var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        var portable = clrType == typeof(string) ? PortableType.String
            : clrType == typeof(int) ? PortableType.Int32
            : clrType == typeof(long) ? PortableType.Int64
            : clrType == typeof(decimal) ? PortableType.Decimal
            : clrType == typeof(bool) ? PortableType.Boolean
            : clrType == typeof(DateTimeOffset) ? PortableType.DateTimeOffset
            : clrType == typeof(Guid) ? PortableType.Guid
            : clrType == typeof(byte[]) ? PortableType.Binary
            : clrType == typeof(double) || clrType == typeof(float) ? PortableType.Double
            : (PortableType?)null;
        if (portable is null)
        {
            findings.Add(Error(
                "GW-EF-002",
                target,
                $"CLR type '{clrType.FullName}' has no lossless portable Groundwork column mapping.",
                clrType == typeof(DateTime)
                    ? "Migrate the property to DateTimeOffset with an explicit offset."
                    : "Convert the value explicitly to String, Int32, Int64, Decimal, Boolean, DateTimeOffset, Guid, Binary, Json, or storage-only Double."));
            return null;
        }

        if (portable == PortableType.Double)
        {
            findings.Add(new EfCoreImportFinding(
                "GW-EF-003",
                EfCoreImportSeverity.Warning,
                target,
                $"Floating-point property '{property.Name}' is imported as storage-only Double; predicates, keys, indexes, ordering, and grouping are refused.",
                "Use Decimal or a scaled Int64 if the application queries this value."));
        }

        LocaleSortKeyDefinition? locale = null;
        var collation = portable == PortableType.String
            ? property.FindAnnotation(RelationalAnnotationNames.Collation)?.Value as string ?? modelCollation
            : null;
        if (collation is not null)
        {
            if (options.LocaleOrderings.TryGetValue(collation, out var ordering))
            {
                locale = new LocaleSortKeyDefinition
                {
                    CultureName = ordering.CultureName,
                    MaximumExpansionFactor = ordering.MaximumExpansionFactor
                };
            }
            else
            {
                findings.Add(Error(
                    "GW-EF-005",
                    target,
                    $"EF collation '{collation}' is provider-specific and cannot be translated to a portable culture ordering automatically.",
                    $"Set LocaleOrderings[\"{collation}\"] to an explicit ICU culture name and maximum expansion factor."));
            }
        }

        var computedSql = property.FindAnnotation(RelationalAnnotationNames.ComputedColumnSql)?.Value as string;
        var defaultSql = property.FindAnnotation(RelationalAnnotationNames.DefaultValueSql)?.Value as string;
        if (computedSql is not null || defaultSql is not null)
        {
            findings.Add(Error(
                "GW-EF-002",
                target,
                $"Provider SQL generation for '{property.Name}' is not a portable column declaration.",
                "Move the computation into application writes or a provider-neutral Groundwork declaration."));
        }
        var defaultAnnotation = property.FindAnnotation(RelationalAnnotationNames.DefaultValue);
        var hasDefault = defaultAnnotation is not null;
        var defaultValue = defaultAnnotation?.Value;
        var isSolePrimaryKey = entity.FindPrimaryKey() is { Properties.Count: 1 } primaryKey &&
                               ReferenceEquals(primaryKey.Properties[0], property);
        var isProviderSequence = property.ValueGenerated == ValueGenerated.OnAdd &&
                                 portable == PortableType.Int64 && isSolePrimaryKey &&
                                 !hasDefault && defaultSql is null && computedSql is null;
        if (property.IsConcurrencyToken)
        {
            findings.Add(Error(
                "GW-EF-002",
                target,
                $"EF concurrency token '{property.Name}' cannot be preserved as an ordinary Groundwork column.",
                "Declare Groundwork optimistic concurrency explicitly and let its provider-owned revision token replace the EF token."));
        }
        else if (property.ValueGenerated is ValueGenerated.OnUpdate or ValueGenerated.OnAddOrUpdate)
        {
            findings.Add(Error(
                "GW-EF-002",
                target,
                $"EF value generation '{property.ValueGenerated}' for '{property.Name}' has no portable Groundwork column equivalent.",
                "Move generation into application writes, or declare an explicit portable default or sole Int64 ProviderSequence key."));
        }
        else if (property.ValueGenerated == ValueGenerated.OnAdd && !isProviderSequence && !hasDefault &&
                 defaultSql is null && computedSql is null && portable != PortableType.Guid)
        {
            findings.Add(Error(
                "GW-EF-002",
                target,
                $"EF OnAdd generation for '{property.Name}' cannot be inferred as a portable Groundwork sequence.",
                "Use a sole non-nullable Int64 ProviderSequence key, or make the application supply the value explicitly."));
        }

        return new ColumnDefinition
        {
            Name = name,
            Type = portable.Value,
            IsNullable = property.IsNullable,
            MaxLength = portable is PortableType.String or PortableType.Binary ? property.GetMaxLength() : null,
            Precision = portable == PortableType.Decimal ? property.GetPrecision() : null,
            Scale = portable == PortableType.Decimal ? property.GetScale() : null,
            LocaleSortKey = locale,
            Default = hasDefault
                ? new PortableDefault(defaultValue is byte[] bytes ? bytes.ToArray() : defaultValue)
                : null,
            Generation = isProviderSequence
                ? ColumnGeneration.ProviderSequence
                : ColumnGeneration.Supplied
        };
    }

    private static IndexDefinition? ImportIndex(
        IReadOnlyIndex index,
        StoreObjectIdentifier store,
        string table,
        int ordinal,
        IReadOnlySet<string> mappedNames,
        ICollection<EfCoreImportFinding> findings)
    {
        var names = index.Properties.Select(property => PropertyName(property, store)).ToArray();
        if (names.Any(name => !mappedNames.Contains(name)))
            return null;
        if (index.GetFilter() is { } filter)
        {
            findings.Add(Error(
                "GW-EF-001",
                $"{table}.{index.Name}",
                $"Filtered EF index '{index.Name}' uses provider SQL '{filter}', which cannot be inferred as Groundwork missing-value semantics.",
                "Declare an unfiltered index, or explicitly choose Included/Excluded missing-value behavior in Groundwork."));
            return null;
        }
        var descending = index.IsDescending;
        return new IndexDefinition
        {
            Name = index.GetDatabaseName() ?? index.Name ?? $"ix_{table}_{ordinal}",
            Columns = names.Select((name, position) => new IndexColumn(
                name,
                descending is not null && position < descending.Count && descending[position]
                    ? SortDirection.Descending
                    : SortDirection.Ascending)).ToArray(),
            IsUnique = index.IsUnique,
            MissingValues = MissingValueBehavior.Included
        };
    }

    private static string PropertyName(IReadOnlyProperty property) => property.Name;

    private static string PropertyName(IReadOnlyProperty property, StoreObjectIdentifier store) =>
        property.GetColumnName(store) ?? property.Name;

    private static string ReferenceName(IReadOnlyForeignKey foreignKey) =>
        foreignKey.DependentToPrincipal?.Name ??
        "ref_" + string.Join("_", foreignKey.Properties.Select(property => property.Name));

    private static bool HasQueryFilter(IReadOnlyEntityType entity)
    {
#if NET10_0_OR_GREATER
        return entity.GetDeclaredQueryFilters().Any();
#else
        return entity.GetQueryFilter() is not null;
#endif
    }

    private static bool HasPrefix(IEnumerable<string> candidate, IReadOnlyList<string> prefix) =>
        candidate.Take(prefix.Count).SequenceEqual(prefix, StringComparer.Ordinal);

    private static string UniqueIndexName(IEnumerable<IndexDefinition> indexes, string candidate)
    {
        var names = indexes.Select(index => index.Name).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(candidate))
            return candidate;
        for (var suffix = 2; ; suffix++)
        {
            var value = candidate + "_" + suffix;
            if (!names.Contains(value))
                return value;
        }
    }

    private static EfCoreImportFinding Error(string code, string target, string message, string alternative) =>
        new(code, EfCoreImportSeverity.Error, target, message, alternative);

    private static string PortabilityAlternative(string code) => code switch
    {
        "GW-PORT-001" => "Make the unique index columns required, or choose Excluded missing-value behavior manually.",
        "GW-PORT-002" => "Configure explicit portable decimal precision and scale in EF before importing.",
        "GW-PORT-003" => "Configure a positive maximum length for every string or binary index column.",
        "GW-PORT-012" => "Keep Double storage-only; use Decimal or a scaled Int64 in keys and indexes.",
        "GW-PORT-013" => "Supply a default whose CLR value is valid for the inferred PortableType.",
        "GW-PORT-014" => "Supply a valid ICU culture, positive expansion factor, and bounded String source.",
        _ => "Correct the named EF facet, or declare this storage shape manually in Groundwork."
    };

    private sealed class TableIdentityComparer : IEqualityComparer<(string? Schema, string Table)>
    {
        internal static readonly TableIdentityComparer Instance = new();

        public bool Equals((string? Schema, string Table) left, (string? Schema, string Table) right) =>
            string.Equals(left.Schema, right.Schema, StringComparison.Ordinal) &&
            string.Equals(left.Table, right.Table, StringComparison.Ordinal);

        public int GetHashCode((string? Schema, string Table) value) =>
            HashCode.Combine(value.Schema, value.Table);
    }
}
