using System.Collections.Generic;
using System.Linq.Expressions;

namespace Groundwork.Query.Linq.Tests.Lookalikes;

public static class EnumerableExtensionFactory
{
    public sealed class CustomValues
    {
    }

    public static bool Contains(this CustomValues values, int value) => false;

    public static Expression<System.Func<LinqFrontEndTests.Ticket, bool>> Create(CustomValues values) =>
        ticket => values.Contains(ticket.TenantId);
}
