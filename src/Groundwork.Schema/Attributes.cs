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

/// <summary>Whether rows of a declared table are partitioned by a storage scope.</summary>
public enum SchemaScope
{
    Global,
    Scoped
}

/// <summary>System-owned timestamp policy for a declared table.</summary>
public enum SchemaTimestamps
{
    None
}

/// <summary>When declared retention is enforced.</summary>
public enum SchemaRetentionTrigger
{
    Explicit,
    OnAppend
}

/// <summary>The closed set of calendar grouping operations available to a declared aggregation.</summary>
public enum SchemaTimeBucket
{
    None,
    FixedUtc,
    LocalCalendarDay
}

/// <summary>The closed set of reductions available to a declared aggregation.</summary>
public enum SchemaAggregateKind
{
    Min,
    Max,
    Count,
    Sum,
    SetUnion,
    FirstBy
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GwTableAttribute : Attribute
{
    public GwTableAttribute(string name) => Name = AttributeValidation.Require(name, nameof(name));

    public string Name { get; }

    /// <summary>
    /// The stable logical identity of this table. Spell it only when renaming the physical
    /// <see cref="Name"/>, keeping the original name as the id, so the change deploys as a rename
    /// instead of dropping the old storage and creating new empty storage.
    /// </summary>
    public string? Id { get; set; }

    public SchemaScope Scope { get; set; } = SchemaScope.Global;
    /// <summary>Names the system-owned optimistic concurrency token, opting the table into it.</summary>
    public string? ConcurrencyToken { get; set; }
    public SchemaTimestamps Timestamps { get; set; } = SchemaTimestamps.None;
}

/// <summary>Declares how many newest rows survive, optionally independently per partition.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GwRetentionAttribute : Attribute
{
    public GwRetentionAttribute(int keepNewest, string orderBy)
    {
        KeepNewest = keepNewest;
        OrderBy = AttributeValidation.Require(orderBy, nameof(orderBy));
    }

    public int KeepNewest { get; }
    public string OrderBy { get; }
    public SchemaRetentionTrigger Trigger { get; set; } = SchemaRetentionTrigger.Explicit;
    /// <summary>Comma-separated partition columns.</summary>
    public string? PartitionBy { get; set; }
}

/// <summary>Declares the durable replay window for append operations.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GwAppendIdempotencyAttribute : Attribute
{
    public GwAppendIdempotencyAttribute(string window) => Window = AttributeValidation.Require(window, nameof(window));

    public string Window { get; }
    /// <summary>Overrides the kernel-owned default ledger name.</summary>
    public string? LedgerName { get; set; }
}

/// <summary>Declares the durable replay window for operation-identified retention.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GwRetentionIdempotencyAttribute : Attribute
{
    public GwRetentionIdempotencyAttribute(string window) => Window = AttributeValidation.Require(window, nameof(window));

    public string Window { get; }
    /// <summary>Overrides the kernel-owned default ledger name.</summary>
    public string? LedgerName { get; set; }
}

/// <summary>
/// Declares one closed aggregation shape. The specification is a comma-separated term list:
/// <c>group &lt;alias&gt;</c>, <c>bucket &lt;alias&gt; &lt;column&gt; &lt;width&gt;</c>,
/// <c>day &lt;alias&gt; &lt;column&gt;</c>, <c>count &lt;alias&gt;</c>,
/// <c>min|max|sum &lt;alias&gt; &lt;column&gt;</c>, <c>setUnion &lt;alias&gt; &lt;column&gt; &lt;maxValues&gt;</c>,
/// and <c>firstBy &lt;alias&gt; &lt;column&gt; &lt;orderColumn&gt; ASC|DESC</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class GwAggregateAttribute : Attribute
{
    public GwAggregateAttribute(string name, string specification)
    {
        Name = AttributeValidation.Require(name, nameof(name));
        Specification = AttributeValidation.Require(specification, nameof(specification));
    }

    public string Name { get; }
    public string Specification { get; }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class GwColumnAttribute : Attribute
{
    public string? Name { get; set; }

    /// <summary>
    /// The stable logical identity of this column. Spell it only when renaming the physical
    /// <see cref="Name"/>, keeping the original name as the id, so the change deploys as a rename
    /// instead of dropping the old column and adding a new empty one.
    /// </summary>
    public string? Id { get; set; }

    public int Length { get; set; } = -1;
    public int Precision { get; set; } = -1;
    public int Scale { get; set; } = -1;
    public TextFolding Folding { get; set; } = TextFolding.None;
    public SchemaGeneration Generation { get; set; } = SchemaGeneration.Supplied;
    public bool Required { get; set; }
    /// <summary>The portable default in its invariant text form, read against the column type.</summary>
    public string? Default { get; set; }
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
