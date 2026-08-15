using System;

namespace Groundwork.Schema;

/// <summary>Text comparison policy for a declared column.</summary>
public enum TextFolding
{
    None,
    AsciiIgnoreCase,
    UnicodeOrdinalIgnoreCase
}

/// <summary>Portable value kinds understood by the schema generator.</summary>
public enum SchemaValueType
{
    String,
    Int32,
    Int64,
    Decimal,
    Boolean,
    DateTimeOffset,
    Guid,
    Binary,
    Json
}

/// <summary>How a value for a column is obtained.</summary>
public enum SchemaGeneration
{
    Supplied,
    ProviderSequence
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GwTableAttribute : Attribute
{
    public GwTableAttribute(string name) => Name = AttributeValidation.Require(name, nameof(name));

    public string Name { get; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class GwColumnAttribute : Attribute
{
    public string? Name { get; set; }
    public int Length { get; set; } = -1;
    public int Precision { get; set; } = -1;
    public int Scale { get; set; } = -1;
    public TextFolding Folding { get; set; } = TextFolding.None;
    public SchemaGeneration Generation { get; set; } = SchemaGeneration.Supplied;
    public bool Required { get; set; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class GwKeyAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class GwIndexAttribute : Attribute
{
    public GwIndexAttribute(string name, string specification)
    {
        Name = AttributeValidation.Require(name, nameof(name));
        Specification = AttributeValidation.Require(specification, nameof(specification));
    }

    public string Name { get; }
    public string Specification { get; }
    public bool IncludeNulls { get; set; } = true;
    public bool Unique { get; set; }
}

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
public sealed class GroundworkSchemaAttribute : Attribute
{
    public GroundworkSchemaAttribute(string canonicalJson, string fingerprint)
    {
        CanonicalJson = AttributeValidation.Require(canonicalJson, nameof(canonicalJson));
        Fingerprint = AttributeValidation.Require(fingerprint, nameof(fingerprint));
    }

    public string CanonicalJson { get; }
    public string Fingerprint { get; }
}

internal static class AttributeValidation
{
    public static string Require(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value;
}
