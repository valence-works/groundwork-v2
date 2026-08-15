namespace Groundwork.Kernel;

/// <summary>One provider command observed while executing a write path.</summary>
public readonly record struct WritePathEvent(
    string Operation,
    string? CommandText,
    bool IsProbe);

/// <summary>Optional observer for measuring provider write-path round trips.</summary>
public interface IWritePathObserver
{
    void Observe(WritePathEvent command);
}
