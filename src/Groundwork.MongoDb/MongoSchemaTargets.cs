using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.MongoDb;

/// <summary>
/// Physicalizes a declaration into the MongoDB schema target. The deployment tool compiles through
/// exactly this, so a tool-applied target and the target the runtime coordinator expects are one
/// value rather than two that happen to agree.
/// </summary>
public sealed class MongoSchemaTargetCompiler : IPhysicalSchemaTargetCompiler
{
    public PhysicalSchemaTarget Compile(StorageUnit declaration) => MongoSchemaTargets.Compile(declaration);
}

/// <summary>The MongoDB half of the provider-neutral schema target vocabulary.</summary>
public static class MongoSchemaTargets
{
    /// <summary>
    /// The provider name every MongoDB schema target and data-migration ledger entry is recorded
    /// under. It is <see cref="MongoDataMigrationExecutor.ProviderName"/>, not a second spelling:
    /// the schema ledger and the data-migration ledger address the same target identity.
    /// </summary>
    public static readonly ProviderIdentity Provider = new(MongoDataMigrationExecutor.ProviderName, "1.0");

    /// <summary>
    /// The provider-definition kind that records a derived column's folded search-key algorithm.
    /// It names the same fact the relational catalog table records, so a folded column is described
    /// identically in a plan whether the target is a table or a collection.
    /// </summary>
    public const string SearchKeyDefinitionKind = "search-key-algorithm";

    /// <summary>Separates the collection name from the derived column name in a definition identity.</summary>
    public const string SearchKeyDefinitionSeparator = "\u001f";

    /// <summary>
    /// The physical declaration MongoDB stores: folded text columns expanded into their
    /// provider-owned search keys, exactly as the runtime coordinator expands them.
    /// </summary>
    public static StorageUnit Physicalize(StorageUnit source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.InteropView is not null)
        {
            throw new NotSupportedException(
                "MongoDB cannot materialize one safe per-unit interop view because scoped units use separate physical collections. " +
                "Declare interop views only for relational providers.");
        }
        ProviderOwnedColumns.ValidateLogicalDeclaration(source);
        PortabilityValidator.EnsurePortableDefaults(source);
        var expanded = SearchKeyProjection.Expand(source);
        AggregationProfileValidator.ValidateUnit(expanded);
        PortabilityValidator.EnsurePhysicalIdentifiers(expanded);
        MongoDeclarationRules.Validate(expanded);
        var portability = PortabilityValidator.Validate(expanded, new PortabilityValidationContext(["mongodb"]));
        if (!portability.IsPortable)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                portability.Refusals.Select(refusal => $"{refusal.Code} at {refusal.Path}: {refusal.Message}")));
        }
        return expanded;
    }

    public static PhysicalSchemaTarget Compile(StorageUnit declaration)
    {
        var physical = Physicalize(declaration);
        return new PhysicalSchemaTarget(
            new SchemaSubject(physical),
            Provider,
            physical.DerivedColumns.Select(derived => new ProviderPhysicalSchemaDefinition(
                Provider.Name,
                physical.Id,
                SearchKeyDefinitionKind,
                physical.Name + SearchKeyDefinitionSeparator + derived.Name,
                derived.AlgorithmId ?? throw new InvalidOperationException(
                    $"Derived search-key column '{derived.Name}' is missing its algorithm identity."))).ToArray());
    }

    /// <summary>The derived column a search-key provider definition describes.</summary>
    internal static string DerivedColumnName(ProviderPhysicalSchemaDefinition definition)
    {
        var separator = definition.SubjectIdentity.LastIndexOf(SearchKeyDefinitionSeparator, StringComparison.Ordinal);
        return separator < 0
            ? definition.SubjectIdentity
            : definition.SubjectIdentity[(separator + SearchKeyDefinitionSeparator.Length)..];
    }
}
