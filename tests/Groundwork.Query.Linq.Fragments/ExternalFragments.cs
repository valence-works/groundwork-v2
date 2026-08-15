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
}
