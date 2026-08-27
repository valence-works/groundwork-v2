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
/// living in two places that can disagree.
/// </para>
/// <para>
/// The fence is only as accurate as what it was given. A caller that supplies neither a connection
/// nor a profile is admitted under the portable defaults, and a request between those defaults and
/// the provider's real limit will pass admission and then be refused by the renderer instead —
/// later, and with a rendering code rather than an admission one. It still refuses; what degrades is
/// the diagnostic.
/// </para>
/// </summary>
public sealed record QueryAdmissionProfile
{
    /// <summary>
    /// The fence's own portable defaults, used when a connection advertises no budgets. They are not
    /// a claim about any provider: a connection that advertises nothing is admitted under these and
    /// then held to its real limit by its own renderer, which refuses by name.
    /// </summary>
    public static QueryAdmissionProfile Default { get; } = new();

    /// <summary>
    /// The provider's real bound on bound parameters in one command, when a provider sets it. The
    /// initializer is the fence's portable default and is not a claim about any provider.
    /// </summary>
    public int MaximumParameters { get; init; } = 2_100;

    /// <summary>
    /// The provider's real bound on distinct values in one membership predicate, when a provider
    /// sets it. The initializer is the fence's portable default.
    /// </summary>
    public int MaximumInValues { get; init; } = QueryRenderOptions.Default.InValueLimit;
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
