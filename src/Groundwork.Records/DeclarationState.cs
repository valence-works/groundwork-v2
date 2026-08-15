using Groundwork.Kernel;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Records;

internal sealed class DeclarationState
{
    private readonly List<ColumnDefinition> columns = [];
    private readonly List<IndexDefinition> indexes = [];
    private readonly string id;
    private readonly string name;
    private KeyDefinition? key;

    public DeclarationState(string id, string name)
    {
        this.id = RequireText(id, nameof(id));
        this.name = RequireText(name, nameof(name));
    }

    public IReadOnlyList<ColumnDefinition> Columns => columns;

    public void AddColumn(ColumnDefinition definition)
    {
        if (definition is null)
            throw new ArgumentNullException(nameof(definition));
        if (columns.Any(column => string.Equals(column.Name, definition.Name, StringComparison.Ordinal)))
            throw new ArgumentException($"Column '{definition.Name}' is already declared.", nameof(definition));

        columns.Add(definition);
    }

    public void ReplaceColumn(ColumnDefinition definition)
    {
        if (definition is null)
            throw new ArgumentNullException(nameof(definition));
        var index = columns.FindIndex(column => string.Equals(column.Name, definition.Name, StringComparison.Ordinal));
        if (index < 0)
            throw new ArgumentException($"Column '{definition.Name}' is not declared.", nameof(definition));

        columns[index] = definition;
    }

    public void SetKey(IEnumerable<string> columnNames)
    {
        var names = SnapshotNames(columnNames, "key");
        key = new KeyDefinition { Columns = names };
    }

    public void AddIndex(string name, IEnumerable<IndexColumn> indexColumns, bool unique)
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
            IsUnique = unique
        });
    }

    public KernelStorageUnit Build(PortabilityValidationContext? context)
    {
        if (key is null)
            throw new InvalidOperationException("A storage declaration requires a key before Build().");

        var unit = new KernelStorageUnit
        {
            Id = new StorageUnitId(id),
            Name = name,
            Columns = Array.AsReadOnly(columns.ToArray()),
            Key = new KeyDefinition { Columns = Array.AsReadOnly(key.Columns.ToArray()) },
            Indexes = Array.AsReadOnly(indexes.ToArray())
        };

        var result = BuilderPortabilityValidation.Validate(unit, context);
        if (!result.IsPortable)
        {
            throw new StorageDeclarationException(result.Refusals.Select(refusal =>
                new GroundworkDiagnostic(refusal.Code, refusal.Message, refusal.Path)));
        }

        return unit;
    }

    private static IReadOnlyList<string> SnapshotNames(IEnumerable<string> names, string parameterName)
    {
        var snapshot = (names ?? throw new ArgumentNullException(parameterName)).ToArray();
        if (snapshot.Length == 0 || snapshot.Any(name => string.IsNullOrWhiteSpace(name)))
            throw new ArgumentException("At least one non-empty column name is required.", parameterName);

        return Array.AsReadOnly(snapshot);
    }

    private static string RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);

        return value;
    }
}
