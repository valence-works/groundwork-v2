using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CodeActions;

namespace Groundwork.Analyzers;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(LinqCodeFixProvider))]
public sealed class LinqCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => ["GW_LINQ_108"];
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics.FirstOrDefault();
        if (diagnostic is null) return;
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(diagnostic.Location.SourceSpan) is not InvocationExpressionSyntax invocation || invocation.ArgumentList.Arguments.Count != 1) return;
        context.RegisterCodeFix(
            CodeAction.Create("Use an explicit ordinal comparison", cancellationToken =>
                Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(invocation, invocation.WithArgumentList(invocation.ArgumentList.AddArguments(
                    SyntaxFactory.Argument(SyntaxFactory.ParseExpression("global::System.StringComparison.Ordinal"))))))),
                "Groundwork_LINQ_108_Ordinal"), diagnostic);
    }
}
