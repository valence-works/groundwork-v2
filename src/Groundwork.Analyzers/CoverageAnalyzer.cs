using System.Collections.Immutable;
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
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [
            AnalyzerDiagnostics.Unresolvable,
            AnalyzerDiagnostics.For("GW-COVER-005"),
            AnalyzerDiagnostics.For("GW-COVER-006"),
            AnalyzerDiagnostics.For("GW-COVER-009"),
            AnalyzerDiagnostics.For("GW-COVER-016")
        ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var schema = AnalyzerSchema.Read(start.Compilation, start.Options);
            start.RegisterSyntaxNodeAction(
                action => AnalyzeInvocation(action, schema),
                Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
        });
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context, AnalyzerSchema schema)
    {
        if (context.Node is not InvocationExpressionSyntax invocation)
            return;
        var member = invocation.Expression as MemberAccessExpressionSyntax;
        if (member is null || !IsTerminal(member.Name.Identifier.ValueText))
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

        for (var index = 0; index < resolution.Requests.Length; index++)
        {
            var result = QueryCoverageChecker.Check(resolution.Requests[index], table.Indexes);
            if (result.IsCovered)
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

    private static bool IsTerminal(string name) => name is
        "QueryAsync" or "CountAsync" or "FirstOrDefaultAsync" or "ToListAsync";
}
