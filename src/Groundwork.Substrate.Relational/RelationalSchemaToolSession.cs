using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>Shared schema-tool session over the relational executor; providers supply only their dialect, connection factory, and resource release.</summary>
public sealed class RelationalSchemaToolSession : ISchemaToolProviderSession
{
    private readonly RelationalSchemaExecutor executor;
    private readonly Action? release;

    public RelationalSchemaToolSession(
        ProviderIdentity provider,
        Func<DbConnection> createConnection,
        RelationalDialect dialect,
        Action? release = null)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        executor = new RelationalSchemaExecutor(createConnection, dialect);
        this.release = release;
    }

    public ProviderIdentity Provider { get; }

    public IPhysicalSchemaExecutor Executor => executor;

    public IPhysicalSchemaHistoryInspector Inspector => executor;

    public void Dispose() => release?.Invoke();
}
