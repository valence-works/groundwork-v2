using Groundwork.Kernel;

namespace Groundwork.Testing;

/// <summary>
/// Declares the storage-family-specific shape used by <see cref="ConformanceSuite"/>.
/// Providers remain responsible for execution; a family supplies only its declaration and
/// the small value/key mapping needed to exercise it without a hard-coded probe schema.
/// </summary>
public sealed class ConformanceScenario
{
    public ConformanceScenario(
        StorageUnit global,
        StorageUnit scoped,
        Func<string, string, string?, StorageValues> values,
        Func<string, WriteOutcome, StorageKey> key,
        Func<StorageValues, StorageKey, StorageValues> attachKey,
        Func<string, StorageKey> missingKey,
        string valueColumn,
        Func<WriteOutcomeStatus, bool>? acceptsUpsertStatus = null)
    {
        Global = global ?? throw new ArgumentNullException(nameof(global));
        Scoped = scoped ?? throw new ArgumentNullException(nameof(scoped));
        Values = values ?? throw new ArgumentNullException(nameof(values));
        Key = key ?? throw new ArgumentNullException(nameof(key));
        AttachKey = attachKey ?? throw new ArgumentNullException(nameof(attachKey));
        MissingKey = missingKey ?? throw new ArgumentNullException(nameof(missingKey));
        AcceptsUpsertStatus = acceptsUpsertStatus ?? (status => status == WriteOutcomeStatus.Upserted);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueColumn);
        if (!global.Columns.Any(column => string.Equals(column.Name, valueColumn, StringComparison.Ordinal)) ||
            !scoped.Columns.Any(column => string.Equals(column.Name, valueColumn, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"The conformance value column '{valueColumn}' must be declared on both scenario units.",
                nameof(valueColumn));
        }

        ValueColumn = valueColumn;
    }

    public StorageUnit Global { get; }

    public StorageUnit Scoped { get; }

    public string ValueColumn { get; }

    public Func<string, string, string?, StorageValues> Values { get; }

    public Func<string, WriteOutcome, StorageKey> Key { get; }

    public Func<StorageValues, StorageKey, StorageValues> AttachKey { get; }

    public Func<string, StorageKey> MissingKey { get; }

    public Func<WriteOutcomeStatus, bool> AcceptsUpsertStatus { get; }

    /// <summary>The original shipped #237 probe, retained as the default scenario.</summary>
    public static ConformanceScenario Default { get; } = CreateDefault();

    private static ConformanceScenario CreateDefault()
    {
        var global = CreateUnit("conformance-global", ScopePolicy.Global, ConcurrencyDeclaration.None);
        var scoped = CreateUnit("conformance-scoped", ScopePolicy.Scoped, ConcurrencyDeclaration.Optimistic());
        return new ConformanceScenario(
            global,
            scoped,
            static (id, value, unique) => new StorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["value"] = value,
                ["uniqueValue"] = unique ?? id
            }),
            static (id, _) => new StorageKey(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id }),
            static (values, _) => values,
            static id => new StorageKey(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id }),
            "value");
    }

    private static StorageUnit CreateUnit(
        string id,
        ScopePolicy scope,
        ConcurrencyDeclaration concurrency) => new()
    {
        Id = new StorageUnitId(id),
        Name = id,
        Columns =
        [
            // Keep the provider-neutral probe's variable-length primary key bounded so
            // providers can validate native key widths from the declaration alone.
            new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
            new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 256 },
            new ColumnDefinition { Name = "uniqueValue", Type = PortableType.String, MaxLength = 256 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Scope = scope,
        Concurrency = concurrency,
        Indexes =
        [
            new IndexDefinition { Name = "by-value", Columns = [new IndexColumn("value")] },
            new IndexDefinition
            {
                Name = "unique-value",
                Columns = [new IndexColumn("uniqueValue")],
                IsUnique = true,
                MissingValues = MissingValueBehavior.Excluded
            }
        ]
    };
}
