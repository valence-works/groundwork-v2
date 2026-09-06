using Groundwork.Kernel;

namespace Groundwork.Substrate.Relational;

/// <summary>Session-local opaque identities; native names never leave this provider-owned map.</summary>
internal sealed class RelationalEvidenceCapture
{
    private readonly Action<Action>? invokeStructuredObserver;
    private readonly Dictionary<string, ProviderOpaqueIdentity> targets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderOpaqueIdentity> indexes = new(StringComparer.Ordinal);

    internal RelationalEvidenceCapture(Action<Action>? invokeStructuredObserver = null) =>
        this.invokeStructuredObserver = invokeStructuredObserver;

    internal ProviderOpaqueIdentity Id { get; } = new(Guid.NewGuid());

    internal ProviderExecutionIdentity NewInvocation() =>
        new(Id, new(Guid.NewGuid()), new(Guid.NewGuid()), new(Guid.NewGuid()), 0, 0);

    internal ProviderExecutionTarget Target(StorageUnit unit, ProviderScopeBindingMode scope) =>
        new(unit.Id, Resolve(targets, unit.Name), scope);

    internal ProviderOpaqueIdentity Index(string physicalName) => Resolve(indexes, physicalName);

    internal void InvokeStructuredObserver(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (invokeStructuredObserver is null)
            callback();
        else
            invokeStructuredObserver(callback);
    }

    internal T InvokeStructuredObserver<T>(Func<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        T result = default!;
        InvokeStructuredObserver(() => { result = callback(); });
        return result;
    }

    private static ProviderOpaqueIdentity Resolve(Dictionary<string, ProviderOpaqueIdentity> identities, string name)
    {
        // Session execution normally holds a provider gate. Keep identity assignment safe for
        // providers that allow concurrent non-transactional reads as well.
        lock (identities)
        {
            if (!identities.TryGetValue(name, out var identity))
                identities.Add(name, identity = new(Guid.NewGuid()));
            return identity;
        }
    }
}
