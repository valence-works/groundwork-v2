using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Query.Linq.Tests;

/// <summary>Versioned source spellings and their expected public lowering decision.</summary>
public sealed class LinqCorpusTests
{
    private static IReadOnlyDictionary<string, string> DiagnosticFixes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GW-LINQ-101"] = "declare a computed column; expressions over columns are not portable",
        ["GW-LINQ-102"] = "declare a computed column; expressions over columns are not portable",
        ["GW-LINQ-103"] = "add `.AcceptScan(...)`",
        ["GW-LINQ-104"] = "v2 has no joins; use a declared element set or two queries",
        ["GW-LINQ-105"] = "use `.LatestPer(...)` for grouped top-1",
        ["GW-LINQ-106"] = "declare the element set",
        ["GW-LINQ-107"] = "mark it `[GwQueryFragment]`",
        ["GW-LINQ-108"] = "use Ordinal/OrdinalIgnoreCase matching the column's folding",
        ["GW-LINQ-109"] = "use `DateTimeOffset.UtcNow`",
        ["GW-LINQ-110"] = "the value has more scale/range than `decimal(10,2)`"
    };
    private sealed record CorpusShape(string Spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> Build, string? ExpectedDiagnostic, string? ExpectedSpan, string? ExpectedSignature);
    public sealed record CorpusCase(string Spelling, int Value, string? ExpectedDiagnostic, string? ExpectedSpan, string? ExpectedSignature, Expression<Func<LinqFrontEndTests.Ticket, bool>> Expression);
    public static IReadOnlyList<object[]> Corpus => Cases.Select(item => new object[] { item }).ToArray();

    [Fact]
    public void Corpus_and_documentation_vocabulary_are_versioned_together()
    {
        Assert.Equal(250, Cases.Count);
        Assert.Equal(Cases.Count, Cases.Select(item => item.Spelling).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Cases.Count, Cases.Select(item => item.Expression.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Cases.Count, Cases.Select(item => ExpressionShape(item.Expression)).Distinct(StringComparer.Ordinal).Count());
        var expectedCodes = Cases.Where(item => item.ExpectedDiagnostic is not null).Select(item => item.ExpectedDiagnostic!).Distinct(StringComparer.Ordinal).OrderBy(code => code, StringComparer.Ordinal).ToArray();
        Assert.Equal(DiagnosticFixes.Keys.OrderBy(code => code, StringComparer.Ordinal), expectedCodes);
        var docs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/v2/query-linq.md"));
        Assert.True(File.Exists(docs), $"Expected generated documentation at {docs}");
        var document = File.ReadAllText(docs);
        var start = document.IndexOf("| Code | AST equivalent / fix |", StringComparison.Ordinal);
        Assert.True(start >= 0, "The generated diagnostic table is missing.");
        var tableLines = document[start..].Split('\n').TakeWhile(line => line.StartsWith("|", StringComparison.Ordinal)).ToArray();
        Assert.Equal(GenerateDiagnosticTable(), string.Join('\n', tableLines) + "\n");
        var corpusStart = document.IndexOf("| Decision | Corpus forms |", StringComparison.Ordinal);
        Assert.True(corpusStart >= 0, "The corpus lowering table is missing.");
        var corpusLines = document[corpusStart..].Split('\n').TakeWhile(line => line.StartsWith("|", StringComparison.Ordinal)).ToArray();
        Assert.Equal(GenerateCorpusTable(), string.Join('\n', corpusLines) + "\n");
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Every_versioned_spelling_has_its_recorded_public_decision(CorpusCase item)
    {
        var diagnostics = ExpressionLowerer.Diagnose(item.Expression, LinqFrontEndTests.Tickets);
        if (item.ExpectedDiagnostic is not null)
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal(item.ExpectedDiagnostic, diagnostic.Code);
            Assert.Equal(item.ExpectedSpan, SpanIdentity(diagnostic.Span));
            return;
        }
        Assert.Empty(diagnostics);
        var lowered = ExpressionLowerer.Lower(item.Expression, LinqFrontEndTests.Tickets);
        Assert.Equal(item.ExpectedSignature, AstSignature(lowered));
    }

    private static IReadOnlyList<CorpusCase> Cases => Shapes.SelectMany(shape => Enumerable.Range(0, 5).Select(value =>
    {
        var expression = Variant(shape.Build(value), value);
        var expectedSignature = shape.ExpectedDiagnostic is null ? VariantSignature(shape.ExpectedSignature!, value) : null;
        return new CorpusCase(string.Format(CultureInfo.InvariantCulture, shape.Spelling, value) + VariantSuffix(value), value, shape.ExpectedDiagnostic, shape.ExpectedSpan, expectedSignature, expression);
    })).ToArray();

    private static Expression<Func<LinqFrontEndTests.Ticket, bool>> Variant(Expression<Func<LinqFrontEndTests.Ticket, bool>> source, int variant)
    {
        var body = variant switch
        {
            0 => source.Body,
            1 => Expression.AndAlso(source.Body, Expression.Equal(Expression.Property(source.Parameters[0], nameof(LinqFrontEndTests.Ticket.Marker)), Expression.Constant(100 + variant))),
            2 => Expression.AndAlso(source.Body, Expression.GreaterThan(Expression.Property(source.Parameters[0], nameof(LinqFrontEndTests.Ticket.Marker)), Expression.Constant(200 + variant))),
            3 => Expression.AndAlso(source.Body, Expression.LessThan(Expression.Property(source.Parameters[0], nameof(LinqFrontEndTests.Ticket.Marker)), Expression.Constant(300 + variant))),
            _ => Expression.AndAlso(source.Body, Expression.NotEqual(Expression.Property(source.Parameters[0], nameof(LinqFrontEndTests.Ticket.Marker)), Expression.Constant(400 + variant)))
        };
        return Expression.Lambda<Func<LinqFrontEndTests.Ticket, bool>>(body, source.Parameters);
    }

    private static string VariantSuffix(int variant) => variant switch
    {
        0 => string.Empty,
        1 => " && ticket.Marker == " + (100 + variant).ToString(CultureInfo.InvariantCulture),
        2 => " && ticket.Marker > " + (200 + variant).ToString(CultureInfo.InvariantCulture),
        3 => " && ticket.Marker < " + (300 + variant).ToString(CultureInfo.InvariantCulture),
        _ => " && ticket.Marker != " + (400 + variant).ToString(CultureInfo.InvariantCulture)
    };

    private static string ExpressionShape(Expression expression)
    {
        var builder = new StringBuilder();
        new ShapeVisitor(builder).Visit(expression);
        return builder.ToString();
    }

    private sealed class ShapeVisitor : ExpressionVisitor
    {
        private readonly StringBuilder builder;
        public ShapeVisitor(StringBuilder builder) => this.builder = builder;
        public override Expression? Visit(Expression? node)
        {
            if (node is null) return null;
            builder.Append(node.NodeType).Append(':').Append(node.Type.FullName).Append(';');
            return base.Visit(node);
        }
        protected override Expression VisitMember(MemberExpression node)
        {
            builder.Append(node.Member.DeclaringType?.FullName).Append('.').Append(node.Member.Name).Append(';');
            return base.VisitMember(node);
        }
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            builder.Append(node.Method.DeclaringType?.FullName).Append('.').Append(node.Method.Name).Append(';');
            return base.VisitMethodCall(node);
        }
    }

    private static string AstSignature(Predicate predicate) => predicate switch
    {
        Predicate.Equal => "Equal",
        Predicate.Range => "Range",
        Predicate.Not not => "Not(" + AstSignature(not.Inner) + ")",
        Predicate.And and => "And(" + string.Join(',', and.Terms.Select(AstSignature).OrderBy(value => value, StringComparer.Ordinal)) + ")",
        Predicate.Or or => "Or(" + string.Join(',', or.Terms.Select(AstSignature).OrderBy(value => value, StringComparer.Ordinal)) + ")",
        Predicate.In => "In",
        Predicate.ElementOf => "ElementOf",
        Predicate.Substring => "Substring",
        Predicate.StartsWith => "StartsWith",
        _ => predicate.GetType().Name
    };

    private static string VariantSignature(string baseSignature, int variant)
    {
        if (variant == 0) return baseSignature;
        var extra = variant switch
        {
            1 => "Equal",
            2 or 3 => "Range",
            _ => "Not(Equal)"
        };
        var terms = baseSignature.StartsWith("And(", StringComparison.Ordinal)
            ? baseSignature[4..^1].Split(',', StringSplitOptions.RemoveEmptyEntries).Append(extra)
            : new[] { baseSignature, extra };
        return "And(" + string.Join(',', terms.OrderBy(value => value, StringComparer.Ordinal)) + ")";
    }

    private static string SpanIdentity(Expression expression) => expression switch
    {
        MethodCallExpression call => "Call:" + call.Method.Name,
        MemberExpression member => "Member:" + member.Member.Name,
        BinaryExpression binary => "Binary:" + binary.NodeType,
        ConstantExpression constant => "Constant:" + constant.Type.Name,
        _ => expression.NodeType.ToString()
    };

    private static string GenerateCorpusTable()
    {
        var builder = new StringBuilder("| Decision | Corpus forms |\n| --- | --- |\n");
        var accepted = Cases.Where(item => item.ExpectedDiagnostic is null).Select(item => item.ExpectedSignature!).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal);
        var rejected = Cases.Where(item => item.ExpectedDiagnostic is not null).Select(item => item.ExpectedDiagnostic!).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal);
        builder.Append("| Accepted ASTs | ").Append(string.Join(", ", accepted)).Append(" |\n");
        builder.Append("| Rejected diagnostics | ").Append(string.Join(", ", rejected)).Append(" |\n");
        return builder.ToString();
    }

    private static string GenerateDiagnosticTable()
    {
        var builder = new StringBuilder("| Code | AST equivalent / fix |\n| --- | --- |\n");
        foreach (var entry in DiagnosticFixes.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            builder.Append("| ").Append(entry.Key).Append(" | ").Append(char.ToUpperInvariant(entry.Value[0])).Append(entry.Value[1..]).Append(". |\n");
        return builder.ToString();
    }

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
        Shape("ticket.Status!.ToLower() == \"x\" && ticket.IsOpen == ({0} >= 0)", value => ticket => ticket.Status!.ToLower() == "x" && ticket.IsOpen == (value >= 0), "GW-LINQ-101", "Binary:Equal"),
        Shape("ticket.Status!.Length > {0}", value => ticket => ticket.Status!.Length > value, "GW-LINQ-101", "Binary:GreaterThan"),
        Shape("ticket.CreatedAt.UtcDateTime == DateTime.UtcNow", value => ticket => ticket.CreatedAt.UtcDateTime == DateTime.UtcNow, "GW-LINQ-104", "Binary:Equal"),
        Shape("ticket.TagIds.Any(value => value > {0})", value => ticket => ticket.TagIds.Any(item => item > value), "GW-LINQ-106"),
        Shape("ticket.TagIds.All(value => value > {0})", value => ticket => ticket.TagIds.All(item => item > value), "GW-LINQ-106", "Call:All"),
        Shape("ticket.Status!.Contains(\"x{0}\", StringComparison.CurrentCulture)", value => ticket => ticket.Status!.Contains("x" + value, StringComparison.CurrentCulture), "GW-LINQ-108", "Call:Contains"),
        Shape("ticket.CreatedAt == DateTimeOffset.Now", value => ticket => ticket.CreatedAt == DateTimeOffset.Now, "GW-LINQ-109"),
        Shape("ticket.CreatedAt >= DateTimeOffset.Now", value => ticket => ticket.CreatedAt >= DateTimeOffset.Now, "GW-LINQ-109", "Member:Now"),
        Shape("ticket.Amount == 1.23456m + {0}m", value => ticket => ticket.Amount == 1.23456m + value, "GW-LINQ-110"),
        Shape("ticket.OtherTenant == ticket.TenantId", value => ticket => ticket.OtherTenant == ticket.TenantId, "GW-LINQ-103"),
        Shape("ticket.TenantId * {0} > 2", value => ticket => ticket.TenantId * value > 2, "GW-LINQ-102"),
        Shape("ticket.Status!.Substring(0, {0}) == \"x\"", value => ticket => ticket.Status!.Substring(0, value) == "x", "GW-LINQ-101", "Binary:Equal"),
        Shape("ticket.Status!.Contains(\"x{0}\", StringComparison.OrdinalIgnoreCase)", value => ticket => ticket.Status!.Contains("x" + value, StringComparison.OrdinalIgnoreCase), "GW-LINQ-108", "Call:Contains"),
        Shape("ticket.TenantId / ({0} + 1) > 0", value => ticket => ticket.TenantId / (value + 1) > 0, "GW-LINQ-102"),
        Shape("ticket.TagIds.Any(value => value != {0})", value => ticket => ticket.TagIds.Any(item => item != value), "GW-LINQ-106", "Call:Any"),
        Shape("ticket.CreatedAt.Year != ({0} + 2026)", value => ticket => ticket.CreatedAt.Year != value + 2026, typeof(Predicate.Range)),
        Shape("ticket.CreatedAt.Date <= new DateTime({0} + 2026, 1, 1)", value => ticket => ticket.CreatedAt.Date <= new DateTime(value + 2026, 1, 1), typeof(Predicate.Range)),
        Shape("ticket.OtherTenant > ticket.TenantId", value => ticket => ticket.OtherTenant > ticket.TenantId, "GW-LINQ-103", "Binary:GreaterThan"),
        Shape("ticket.TagIds.GroupBy(value => value).Any()", value => ticket => ticket.TagIds.GroupBy(item => item).Any(), "GW-LINQ-105"),
        Shape("ticket.IsOpen == (0 == {0})", value => ticket => ticket.IsOpen == (0 == value), typeof(Predicate.Equal)),
    };

    private static CorpusShape Shape(string spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> build, Type expectedAst) => new(spelling, build, null, null, SignatureForType(expectedAst));
    private static CorpusShape Shape(string spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> build, string expectedDiagnostic) => new(spelling, build, expectedDiagnostic, ExpectedSpanForDiagnostic(expectedDiagnostic), null);
    private static CorpusShape Shape(string spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> build, string expectedDiagnostic, string expectedSpan) => new(spelling, build, expectedDiagnostic, expectedSpan, null);
    private static string SignatureForType(Type type) => type == typeof(Predicate.Not) ? "Not(Equal)" : type == typeof(Predicate.And) ? "And(Equal,Equal)" : type == typeof(Predicate.Or) ? "Or(Equal,Equal)" : type.Name;
    private static string ExpectedSpanForDiagnostic(string code) => code switch
    {
        "GW-LINQ-103" => "Binary:Equal",
        "GW-LINQ-102" => "Binary:GreaterThan",
        "GW-LINQ-108" => "Call:StartsWith",
        "GW-LINQ-107" => "Call:IsOpen",
        "GW-LINQ-101" => "Binary:Equal",
        "GW-LINQ-104" => "Binary:Equal",
        "GW-LINQ-106" => "Call:Any",
        "GW-LINQ-105" => "Call:Any",
        "GW-LINQ-109" => "Member:Now",
        "GW-LINQ-110" => "Binary:Equal",
        _ => throw new InvalidOperationException(code)
    };
    private static Expression<Func<LinqFrontEndTests.Ticket, bool>> LongAtLeast(int value)
    {
        long threshold = value;
        return ticket => ticket.LongValue >= threshold;
    }
    private static bool IsOpen(LinqFrontEndTests.Ticket ticket) => ticket.IsOpen;
}
