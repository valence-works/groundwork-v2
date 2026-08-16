using System.Reflection;
using Groundwork.Samples.EventLog;
using Xunit;

namespace Groundwork.Samples.EventLog.Tests;

public sealed class EventLogArchitectureTests
{
    [Fact]
    public void Sample_references_only_the_kernel_product_assembly()
    {
        var references = typeof(EventLogDeclaration).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name?.StartsWith("Groundwork.", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Single(references, "Groundwork.Kernel");
        Assert.DoesNotContain(references, name => name is "Groundwork.Records" or "Groundwork.Documents");
    }

    [Fact]
    public void Declaration_stays_within_the_headline_line_count()
    {
        Assert.Equal(20, EventLogDeclaration.DeclarationLineCount);
        Assert.Equal(20, File.ReadAllLines(SourcePath()).Count(line =>
            line.TrimStart().StartsWith(".", StringComparison.Ordinal) ||
            line.Contains("LogRecords =", StringComparison.Ordinal)));
    }

    private static string SourcePath()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "samples", "Groundwork.Samples.EventLog", "EventLog.cs")))
            directory = Directory.GetParent(directory)?.FullName;
        return Path.Combine(directory ?? throw new InvalidOperationException("The repository root could not be located."), "samples", "Groundwork.Samples.EventLog", "EventLog.cs");
    }
}
