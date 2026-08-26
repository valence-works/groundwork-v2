namespace Groundwork.Kernel;

/// <summary>Whether a provider command read data or changed it.</summary>
public enum ProviderCommandKind
{
    Read,
    Write
}

/// <summary>
/// One provider command issued while executing a storage session.
/// </summary>
/// <remarks>
/// Every command a session sends to its provider raises exactly one of these — reads, writes, probes and
/// retention alike. Schema work does not: <c>ISchemaCoordinator</c> hangs off the connection rather than a
/// session and issues its own commands, so DDL never reaches a session observer. That boundary is structural
/// rather than a documented exclusion.
/// </remarks>
public readonly record struct ProviderCommandEvent(
    string Operation,
    string? CommandText,
    ProviderCommandKind Kind,
    bool IsProbe);

/// <summary>
/// Optional observer for counting the provider round trips a session performs.
/// </summary>
/// <remarks>
/// The observer belongs to the session because the session is what issues commands. It deliberately does not
/// live on <c>WriteOptions</c>: those express an optimistic-concurrency precondition for one mutation, and an
/// observer is not a precondition. Keeping it there also made the batched write path take its observer from
/// whichever write happened to be staged first, so an observer attached to any later write was silently
/// ignored.
/// </remarks>
public interface IProviderCommandObserver
{
    void Observe(ProviderCommandEvent command);
}
