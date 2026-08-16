namespace Groundwork.Store;

/// <summary>Internal test seam for proving native aggregation round-trip counts.</summary>
internal static class AggregationExecutionDiagnostics
{
    private static readonly AsyncLocal<Action<string>?> Current = new();

    internal static Action<string>? Observer
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    internal static void Observe(string operation) => Observer?.Invoke(operation);
}
