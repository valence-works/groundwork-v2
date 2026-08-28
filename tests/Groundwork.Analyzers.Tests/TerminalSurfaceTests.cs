using System.Reflection;
using Groundwork.Analyzers;
using Groundwork.Query.Linq;
using Xunit;

namespace Groundwork.Analyzers.Tests;

/// <summary>Pins the recognized terminal names to the real closed query surface.</summary>
public sealed class TerminalSurfaceTests
{
    [Fact]
    public void Recognized_terminals_are_exactly_the_closed_surface_terminals()
    {
        var interfaceTerminals = typeof(IGwQueryable<>).GetMethods()
            .Where(method => IsTerminalReturn(method.ReturnType))
            .Select(method => method.Name);
        var extensionTerminals = typeof(GwQueryAsyncExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetParameters().FirstOrDefault()?.ParameterType is { IsGenericType: true } receiver &&
                             receiver.GetGenericTypeDefinition() == typeof(IGwQueryable<>))
            .Select(method => method.Name);
        var surface = interfaceTerminals.Concat(extensionTerminals)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(surface);
        Assert.Contains("SumAsync", surface);
        Assert.Contains("MinAsync", surface);
        Assert.Contains("MaxAsync", surface);
        Assert.Equal(surface, QueryResolver.TerminalNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void Reduction_surface_is_closed_and_empty_safe()
    {
        var methods = typeof(IGwQueryable<>).GetMethods()
            .Where(method => method.Name is "Sum" or "Min" or "Max")
            .ToArray();

        Assert.Equal(6, methods.Count(method => method.Name == "Sum"));
        Assert.Equal(11, methods.Count(method => method.Name == "Min"));
        Assert.Equal(11, methods.Count(method => method.Name == "Max"));
        Assert.All(methods, method =>
        {
            Assert.False(method.IsGenericMethod);
            Assert.Equal(typeof(LinqTerminal<>), method.ReturnType.GetGenericTypeDefinition());
            var selectorType = method.GetParameters()[0].ParameterType.GetGenericArguments()[0];
            var selectedType = selectorType.GetGenericArguments()[1];
            var expectedType = method.Name == "Sum"
                ? selectedType == typeof(decimal) || selectedType == typeof(decimal?) ? typeof(decimal?) : typeof(long?)
                : selectedType == typeof(int) || selectedType == typeof(int?) ? typeof(int?)
                : selectedType == typeof(long) || selectedType == typeof(long?) ? typeof(long?)
                : selectedType == typeof(decimal) || selectedType == typeof(decimal?) ? typeof(decimal?)
                : selectedType == typeof(string) ? typeof(string)
                : selectedType == typeof(DateTimeOffset) || selectedType == typeof(DateTimeOffset?) ? typeof(DateTimeOffset?)
                : typeof(Guid?);
            Assert.Equal(expectedType, method.ReturnType.GetGenericArguments()[0]);
        });
    }

    private static bool IsTerminalReturn(Type type) =>
        type.IsGenericType &&
        (type.GetGenericTypeDefinition() == typeof(LinqTerminal<>) ||
         type.GetGenericTypeDefinition() == typeof(Task<>));
}
