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
        Assert.Equal(surface, QueryResolver.TerminalNames.OrderBy(name => name, StringComparer.Ordinal));
    }

    private static bool IsTerminalReturn(Type type) =>
        type.IsGenericType &&
        (type.GetGenericTypeDefinition() == typeof(LinqTerminal<>) ||
         type.GetGenericTypeDefinition() == typeof(Task<>));
}
