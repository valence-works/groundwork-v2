using System.Globalization;
using System.Linq.Expressions;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Query.Linq.Tests;

/// <summary>Versioned source spellings and their expected public lowering decision.</summary>
public sealed class LinqCorpusTests
{
    private sealed record CorpusShape(string Spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> Build, string? ExpectedDiagnostic, Type? ExpectedAst);
    public sealed record CorpusCase(string Spelling, int Value, string? ExpectedDiagnostic, Type? ExpectedAst, Expression<Func<LinqFrontEndTests.Ticket, bool>> Expression);
    public static IReadOnlyList<object[]> Corpus => Cases.Select(item => new object[] { item }).ToArray();

    [Fact]
    public void Corpus_and_documentation_vocabulary_are_versioned_together()
    {
        Assert.Equal(250, Cases.Count);
        var expectedCodes = Cases.Where(item => item.ExpectedDiagnostic is not null).Select(item => item.ExpectedDiagnostic!).Distinct(StringComparer.Ordinal).OrderBy(code => code, StringComparer.Ordinal).ToArray();
        Assert.Equal(LinqDiagnosticCatalog.Entries.Select(entry => entry.Code).OrderBy(code => code, StringComparer.Ordinal), expectedCodes);
        var docs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/v2/query-linq.md"));
        Assert.True(File.Exists(docs), $"Expected generated documentation at {docs}");
        var document = File.ReadAllText(docs);
        var start = document.IndexOf("| Code | AST equivalent / fix |", StringComparison.Ordinal);
        Assert.True(start >= 0, "The generated diagnostic table is missing.");
        var tableLines = document[start..].Split('\n').TakeWhile(line => line.StartsWith("|", StringComparison.Ordinal)).ToArray();
        Assert.Equal(LinqDiagnosticCatalog.GenerateMarkdownTable(), string.Join('\n', tableLines) + "\n");
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Every_versioned_spelling_has_its_recorded_public_decision(CorpusCase item)
    {
        var diagnostics = ExpressionLowerer.Diagnose(item.Expression, LinqFrontEndTests.Tickets);
        if (item.ExpectedDiagnostic is not null)
        {
            Assert.Contains(diagnostics, diagnostic => diagnostic.Code == item.ExpectedDiagnostic);
            return;
        }
        Assert.Empty(diagnostics);
        Assert.IsType(item.ExpectedAst!, ExpressionLowerer.Lower(item.Expression, LinqFrontEndTests.Tickets));
    }

    private static IReadOnlyList<CorpusCase> Cases => Shapes.SelectMany(shape => Enumerable.Range(0, 5).Select(value => new CorpusCase(
        string.Format(CultureInfo.InvariantCulture, shape.Spelling, value), value, shape.ExpectedDiagnostic, shape.ExpectedAst, shape.Build(value)))).ToArray();

    private static IReadOnlyList<CorpusShape> Shapes { get; } = new CorpusShape[]
    {
        Shape("ticket.TenantId == {0}", value => ticket => ticket.TenantId == value, typeof(Predicate.Equal)),
        Shape("ticket.TenantId != {0}", value => ticket => ticket.TenantId != value, typeof(Predicate.Not)),
        Shape("ticket.TenantId > {0}", value => ticket => ticket.TenantId > value, typeof(Predicate.Range)),
        Shape("ticket.TenantId >= {0}", value => ticket => ticket.TenantId >= value, typeof(Predicate.Range)),
        Shape("ticket.TenantId < {0}", value => ticket => ticket.TenantId < value, typeof(Predicate.Range)),
        Shape("ticket.TenantId <= {0}", value => ticket => ticket.TenantId <= value, typeof(Predicate.Range)),
        Shape("ticket.IsOpen", value => ticket => ticket.IsOpen == (value >= 0), typeof(Predicate.Equal)),
        Shape("!ticket.IsOpen", value => ticket => !ticket.IsOpen, typeof(Predicate.Not)),
        Shape("ticket.Status == null", value => ticket => ticket.Status == null, typeof(Predicate.Equal)),
        Shape("ticket.Status != null", value => ticket => ticket.Status != null, typeof(Predicate.Not)),
        Shape("ticket.Status == \"status-{0}\"", value => ticket => ticket.Status == "status-" + value, typeof(Predicate.Equal)),
        Shape("ticket.Status!.Contains(\"x{0}\", StringComparison.Ordinal)", value => ticket => ticket.Status!.Contains("x" + value, StringComparison.Ordinal), typeof(Predicate.Substring)),
        Shape("ticket.Status!.StartsWith(\"x{0}\", StringComparison.Ordinal)", value => ticket => ticket.Status!.StartsWith("x" + value, StringComparison.Ordinal), typeof(Predicate.StartsWith)),
        Shape("ticket.Status!.EndsWith(\"x{0}\", StringComparison.Ordinal)", value => ticket => ticket.Status!.EndsWith("x" + value, StringComparison.Ordinal), typeof(Predicate.Substring)),
        Shape("new[] {{ {0}, {0}+1 }}.Contains(ticket.TenantId)", value => ticket => new[] { value, value + 1 }.Contains(ticket.TenantId), typeof(Predicate.In)),
        Shape("ticket.Amount == {0}.01m", value => ticket => ticket.Amount == value + 0.01m, typeof(Predicate.Equal)),
        Shape("ticket.LongValue >= (long){0}", LongAtLeast, typeof(Predicate.Range)),
        Shape("ticket.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-{0})", value => ticket => ticket.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-value), typeof(Predicate.Range)),
        Shape("ticket.OptionalAt.HasValue", value => ticket => ticket.OptionalAt.HasValue, typeof(Predicate.Not)),
        Shape("ticket.OptionalAt!.Value >= DateTimeOffset.UtcNow", value => ticket => ticket.OptionalAt!.Value >= DateTimeOffset.UtcNow, typeof(Predicate.Range)),
        Shape("ticket.IsOpen && ticket.TenantId == {0}", value => ticket => ticket.IsOpen && ticket.TenantId == value, typeof(Predicate.And)),
        Shape("ticket.IsOpen || ticket.TenantId == {0}", value => ticket => ticket.IsOpen || ticket.TenantId == value, typeof(Predicate.Or)),
        Shape("ticket.TagIds.Any(value => value == {0})", value => ticket => ticket.TagIds.Any(item => item == value), typeof(Predicate.ElementOf)),
        Shape("ticket.TagIds.All(value => value == {0})", value => ticket => ticket.TagIds.All(item => item == value), typeof(Predicate.ElementOf)),
        Shape("ticket.CreatedAt.Year == ({0} + 2026)", value => ticket => ticket.CreatedAt.Year == value + 2026, typeof(Predicate.Range)),
        Shape("ticket.CreatedAt.Date == new DateTime({0} + 2026, 1, 1)", value => ticket => ticket.CreatedAt.Date == new DateTime(value + 2026, 1, 1), typeof(Predicate.Range)),
        Shape("ticket.TenantId == ticket.OtherTenant", value => ticket => ticket.TenantId == ticket.OtherTenant, "GW-LINQ-103"),
        Shape("ticket.TenantId + {0} > 2", value => ticket => ticket.TenantId + value > 2, "GW-LINQ-102"),
        Shape("ticket.Status!.StartsWith(\"x{0}\")", value => ticket => ticket.Status!.StartsWith("x" + value), "GW-LINQ-108"),
        Shape("IsOpen(ticket)", value => ticket => IsOpen(ticket), "GW-LINQ-107"),
        Shape("ticket.Status!.ToLower() == \"x\" && ticket.IsOpen == ({0} >= 0)", value => ticket => ticket.Status!.ToLower() == "x" && ticket.IsOpen == (value >= 0), "GW-LINQ-101"),
        Shape("ticket.Status!.Length > {0}", value => ticket => ticket.Status!.Length > value, "GW-LINQ-101"),
        Shape("ticket.CreatedAt.UtcDateTime == DateTime.UtcNow", value => ticket => ticket.CreatedAt.UtcDateTime == DateTime.UtcNow, "GW-LINQ-104"),
        Shape("ticket.TagIds.Any(value => value > {0})", value => ticket => ticket.TagIds.Any(item => item > value), "GW-LINQ-106"),
        Shape("ticket.TagIds.All(value => value > {0})", value => ticket => ticket.TagIds.All(item => item > value), "GW-LINQ-106"),
        Shape("ticket.Status!.Contains(\"x{0}\", StringComparison.CurrentCulture)", value => ticket => ticket.Status!.Contains("x" + value, StringComparison.CurrentCulture), "GW-LINQ-108"),
        Shape("ticket.CreatedAt == DateTimeOffset.Now", value => ticket => ticket.CreatedAt == DateTimeOffset.Now, "GW-LINQ-109"),
        Shape("ticket.CreatedAt >= DateTimeOffset.Now", value => ticket => ticket.CreatedAt >= DateTimeOffset.Now, "GW-LINQ-109"),
        Shape("ticket.Amount == 1.23456m + {0}m", value => ticket => ticket.Amount == 1.23456m + value, "GW-LINQ-110"),
        Shape("ticket.OtherTenant == ticket.TenantId", value => ticket => ticket.OtherTenant == ticket.TenantId, "GW-LINQ-103"),
        Shape("ticket.TenantId * {0} > 2", value => ticket => ticket.TenantId * value > 2, "GW-LINQ-102"),
        Shape("ticket.Status!.Substring(0, {0}) == \"x\"", value => ticket => ticket.Status!.Substring(0, value) == "x", "GW-LINQ-101"),
        Shape("ticket.Status!.Contains(\"x{0}\", StringComparison.OrdinalIgnoreCase)", value => ticket => ticket.Status!.Contains("x" + value, StringComparison.OrdinalIgnoreCase), "GW-LINQ-108"),
        Shape("ticket.TenantId / ({0} + 1) > 0", value => ticket => ticket.TenantId / (value + 1) > 0, "GW-LINQ-102"),
        Shape("ticket.TagIds.Any(value => value != {0})", value => ticket => ticket.TagIds.Any(item => item != value), "GW-LINQ-106"),
        Shape("ticket.CreatedAt.Year != ({0} + 2026)", value => ticket => ticket.CreatedAt.Year != value + 2026, typeof(Predicate.Range)),
        Shape("ticket.CreatedAt.Date <= new DateTime({0} + 2026, 1, 1)", value => ticket => ticket.CreatedAt.Date <= new DateTime(value + 2026, 1, 1), typeof(Predicate.Range)),
        Shape("ticket.OtherTenant > ticket.TenantId", value => ticket => ticket.OtherTenant > ticket.TenantId, "GW-LINQ-103"),
        Shape("ticket.TagIds.GroupBy(value => value).Any()", value => ticket => ticket.TagIds.GroupBy(item => item).Any(), "GW-LINQ-105"),
        Shape("ticket.IsOpen == (0 == {0})", value => ticket => ticket.IsOpen == (0 == value), typeof(Predicate.Equal)),
    };

    private static CorpusShape Shape(string spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> build, Type expectedAst) => new(spelling, build, null, expectedAst);
    private static CorpusShape Shape(string spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> build, string expectedDiagnostic) => new(spelling, build, expectedDiagnostic, null);
    private static Expression<Func<LinqFrontEndTests.Ticket, bool>> LongAtLeast(int value)
    {
        long threshold = value;
        return ticket => ticket.LongValue >= threshold;
    }
    private static bool IsOpen(LinqFrontEndTests.Ticket ticket) => ticket.IsOpen;
}
