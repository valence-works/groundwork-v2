using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Groundwork.Schema;

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
        SchemaGeneration generation = SchemaGeneration.Supplied)
    {
        Name = Require(name, nameof(name));
        Type = type;
        IsNullable = isNullable;
        Length = length;
        Precision = precision;
        Scale = scale;
        Folding = folding;
        Generation = generation;
    }

    public string Name { get; }
    public SchemaValueType Type { get; }
    public bool IsNullable { get; }
    public int? Length { get; }
    public int? Precision { get; }
    public int? Scale { get; }
    public TextFolding Folding { get; }
    public SchemaGeneration Generation { get; }

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

public sealed record SchemaTable
{
    public SchemaTable(
        string name,
        IEnumerable<SchemaColumn> columns,
        IEnumerable<string> key,
        IEnumerable<SchemaIndex>? indexes = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A non-empty value is required.", nameof(name)) : name;
        Columns = Snapshot(columns, nameof(columns));
        Key = Snapshot(key, nameof(key));
        Indexes = Snapshot(indexes ?? Array.Empty<SchemaIndex>(), nameof(indexes));
    }

    public string Name { get; }
    public IReadOnlyList<SchemaColumn> Columns { get; }
    public IReadOnlyList<string> Key { get; }
    public IReadOnlyList<SchemaIndex> Indexes { get; }

    private static IReadOnlyList<T> Snapshot<T>(IEnumerable<T> values, string parameterName) =>
        new ReadOnlyCollection<T>((values ?? throw new ArgumentNullException(parameterName)).ToArray());
}

public sealed record SchemaDocument
{
    public SchemaDocument(IEnumerable<SchemaTable> tables)
    {
        Tables = new ReadOnlyCollection<SchemaTable>((tables ?? throw new ArgumentNullException(nameof(tables))).ToArray());
    }

    public IReadOnlyList<SchemaTable> Tables { get; }
}
