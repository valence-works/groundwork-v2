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
    public sealed record CorpusDecision(string? Code, string Title, string AstEquivalent, string? FixText);
    private sealed record CorpusShape(string Spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> Build, CorpusDecision Decision, string? ExpectedSpan, string? ExpectedSignature);
    public sealed record CorpusCase(string Spelling, int Value, CorpusDecision Decision, string? ExpectedSpan, string? ExpectedSignature, Expression<Func<LinqFrontEndTests.Ticket, bool>> Expression);

    private static IReadOnlyList<CorpusDecision> Decisions { get; } = new[]
    {
        new CorpusDecision(null, "Equality", "`Predicate.Equal(column, constant)`", null),
        new CorpusDecision(null, "Inequality", "`Predicate.Not(Predicate.Equal(column, constant))`", null),
        new CorpusDecision(null, "Range", "`Predicate.Range(column, lower?, upper?)`, retaining bound inclusivity", null),
        new CorpusDecision(null, "Conjunction", "`Predicate.And(terms)`, normalized by term", null),
        new CorpusDecision(null, "Disjunction", "`Predicate.Or(terms)`, normalized by term", null),
        new CorpusDecision(null, "Membership", "`Predicate.In(column, values)`, with the value count retained", null),
        new CorpusDecision(null, "Element-set quantifier", "`Predicate.ElementOf(set, values, Any|All)`", null),
        new CorpusDecision(null, "Substring matching", "`Predicate.Substring(column, needle, Contains|EndsWith)`", null),
        new CorpusDecision(null, "Prefix matching", "`Predicate.StartsWith(column, prefix)`", null),
        new CorpusDecision("GW-LINQ-101", "Computed/member expression", "", "declare a computed column; expressions over columns are not portable"),
        new CorpusDecision("GW-LINQ-102", "Arithmetic expression", "", "declare a computed column; expressions over columns are not portable"),
        new CorpusDecision("GW-LINQ-103", "Column-to-column comparison", "", "add `.AcceptScan(...)`"),
        new CorpusDecision("GW-LINQ-104", "Cross-table expression", "", "v2 has no joins; use a declared element set or two queries"),
        new CorpusDecision("GW-LINQ-105", "Grouped top-one", "", "use `.LatestPer(...)` for grouped top-1"),
        new CorpusDecision("GW-LINQ-106", "Unsupported element-set predicate", "", "declare the element set"),
        new CorpusDecision("GW-LINQ-107", "Opaque helper", "", "mark it `[GwQueryFragment]`"),
        new CorpusDecision("GW-LINQ-108", "Unpinned string comparison", "", "use Ordinal/OrdinalIgnoreCase matching the column's folding"),
        new CorpusDecision("GW-LINQ-109", "Non-UTC clock", "", "use `DateTimeOffset.UtcNow`"),
        new CorpusDecision("GW-LINQ-110", "Decimal precision/scale", "", "the value has more scale/range than `decimal(10,2)`")
    };
    public static IReadOnlyList<object[]> Corpus => Cases.Select(item => new object[] { item }).ToArray();

    [Fact]
    public void Corpus_and_documentation_vocabulary_are_versioned_together()
    {
        Assert.Equal(250, Cases.Count);
        Assert.Equal(Cases.Count, Cases.Select(item => item.Spelling).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Cases.Count, Cases.Select(item => item.Expression.ToString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Cases.Count, Cases.Select(item => ExpressionShape(item.Expression)).Distinct(StringComparer.Ordinal).Count());
        var expectedCodes = Cases.Where(item => item.Decision.Code is not null).Select(item => item.Decision.Code!).Distinct(StringComparer.Ordinal).OrderBy(code => code, StringComparer.Ordinal).ToArray();
        Assert.Equal(Decisions.Where(item => item.Code is not null).Select(item => item.Code!).OrderBy(code => code, StringComparer.Ordinal), expectedCodes);
        var docs = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/v2/query-linq.md"));
        Assert.True(File.Exists(docs), $"Expected generated documentation at {docs}");
        var document = File.ReadAllText(docs);
        var start = document.IndexOf("| Code | AST equivalent / fix |", StringComparison.Ordinal);
        Assert.True(start >= 0, "The generated diagnostic table is missing.");
        var tableLines = document[start..].Split('\n').TakeWhile(line => line.StartsWith("|", StringComparison.Ordinal)).ToArray();
        Assert.Equal(GenerateDiagnosticTable(), string.Join('\n', tableLines) + "\n");
        var corpusStart = document.IndexOf("| Source decision | AST equivalent / fix |", StringComparison.Ordinal);
        Assert.True(corpusStart >= 0, "The corpus lowering table is missing.");
        var corpusLines = document[corpusStart..].Split('\n').TakeWhile(line => line.StartsWith("|", StringComparison.Ordinal)).ToArray();
        Assert.Equal(GenerateDecisionTable(), string.Join('\n', corpusLines) + "\n");
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Every_versioned_spelling_has_its_recorded_public_decision(CorpusCase item)
    {
        var diagnostics = ExpressionLowerer.Diagnose(item.Expression, LinqFrontEndTests.Tickets);
        if (item.Decision.Code is not null)
        {
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal(item.Decision.Code, diagnostic.Code);
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
        var expectedSignature = shape.Decision.Code is null ? VariantSignature(shape.ExpectedSignature!, value) : null;
        return new CorpusCase(string.Format(CultureInfo.InvariantCulture, shape.Spelling, value) + VariantSuffix(value), value, shape.Decision, shape.ExpectedSpan, expectedSignature, expression);
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
        Predicate.Equal equal => $"Equal:{ColumnSignature(equal.Column)}:{ConstantSignature(equal.Value)}",
        Predicate.Range range => $"Range:{ColumnSignature(range.Column)}:L{BoundSignature(range.Lower)}:U{BoundSignature(range.Upper)}",
        Predicate.Not not => "Not(" + AstSignature(not.Inner) + ")",
        Predicate.And and => "And(" + string.Join(',', and.Terms.Select(AstSignature).OrderBy(value => value, StringComparer.Ordinal)) + ")",
        Predicate.Or or => "Or(" + string.Join(',', or.Terms.Select(AstSignature).OrderBy(value => value, StringComparer.Ordinal)) + ")",
        Predicate.In membership => $"In:{ColumnSignature(membership.Column)}:Count={membership.Values.Length}:Values={string.Join(',', membership.Values.Select(ConstantSignature).OrderBy(value => value, StringComparer.Ordinal))}",
        Predicate.ElementOf element => $"ElementOf:{element.Set.Name}:{element.Set.Type}:Quantifier={element.Quantifier}:Count={element.Values.Length}:Values={string.Join(',', element.Values.Select(ConstantSignature).OrderBy(value => value, StringComparer.Ordinal))}",
        Predicate.Substring substring => $"Substring:{ColumnSignature(substring.Column)}:Anchor={substring.Anchor}:Count=1:ValueType=String",
        Predicate.StartsWith startsWith => $"StartsWith:{ColumnSignature(startsWith.Column)}:Anchor=StartsWith:Count=1:ValueType=String",
        Predicate.ColumnCompare compare => $"ColumnCompare:{ColumnSignature(compare.Left)}:Op={compare.Op}:{ColumnSignature(compare.Right)}",
        Predicate.AlwaysTrue => "AlwaysTrue",
        Predicate.AlwaysFalse => "AlwaysFalse",
        _ => predicate.GetType().Name
    };

    private static string ColumnSignature(ColumnRef column) => $"{column.Table.Value}.{column.Name}:Type={column.Type}:Nullable={column.IsNullable}";
    private static string ConstantSignature(QueryConstant value) => $"Kind={value.Kind}:Type={value.Type}";
    private static string BoundSignature(Bound? bound) => bound is null ? "None" : $"{(bound.IsInclusive ? "Inclusive" : "Exclusive")}:{ConstantSignature(bound.Value)}";
    private static string Escape(string value) => value.Length + "#" + value.Replace("|", "||", StringComparison.Ordinal);

    private static string VariantSignature(string baseSignature, int variant)
    {
        if (variant == 0) return baseSignature;
        var extra = variant switch
        {
            1 => AstSignature(new Predicate.Equal(MarkerColumn, QueryConstant.Of(MarkerColumn, 100 + variant))),
            2 => AstSignature(new Predicate.Range(MarkerColumn, Bound.Exclusive(QueryConstant.Of(MarkerColumn, 200 + variant)), null)),
            3 => AstSignature(new Predicate.Range(MarkerColumn, null, Bound.Exclusive(QueryConstant.Of(MarkerColumn, 300 + variant)))),
            _ => AstSignature(new Predicate.Not(new Predicate.Equal(MarkerColumn, QueryConstant.Of(MarkerColumn, 400 + variant))))
        };
        var terms = baseSignature.StartsWith("And(", StringComparison.Ordinal)
            ? SplitTopLevel(baseSignature[4..^1]).Append(extra)
            : new[] { baseSignature, extra };
        return "And(" + string.Join(',', terms.OrderBy(value => value, StringComparer.Ordinal)) + ")";
    }

    private static readonly ColumnRef MarkerColumn = new(new TableId("tickets"), nameof(LinqFrontEndTests.Ticket.Marker), QueryType.Int32, false);

    private static IEnumerable<string> SplitTopLevel(string value)
    {
        var start = 0;
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            switch (value[index])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return value[start..index];
                    start = index + 1;
                    break;
            }
        }
        if (start < value.Length)
            yield return value[start..];
    }

    private static string SpanIdentity(Expression expression) => expression switch
    {
        MethodCallExpression call => "Call:" + call.Method.Name,
        MemberExpression member => "Member:" + member.Member.Name,
        BinaryExpression binary => "Binary:" + binary.NodeType,
        ConstantExpression constant => "Constant:" + constant.Type.Name,
        _ => expression.NodeType.ToString()
    };

    private static string GenerateDecisionTable()
    {
        var builder = new StringBuilder("| Source decision | AST equivalent / fix |\n| --- | --- |\n");
        foreach (var decision in Cases.Select(item => item.Decision).Distinct().OrderBy(item => item.Code is null ? "A-" + item.Title : "R-" + item.Code, StringComparer.Ordinal))
        {
            var text = decision.Code is null ? decision.AstEquivalent : $"{decision.Code}: {decision.FixText}";
            builder.Append("| ").Append(decision.Title).Append(" | ").Append(text).Append(" |\n");
        }
        return builder.ToString();
    }

    private static string GenerateDiagnosticTable()
    {
        var builder = new StringBuilder("| Code | AST equivalent / fix |\n| --- | --- |\n");
        foreach (var entry in Cases.Select(item => item.Decision).Where(item => item.Code is not null).Distinct().OrderBy(item => item.Code, StringComparer.Ordinal))
        {
            var fix = entry.FixText!;
            builder.Append("| ").Append(entry.Code).Append(" | ").Append(char.ToUpperInvariant(fix[0])).Append(fix[1..]).Append(". |\n");
        }
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

    private static CorpusShape Shape(string spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> build, Type expectedAst)
    {
        var signature = ExpectedSignatureForShape(spelling, expectedAst);
        return new(spelling, build, DecisionForSignature(signature), null, signature);
    }
    private static CorpusShape Shape(string spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> build, string expectedDiagnostic) => new(spelling, build, DecisionForCode(expectedDiagnostic), ExpectedSpanForDiagnostic(expectedDiagnostic), null);
    private static CorpusShape Shape(string spelling, Func<int, Expression<Func<LinqFrontEndTests.Ticket, bool>>> build, string expectedDiagnostic, string expectedSpan) => new(spelling, build, DecisionForCode(expectedDiagnostic), expectedSpan, null);
    private static string ExpectedSignatureForShape(string spelling, Type type)
    {
        var tenant = Column("TenantId", QueryType.Int32, false);
        var status = Column("Status", QueryType.String, true);
        var created = Column("CreatedAt", QueryType.DateTimeOffset, false);
        var optional = Column("OptionalAt", QueryType.DateTimeOffset, true);
        var marker = MarkerColumn;
        var equalTenant = new Predicate.Equal(tenant, QueryConstant.Of(tenant, 0));
        var equalMarker = new Predicate.Equal(marker, QueryConstant.Of(marker, 0));
        var equalStatus = new Predicate.Equal(status, QueryConstant.Of(status, ""));
        var equalOpen = new Predicate.Equal(Column("IsOpen", QueryType.Boolean, false), QueryConstant.Of(true));
        var rangeTenant = new Predicate.Range(tenant, Bound.Exclusive(QueryConstant.Of(tenant, 0)), null);
        var rangeCreated = new Predicate.Range(created, Bound.Inclusive(QueryConstant.Of(created, DateTimeOffset.UnixEpoch)), null);
        var rangeOptional = new Predicate.Range(optional, Bound.Inclusive(QueryConstant.Of(optional, DateTimeOffset.UnixEpoch)), null);
        if (spelling.StartsWith("ticket.TenantId ==", StringComparison.Ordinal)) return AstSignature(equalTenant);
        if (spelling.StartsWith("ticket.TenantId !=", StringComparison.Ordinal)) return AstSignature(new Predicate.Not(equalTenant));
        if (spelling.StartsWith("ticket.TenantId >=", StringComparison.Ordinal)) return AstSignature(new Predicate.Range(tenant, Bound.Inclusive(QueryConstant.Of(tenant, 0)), null));
        if (spelling.StartsWith("ticket.TenantId <=", StringComparison.Ordinal)) return AstSignature(new Predicate.Range(tenant, null, Bound.Inclusive(QueryConstant.Of(tenant, 0))));
        if (spelling.StartsWith("ticket.TenantId <", StringComparison.Ordinal)) return AstSignature(new Predicate.Range(tenant, null, Bound.Exclusive(QueryConstant.Of(tenant, 0))));
        if (spelling.StartsWith("ticket.TenantId >", StringComparison.Ordinal)) return AstSignature(rangeTenant);
        if (spelling == "ticket.IsOpen") return AstSignature(equalOpen);
        if (spelling == "!ticket.IsOpen") return AstSignature(new Predicate.Not(equalOpen));
        if (spelling == "ticket.Status == null") return AstSignature(new Predicate.Equal(status, QueryConstant.Of(status, null)));
        if (spelling == "ticket.Status != null") return AstSignature(new Predicate.Not(new Predicate.Equal(status, QueryConstant.Of(status, null))));
        if (spelling.StartsWith("ticket.Status ==", StringComparison.Ordinal)) return AstSignature(equalStatus);
        if (spelling.Contains(".Contains", StringComparison.Ordinal) && spelling.Contains("StringComparison.Ordinal", StringComparison.Ordinal)) return AstSignature(new Predicate.Substring(status, "", Anchor.Contains));
        if (spelling.Contains(".StartsWith", StringComparison.Ordinal)) return AstSignature(new Predicate.StartsWith(status, ""));
        if (spelling.Contains(".EndsWith", StringComparison.Ordinal)) return AstSignature(new Predicate.Substring(status, "", Anchor.EndsWith));
        if (spelling.StartsWith("new[]", StringComparison.Ordinal)) return AstSignature(new Predicate.In(tenant, new[] { QueryConstant.Of(tenant, 0), QueryConstant.Of(tenant, 1) }));
        if (spelling.StartsWith("ticket.Amount", StringComparison.Ordinal)) return AstSignature(new Predicate.Equal(Column("Amount", QueryType.Decimal, false), QueryConstant.Of(0m)));
        if (spelling.StartsWith("ticket.LongValue", StringComparison.Ordinal)) return AstSignature(new Predicate.Range(Column("LongValue", QueryType.Int64, false), Bound.Inclusive(QueryConstant.Of(0L)), null));
        if (spelling.StartsWith("ticket.CreatedAt >=", StringComparison.Ordinal)) return AstSignature(rangeCreated);
        if (spelling == "ticket.OptionalAt.HasValue") return AstSignature(new Predicate.Not(new Predicate.Equal(optional, QueryConstant.Of(optional, null))));
        if (spelling.StartsWith("ticket.OptionalAt!", StringComparison.Ordinal)) return AstSignature(rangeOptional);
        if (spelling.StartsWith("ticket.IsOpen &&", StringComparison.Ordinal)) return AstSignature(new Predicate.And(new Predicate[] { equalOpen, equalTenant }));
        if (spelling.StartsWith("ticket.IsOpen ||", StringComparison.Ordinal)) return AstSignature(new Predicate.Or(new Predicate[] { equalOpen, equalTenant }));
        if (spelling.StartsWith("ticket.TagIds.Any", StringComparison.Ordinal)) return AstSignature(new Predicate.ElementOf(new ElementSetRef("tag_ids", QueryType.Int32), new[] { QueryConstant.Of(0) }, SetQuantifier.Any));
        if (spelling.StartsWith("ticket.TagIds.All", StringComparison.Ordinal)) return AstSignature(new Predicate.ElementOf(new ElementSetRef("tag_ids", QueryType.Int32), new[] { QueryConstant.Of(0) }, SetQuantifier.All));
        if (spelling.StartsWith("ticket.CreatedAt.Year", StringComparison.Ordinal)) return AstSignature(DateRange(created));
        if (spelling.StartsWith("ticket.CreatedAt.Date", StringComparison.Ordinal)) return AstSignature(DateRange(created));
        if (spelling.StartsWith("ticket.IsOpen ==", StringComparison.Ordinal)) return AstSignature(equalOpen);
        throw new InvalidOperationException($"No exact corpus signature for {spelling} ({type.Name}).");
    }

    private static ColumnRef Column(string name, QueryType type, bool nullable) => new(new TableId("tickets"), name, type, nullable);
    private static Predicate.Range DateRange(ColumnRef column) => new(column, Bound.Inclusive(QueryConstant.Of(column, DateTimeOffset.UnixEpoch)), Bound.Exclusive(QueryConstant.Of(column, DateTimeOffset.UnixEpoch.AddDays(1))));
    private static CorpusDecision DecisionForCode(string code) => Decisions.Single(decision => string.Equals(decision.Code, code, StringComparison.Ordinal));
    private static CorpusDecision DecisionForSignature(string signature) => signature switch
    {
        var value when value.StartsWith("Equal:", StringComparison.Ordinal) => Decisions.Single(decision => decision.Title == "Equality"),
        var value when value.StartsWith("Not(Equal:", StringComparison.Ordinal) => Decisions.Single(decision => decision.Title == "Inequality"),
        var value when value.StartsWith("Range:", StringComparison.Ordinal) => Decisions.Single(decision => decision.Title == "Range"),
        var value when value.StartsWith("And(", StringComparison.Ordinal) => Decisions.Single(decision => decision.Title == "Conjunction"),
        var value when value.StartsWith("Or(", StringComparison.Ordinal) => Decisions.Single(decision => decision.Title == "Disjunction"),
        var value when value.StartsWith("In:", StringComparison.Ordinal) => Decisions.Single(decision => decision.Title == "Membership"),
        var value when value.StartsWith("ElementOf:", StringComparison.Ordinal) => Decisions.Single(decision => decision.Title == "Element-set quantifier"),
        var value when value.StartsWith("Substring:", StringComparison.Ordinal) => Decisions.Single(decision => decision.Title == "Substring matching"),
        var value when value.StartsWith("StartsWith:", StringComparison.Ordinal) => Decisions.Single(decision => decision.Title == "Prefix matching"),
        _ => throw new InvalidOperationException($"No corpus decision for AST {signature}.")
    };
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
