using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>
/// The native budgets a provider's queries must fit. This is the whole of what is genuinely
/// provider-specific about admitting a query: the coverage decision itself is provider-neutral and
/// is never restated per provider.
/// <para>
/// A budget is a property of the provider <em>and its deployment</em> — SQLite's parameter ceiling is
/// a compile-time option of the library it was built from — which is why it is advertised by the
/// connection rather than assumed by the caller. Reading it from the provider that owns it keeps a
/// pre-execution fence in step with the renderer that enforces the same limit, instead of the number
/// living in two places that can disagree. Keyed batch-read chunks also have a provider-owned key
/// count and, where needed, an encoded payload budget.
/// </para>
/// <para>
/// A caller that supplies neither a connection nor a profile is admitted under a conservative
/// portable payload budget that stays below MongoDB's command-document ceiling. Providers may
/// advertise a deployment-specific budget through their connection, and an explicit profile still
/// controls the caller's chosen budget.
/// </para>
/// </summary>
public sealed record QueryAdmissionProfile
{
    /// <summary>
    /// The fence's own portable defaults, used when a connection advertises no budgets. The default
    /// payload budget is deliberately conservative so an omitted connection cannot produce a keyed
    /// batch-read command that reaches MongoDB's 16 MiB command-document ceiling.
    /// </summary>
    public static QueryAdmissionProfile Default { get; } = new()
    {
        MaximumBatchReadPayloadBytes = 15L * 1024 * 1024
    };

    /// <summary>
    /// The provider's real bound on bound parameters in one command, when a provider sets it. The
    /// initializer is the fence's portable default and is not a claim about any provider.
    /// </summary>
    public int MaximumParameters { get; init; } = 2_100;

    /// <summary>
    /// The provider's real bound on distinct values in one ordinary membership predicate, when a
    /// provider sets it. The initializer is the fence's portable default.
    /// </summary>
    public int MaximumInValues { get; init; } = QueryRenderOptions.Default.InValueLimit;

    /// <summary>
    /// The provider-owned maximum number of keys admitted to one keyed batch-read chunk. The
    /// portable fallback is 999, leaving one parameter slot for a provider-injected scope filter.
    /// </summary>
    public int MaximumBatchReadKeys { get; init; } = 999;

    /// <summary>
    /// Optional conservative encoded-payload budget for one keyed batch-read chunk. The default
    /// profile supplies a 15 MiB budget; providers with a different document-size ceiling can
    /// advertise a value here, and the portable planner estimates encoded key size before execution
    /// and splits chunks accordingly.
    /// </summary>
    public long? MaximumBatchReadPayloadBytes { get; init; }
}

/// <summary>
/// Optional provider capability advertising the native budgets a connection's queries are admitted
/// under. It belongs to the connection because that is where a deployment advertises what it can do,
/// and because a session decorator cannot drop what it does not wrap.
/// </summary>
public interface IQueryAdmissionProviderConnection
{
    QueryAdmissionProfile QueryAdmission { get; }
}

/// <summary>Reads a connection's admission profile without forcing every provider to advertise one.</summary>
public static class QueryAdmissionConnectionExtensions
{
    /// <summary>
    /// Returns the connection's advertised profile, or the portable default when it advertises none.
    /// </summary>
    public static QueryAdmissionProfile GetQueryAdmission(this IStorageProviderConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection is IQueryAdmissionProviderConnection advertised
            ? advertised.QueryAdmission
            : QueryAdmissionProfile.Default;
    }
}
