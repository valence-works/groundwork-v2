using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Globalization;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

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
            AnalyzerDiagnostics.For("GW-AGG-ADHOC-905"),
            AnalyzerDiagnostics.For("GW-AGG-ADHOC-906")
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
            var now = clock();
            var aggregationNow = NormalizeDate(now);
            start.RegisterSyntaxNodeAction(
                action => AnalyzeInvocation(action, schema, acceptedScansEnabled, now),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
            start.RegisterSyntaxNodeAction(
                action => AnalyzeAggregationAcceptance(
                    action, acceptedAggregationsEnabled, aggregationInventory, aggregationNow),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
        });
    }

    private void AnalyzeAggregationAcceptance(
        SyntaxNodeAnalysisContext context,
        bool acceptedAggregationsEnabled,
        ConcurrentDictionary<string, byte> inventory,
        DateTimeOffset now)
    {
        if (context.Node is not InvocationExpressionSyntax invocation ||
            context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
            method.Name != "Allow" ||
            method.ContainingType?.ToDisplayString() != "Groundwork.Kernel.AggregationAcceptance")
            return;

        var location = invocation.GetLocation();
        var id = ConstantString(context.SemanticModel, invocation, 0, "id", out var idResolved);
        if (!acceptedAggregationsEnabled)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDiagnostics.For("GW-AGG-ADHOC-902"),
                location,
                "GW-AGG-ADHOC-902: AggregationAcceptance '" + (id ?? "<unresolved>") +
                "' requires [assembly: GwAllowAcceptedAggregations]."));
        }

        var reason = ConstantString(context.SemanticModel, invocation, 1, "reason", out var reasonResolved);
        var owner = ConstantString(context.SemanticModel, invocation, 2, "owner", out var ownerResolved);
        var expiry = TryGetDate(context.SemanticModel, invocation, 3, "expiresOn", out var expiryResolved);
        var groups = ConstantInt(context.SemanticModel, invocation, 4, "maxGroups", out var groupsResolved);
        var inputRows = ConstantInt(context.SemanticModel, invocation, 5, "maxInputRows", out var inputRowsResolved);
        if (!idResolved || !reasonResolved || !ownerResolved || !expiryResolved || !groupsResolved || !inputRowsResolved)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDiagnostics.For("GW-AGG-ADHOC-906"),
                location,
                "GW-AGG-ADHOC-906: AggregationAcceptance metadata must be statically resolvable for inventory; " +
                "use constants for id, reason, owner, expiresOn, maxGroups, and maxInputRows."));
            return;
        }

        if (!inventory.TryAdd(id!, 0))
            return;
        if (expiry is DateTimeOffset expiresOn)
        {
            if (now >= expiresOn)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    AnalyzerDiagnostics.For("GW-AGG-ADHOC-903"),
                    location,
                    "GW-AGG-ADHOC-903: accepted aggregation '" + id + "' expired on " +
                    expiresOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "."));
            }
            else if (expiresOn - now <= TimeSpan.FromDays(30))
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
            context.ReportDiagnostic(Diagnostic.Create(
                AnalyzerDiagnostics.For("GW-AGG-ADHOC-905"),
                location,
                "GW-AGG-ADHOC-905: accepted aggregation '" + id + "' reason='" + reason +
                "' owner='" + owner + "' expiresOn='" +
                expiry!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                "' maxGroups='" + groups!.Value.ToString(CultureInfo.InvariantCulture) +
                "' maxInputRows='" + inputRows!.Value.ToString(CultureInfo.InvariantCulture) + "'."));
        }
    }

    private static string? ConstantString(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        int index,
        string name,
        out bool resolved)
    {
        var expression = ArgumentExpression(semanticModel, invocation, index, name);
        var constant = expression is null ? default : semanticModel.GetConstantValue(expression);
        resolved = constant.HasValue && constant.Value is string;
        return resolved ? (string)constant.Value! : null;
    }

    private static int? ConstantInt(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        int index,
        string name,
        out bool resolved)
    {
        var expression = ArgumentExpression(semanticModel, invocation, index, name);
        var constant = expression is null ? default : semanticModel.GetConstantValue(expression);
        resolved = constant.HasValue && constant.Value is int;
        return resolved ? (int)constant.Value! : null;
    }

    private static DateTimeOffset? TryGetDate(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        int index,
        string name,
        out bool resolved)
    {
        resolved = false;
        if (ArgumentExpression(semanticModel, invocation, index, name) is not ObjectCreationExpressionSyntax creation ||
            semanticModel.GetOperation(creation) is not IObjectCreationOperation operation ||
            operation.Constructor is not IMethodSymbol constructor ||
            constructor.ContainingType.ToDisplayString() != "System.DateTimeOffset")
            return null;
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in operation.Arguments)
        {
            var parameter = argument.Parameter;
            var constant = semanticModel.GetConstantValue(argument.Value.Syntax);
            if (parameter is not null && constant.HasValue && constant.Value is int value)
                values[parameter.Name] = value;
        }
        if (!values.TryGetValue("year", out var year) ||
            !values.TryGetValue("month", out var month) ||
            !values.TryGetValue("day", out var day))
            return null;
        try
        {
            resolved = true;
            return new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ExpressionSyntax? ArgumentExpression(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        int index,
        string name)
    {
        if (semanticModel.GetOperation(invocation) is not IInvocationOperation operation)
            return null;

        foreach (var argument in operation.Arguments)
        {
            var parameter = argument.Parameter;
            if (parameter is not null &&
                parameter.Ordinal == index &&
                string.Equals(parameter.Name, name, StringComparison.Ordinal))
                return argument.Value.Syntax as ExpressionSyntax;
        }

        return null;
    }

    private static DateTimeOffset NormalizeDate(DateTimeOffset value) =>
        new(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero);

    private void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        AnalyzerSchema schema,
        bool acceptedScansEnabled,
        DateTimeOffset now)
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
