using System.Linq.Expressions;
using Groundwork.Query.Linq;

namespace Groundwork.Query.Linq.Fragments;

public sealed class ExternalTicket
{
    public bool IsOpen { get; set; }
}

public static class ExternalFragments
{
    [GwQueryFragment]
    public static Expression<Func<ExternalTicket, bool>> IsOpen => ticket => ticket.IsOpen;

    public static Expression<Func<ExternalTicket, bool>> Unmarked => ticket => ticket.IsOpen;
    public static bool UnmarkedTerm(ExternalTicket ticket) => ticket.IsOpen;

    [GwQueryFragment]
    public static bool IsOpenTerm(ExternalTicket ticket) => ticket.IsOpen;
}
