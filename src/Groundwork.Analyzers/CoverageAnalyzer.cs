using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Globalization;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Groundwork.Analyzers;

/// <summary>
/// Resolves the closed query surface and delegates every coverage decision to the Q3 checker.
/// Unresolvable sites remain an error by default because the G1 gate made runtime enforcement the
/// safety net; projects may downgrade GW-COVER-900 with normal .editorconfig severity settings.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CoverageAnalyzer : DiagnosticAnalyzer
{
    private readonly Func<DateTimeOffset> clock;

    public CoverageAnalyzer()
        : this(static () => DateTimeOffset.UtcNow)
    {
    }

    internal CoverageAnalyzer(Func<DateTimeOffset> clock)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [
            AnalyzerDiagnostics.Unresolvable,
            AnalyzerDiagnostics.For("GW-COVER-005"),
            AnalyzerDiagnostics.For("GW-COVER-006"),
            AnalyzerDiagnostics.For("GW-COVER-009"),
            AnalyzerDiagnostics.For("GW-COVER-016"),
            AnalyzerDiagnostics.For("GW-COVER-901"),
            AnalyzerDiagnostics.For("GW-COVER-902"),
            AnalyzerDiagnostics.For("GW-COVER-903"),
            AnalyzerDiagnostics.For("GW-COVER-904"),
            AnalyzerDiagnostics.For("GW-COVER-905"),
            AnalyzerDiagnostics.For("GW-AGG-ADHOC-902"),
            AnalyzerDiagnostics.For("GW-AGG-ADHOC-903"),
            AnalyzerDiagnostics.For("GW-AGG-ADHOC-904"),
            AnalyzerDiagnostics.For("GW-AGG-ADHOC-905")
        ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var schema = AnalyzerSchema.Read(start.Compilation, start.Options);
            var acceptedScansEnabled = HasAcceptedScanOptIn(start.Compilation);
            var acceptedAggregationsEnabled = HasAcceptedAggregationOptIn(start.Compilation);
            var aggregationInventory = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            start.RegisterSyntaxNodeAction(
                action => AnalyzeInvocation(action, schema, acceptedScansEnabled),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
            start.RegisterSyntaxNodeAction(
                action => AnalyzeAggregationAcceptance(
                    action, acceptedAggregationsEnabled, aggregationInventory),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
        });
    }

    private void AnalyzeAggregationAcceptance(
        SyntaxNodeAnalysisContext context,
        bool acceptedAggregationsEnabled,
        ConcurrentDictionary<string, byte> inventory)
    {
        if (context.Node is not InvocationExpressionSyntax invocation ||
            context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
            method.Name != "Allow" ||
            method.ContainingType?.ToDisplayString() != "Groundwork.Kernel.AggregationAcceptance")
            return;

        var location = invocation.GetLocation();
        var arguments = invocation.ArgumentList.Arguments;
        var id = ConstantString(arguments, 0, "id");
        if (id is null)
            return;
        if (!acceptedAggregationsEnabled)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDiagnostics.For("GW-AGG-ADHOC-902"),
                location,
                "GW-AGG-ADHOC-902: AggregationAcceptance '" + id +
                "' requires [assembly: GwAllowAcceptedAggregations]."));
        }

        if (!inventory.TryAdd(id, 0))
            return;
        var reason = ConstantString(arguments, 1, "reason") ?? "<runtime>";
        var owner = ConstantString(arguments, 2, "owner") ?? "<runtime>";
        var expiry = TryGetDate(arguments, 3, "expiresOn");
        if (expiry is DateTimeOffset expiresOn)
        {
            if (NormalizeDate(clock()) >= expiresOn)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    AnalyzerDiagnostics.For("GW-AGG-ADHOC-903"),
                    location,
                    "GW-AGG-ADHOC-903: accepted aggregation '" + id + "' expired on " +
                    expiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "."));
            }
            else if (expiresOn - NormalizeDate(clock()) <= TimeSpan.FromDays(30))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    AnalyzerDiagnostics.For("GW-AGG-ADHOC-904"),
                    location,
                    "GW-AGG-ADHOC-904: accepted aggregation '" + id + "' expires on " +
                    expiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +  "."));
            }
        }

        if (acceptedAggregationsEnabled)
        {
            var groups = ConstantInt(arguments, 4, "maxGroups");
            var inputRows = ConstantInt(arguments, 5, "maxInputRows");
            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDiagnostics.For("GW-AGG-ADHOC-905"),
                location,
                "GW-AGG-ADHOC-905: accepted aggregation '" + id + "' reason='" + reason +
                "' owner='" + owner + "' expiresOn='" +
                (expiry?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "<runtime>") +
                "' maxGroups='" + (groups?.ToString(CultureInfo.InvariantCulture) ?? "<runtime>") +
                "' maxInputRows='" + (inputRows?.ToString(CultureInfo.InvariantCulture) ?? "<runtime>") + "'."));
        }
    }

    private static string? ConstantString(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        int index,
        string name) => ArgumentExpression(arguments, index, name) is LiteralExpressionSyntax literal &&
            literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;

    private static int? ConstantInt(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        int index,
        string name)
    {
        if (ArgumentExpression(arguments, index, name) is not LiteralExpressionSyntax literal ||
            !literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression))
            return null;
        return literal.Token.Value is int value ? value : null;
    }

    private static DateTimeOffset? TryGetDate(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        int index,
        string name)
    {
        if (ArgumentExpression(arguments, index, name) is not ObjectCreationExpressionSyntax creation ||
            creation.ArgumentList is not { Arguments.Count: >= 3 } argumentList)
            return null;
        var values = argumentList.Arguments.Take(3)
            .Select(argument => argument.Expression is LiteralExpressionSyntax literal &&
                                literal.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NumericLiteralExpression) &&
                                literal.Token.Value is int value ? (int?)value : null)
            .ToArray();
        if (values.Any(value => value is null))
            return null;
        try
        {
            return new DateTimeOffset(values[0]!.Value, values[1]!.Value, values[2]!.Value, 0, 0, 0, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ExpressionSyntax? ArgumentExpression(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        int index,
        string name)
    {
        var named = arguments.FirstOrDefault(argument =>
            string.Equals(argument.NameColon?.Name.Identifier.ValueText, name, StringComparison.Ordinal));
        return named?.Expression ?? (arguments.Count > index ? arguments[index].Expression : null);
    }

    private static DateTimeOffset NormalizeDate(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero);

    private void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        AnalyzerSchema schema,
        bool acceptedScansEnabled)
    {
        if (context.Node is not InvocationExpressionSyntax invocation)
            return;
        var member = invocation.Expression as MemberAccessExpressionSyntax;
        if (member is null || !IsTerminal(member.Name.Identifier.ValueText))
            return;
        if (!QueryResolver.IsClosedSurfaceCandidate(invocation, context.SemanticModel, context.CancellationToken))
            return;

        var resolution = QueryResolver.Resolve(invocation, context.SemanticModel, schema, context.CancellationToken);
        var location = (resolution.FixNode ?? resolution.DiagnosticNode).GetLocation();
        if (!resolution.IsResolved)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDiagnostics.Unresolvable,
                location,
                AnalyzerDiagnostics.UnresolvableCode + ": " + resolution.Failure + ". Rewrite conditional reassignment to WhereIf where possible; otherwise rely on runtime coverage enforcement."));
            return;
        }

        if (!schema.Tables.TryGetValue(resolution.Requests[0].Table.Value, out var table))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDiagnostics.Unresolvable,
                location,
                AnalyzerDiagnostics.UnresolvableCode + ": the query table has no visible schema metadata."));
            return;
        }

        var inventory = new HashSet<string>(StringComparer.Ordinal);
        var now = clock();
        for (var index = 0; index < resolution.Requests.Length; index++)
        {
            var request = resolution.Requests[index];
            var acceptance = request.AcceptedScan;
            if (acceptance?.Allowed == true)
            {
                var id = acceptance.Id!;
                if (!acceptedScansEnabled)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        AnalyzerDiagnostics.For("GW-COVER-902"),
                        location,
                        "GW-COVER-902: AcceptScan '" + id + "' requires [assembly: GwAllowAcceptedScans]."));
                }
                if (inventory.Add(id))
                {
                    if (acceptance.IsExpiredAt(now))
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            AnalyzerDiagnostics.For("GW-COVER-903"),
                            location,
                            "GW-COVER-903: accepted scan '" + id + "' expired on " +
                            acceptance.ExpiresOn!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "."));
                    }
                    else
                    {
                        if (acceptance.IsExpiringAt(now))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(
                                AnalyzerDiagnostics.For("GW-COVER-904"),
                                location,
                                "GW-COVER-904: accepted scan '" + id + "' expires on " +
                                acceptance.ExpiresOn!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "."));
                        }
                    }
                    if (acceptedScansEnabled)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(
                            AnalyzerDiagnostics.For("GW-COVER-905"),
                            location,
                            "GW-COVER-905: accepted scan '" + id + "' reason='" + acceptance.Reason +
                            "' owner='" + acceptance.Owner + "' expiresOn='" +
                            acceptance.ExpiresOn!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + "'."));
                    }
                }
            }

            var result = QueryCoverageChecker.Check(request, table.Indexes);
            if (result.Refusal?.Code == "GW-COVER-901")
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    AnalyzerDiagnostics.For("GW-COVER-901"),
                    location,
                    result.Refusal.Message));
                continue;
            }
            if (result.IsCovered)
                continue;

            if (acceptance?.Allowed == true)
                continue;

            var refusal = result.Refusal;
            var shape = resolution.Requests.Length > 1
                ? $"Shape {index + 1} of {resolution.Requests.Length}" + (index == 0 ? " (all filters absent)" : "") + ": "
                : string.Empty;
            var message = shape + (refusal?.Message ?? result.Reason);
            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDiagnostics.For(refusal?.Code ?? "GW-COVER-006"),
                location,
                message));
        }
    }

    private static bool HasAcceptedScanOptIn(Compilation compilation) =>
        compilation.Assembly.GetAttributes().Any(attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "Groundwork.Query.Model.GwAllowAcceptedScansAttribute",
                StringComparison.Ordinal));

    private static bool HasAcceptedAggregationOptIn(Compilation compilation) =>
        compilation.Assembly.GetAttributes().Any(attribute =>
            string.Equals(
                attribute.AttributeClass?.ToDisplayString(),
                "Groundwork.Kernel.GwAllowAcceptedAggregationsAttribute",
                StringComparison.Ordinal));

    private static bool IsTerminal(string name) => QueryResolver.TerminalNames.Contains(name);
}
