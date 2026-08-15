using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;

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

public static class ExactNameEnumerableFactory
{
    private static readonly Lazy<Type> SpoofedEnumerable = new(CreateSpoofedEnumerable);

    public static Expression<Func<LinqFrontEndTests.Ticket, bool>> CreateContains()
    {
        var ticket = Expression.Parameter(typeof(LinqFrontEndTests.Ticket), "ticket");
        var method = SpoofedEnumerable.Value.GetMethod("Contains", BindingFlags.Public | BindingFlags.Static)!;
        var call = Expression.Call(method, Expression.Constant(new[] { 1, 2 }, typeof(IEnumerable<int>)), Expression.Property(ticket, nameof(LinqFrontEndTests.Ticket.TenantId)));
        return Expression.Lambda<Func<LinqFrontEndTests.Ticket, bool>>(call, ticket);
    }

    public static Expression<Func<LinqFrontEndTests.Ticket, bool>> CreateAny()
    {
        var ticket = Expression.Parameter(typeof(LinqFrontEndTests.Ticket), "ticket");
        var element = Expression.Parameter(typeof(int), "value");
        var predicate = Expression.Lambda<Func<int, bool>>(Expression.Equal(element, Expression.Constant(7)), element);
        var method = SpoofedEnumerable.Value.GetMethod("Any", BindingFlags.Public | BindingFlags.Static)!;
        var call = Expression.Call(method, Expression.Property(ticket, nameof(LinqFrontEndTests.Ticket.TagIds)), predicate);
        return Expression.Lambda<Func<LinqFrontEndTests.Ticket, bool>>(call, ticket);
    }

    private static Type CreateSpoofedEnumerable()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Groundwork.Linq.Spoof"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Groundwork.Linq.Spoof").DefineType(
            "System.Linq.Enumerable",
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        DefineBooleanMethod(type, "Contains", typeof(IEnumerable<int>), typeof(int));
        DefineBooleanMethod(type, "Any", typeof(IEnumerable<int>), typeof(Func<int, bool>));
        return type.CreateType()!;
    }

    private static void DefineBooleanMethod(TypeBuilder type, string name, params Type[] parameters)
    {
        var method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static, typeof(bool), parameters);
        method.GetILGenerator().Emit(OpCodes.Ldc_I4_1);
        method.GetILGenerator().Emit(OpCodes.Ret);
    }
}
