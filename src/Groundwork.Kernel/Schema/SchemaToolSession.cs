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
    bool AllowCreate,
    CancellationToken CancellationToken);

/// <summary>Signals a schema-tool invocation the provider cannot honor; the tool reports it as an invocation error.</summary>
public sealed class SchemaToolProviderInvocationException(string message) : Exception(message);

/// <summary>Discoverable plug-in seam that opens provider sessions for the schema tool.</summary>
public interface ISchemaToolProviderSessionFactory
{
    string Alias { get; }
    ISchemaToolProviderSession Open(SchemaToolProviderOptions options);
}
