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
        Func<StorageUnit, PhysicalSchemaTarget> compile,
        Action? release = null,
        Func<PhysicalSchemaTarget, PhysicalSchemaInspectionResult>? inspect = null)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Executor = executor ?? throw new ArgumentNullException(nameof(executor));
        Targets = new DeclarationCompiler(compile ?? throw new ArgumentNullException(nameof(compile)));
        Inspector = new DeployedHistoryInspector(inspect ?? executor.InspectDeployedHistory);
        this.release = release;
    }

    public ProviderIdentity Provider { get; }

    public IPhysicalSchemaTargetCompiler Targets { get; }

    public IPhysicalSchemaExecutor Executor { get; }

    public IPhysicalSchemaHistoryInspector Inspector { get; }

    public void Dispose() => release?.Invoke();

    private sealed class DeclarationCompiler(
        Func<StorageUnit, PhysicalSchemaTarget> compile) : IPhysicalSchemaTargetCompiler
    {
        public PhysicalSchemaTarget Compile(StorageUnit declaration) => compile(declaration);
    }

    private sealed class DeployedHistoryInspector(
        Func<PhysicalSchemaTarget, PhysicalSchemaInspectionResult> inspect) : IPhysicalSchemaHistoryInspector
    {
        public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target) => inspect(target);
    }
}
