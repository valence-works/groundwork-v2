namespace Groundwork.Kernel.Schema;

/// <summary>
/// Physicalizes a logical declaration into the provider's schema target. A provider exposes the
/// same compilation its runtime coordinator uses, so a deployed target and the runtime's expected
/// target are one value rather than two that happen to agree.
/// </summary>
public interface IPhysicalSchemaTargetCompiler
{
    PhysicalSchemaTarget Compile(StorageUnit declaration);
}

/// <summary>One open deployment-tool connection to a provider's schema machinery.</summary>
public interface ISchemaToolProviderSession : IDisposable
{
    ProviderIdentity Provider { get; }
    IPhysicalSchemaTargetCompiler Targets { get; }
    IPhysicalSchemaExecutor Executor { get; }
    IPhysicalSchemaHistoryInspector Inspector { get; }

    /// <summary>
    /// The provider's data-migration execution, or null when it has none. Reporting pending versus
    /// applied data migrations needs only the ledger, so status works without a host transform
    /// catalog; a provider that returns null simply reports no data-migration state.
    /// </summary>
    IDataMigrationExecutor? DataMigrations => null;
}

public sealed record SchemaToolProviderOptions(
    string Provider,
    string? Connection,
    string? Database,
    bool AllowCreate,
    CancellationToken CancellationToken);

/// <summary>Signals a schema-tool invocation the provider cannot honor; the tool reports it as an invocation error.</summary>
public sealed class SchemaToolProviderInvocationException(string message) : Exception(message);

/// <summary>
/// A provider-session failure whose message the factory authored for operator display; the schema
/// tool echoes it. Raw driver errors must not use this type — the tool keeps those generic.
/// </summary>
public sealed class SchemaToolProviderException(string message) : Exception(message);

/// <summary>Discoverable plug-in seam that opens provider sessions for the schema tool.</summary>
public interface ISchemaToolProviderSessionFactory
{
    string Alias { get; }
    ISchemaToolProviderSession Open(SchemaToolProviderOptions options);
}
