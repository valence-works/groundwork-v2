namespace Groundwork.Kernel.Schema;

/// <summary>One open deployment-tool connection to a provider's schema machinery.</summary>
public interface ISchemaToolProviderSession : IDisposable
{
    ProviderIdentity Provider { get; }
    IPhysicalSchemaExecutor Executor { get; }
    IPhysicalSchemaHistoryInspector Inspector { get; }
}

public sealed record SchemaToolProviderOptions(
    string Provider,
    string? Connection,
    string? Database,
    CancellationToken CancellationToken);

/// <summary>Discoverable plug-in seam that opens provider sessions for the schema tool.</summary>
public interface ISchemaToolProviderSessionFactory
{
    string Alias { get; }
    ISchemaToolProviderSession Open(SchemaToolProviderOptions options);
}
