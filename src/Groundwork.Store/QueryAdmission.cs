using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>
/// The native budgets a session's queries must fit. This is the whole of what is genuinely
/// provider-specific about admitting a query: the coverage decision itself is provider-neutral and
/// is never restated per provider. A caller reads these from the provider that owns them instead of
/// guessing, so a pre-execution fence can never refuse a request the provider's own renderer would
/// have accepted — or accept one it would have refused.
/// </summary>
public sealed record QueryAdmissionProfile
{
    /// <summary>The portable profile used by a session that advertises no native budgets.</summary>
    public static QueryAdmissionProfile Default { get; } = new();

    /// <summary>The provider's real bound on bound parameters in one command.</summary>
    public int MaximumParameters { get; init; } = 2_100;

    /// <summary>The provider's real bound on distinct values in one membership predicate.</summary>
    public int MaximumInValues { get; init; } = QueryRenderOptions.Default.InValueLimit;
}

/// <summary>Optional provider capability advertising the native budgets a session queries under.</summary>
public interface IQueryAdmissionStorageSession
{
    QueryAdmissionProfile QueryAdmission { get; }
}

/// <summary>Reads a session's admission profile without forcing every provider to advertise one.</summary>
public static class QueryAdmissionSessionExtensions
{
    /// <summary>
    /// Returns the session's advertised profile, or the portable default when a session — or a
    /// decorator that does not forward the capability — advertises none.
    /// </summary>
    public static QueryAdmissionProfile GetQueryAdmission(this IStorageSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session is IQueryAdmissionStorageSession advertised
            ? advertised.QueryAdmission
            : QueryAdmissionProfile.Default;
    }
}
