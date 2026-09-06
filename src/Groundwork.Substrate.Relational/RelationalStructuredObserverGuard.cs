namespace Groundwork.Substrate.Relational;

/// <summary>Rejects structured observer re-entry for one provider owner only.</summary>
internal sealed class RelationalStructuredObserverGuard
{
    private readonly string providerName;
    private readonly AsyncLocal<int> callbackDepth = new();

    internal RelationalStructuredObserverGuard(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        this.providerName = providerName;
    }

    internal void Invoke(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var previousDepth = callbackDepth.Value;
        callbackDepth.Value = previousDepth + 1;
        try
        {
            callback();
        }
        finally
        {
            callbackDepth.Value = previousDepth;
        }
    }

    internal void ThrowIfReentry()
    {
        if (callbackDepth.Value > 0)
            throw new InvalidOperationException(
                $"A {providerName} structured execution observer cannot re-enter its provider connection.");
    }
}
