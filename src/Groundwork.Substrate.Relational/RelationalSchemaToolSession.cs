using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Shared schema-tool session over the relational executor. Inspection stays read-only through
/// <see cref="RelationalSchemaExecutor.InspectDeployedHistory(PhysicalSchemaTarget)"/> so plan,
/// validate, status, and apply preflight provision nothing; only executing an authorized apply
/// touches provider infrastructure.
/// </summary>
public sealed class RelationalSchemaToolSession : ISchemaToolProviderSession
{
    private readonly Action? release;

    public RelationalSchemaToolSession(
        ProviderIdentity provider,
        RelationalSchemaExecutor executor,
        Action? release = null,
        Func<PhysicalSchemaTarget, PhysicalSchemaInspectionResult>? inspect = null)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Executor = executor ?? throw new ArgumentNullException(nameof(executor));
        Inspector = new DeployedHistoryInspector(inspect ?? executor.InspectDeployedHistory);
        this.release = release;
    }

    public ProviderIdentity Provider { get; }

    public IPhysicalSchemaExecutor Executor { get; }

    public IPhysicalSchemaHistoryInspector Inspector { get; }

    public void Dispose() => release?.Invoke();

    private sealed class DeployedHistoryInspector(
        Func<PhysicalSchemaTarget, PhysicalSchemaInspectionResult> inspect) : IPhysicalSchemaHistoryInspector
    {
        public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target) => inspect(target);
    }
}
