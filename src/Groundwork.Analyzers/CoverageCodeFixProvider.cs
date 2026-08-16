using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Groundwork.Analyzers;

/// <summary>Offers the blessed WhereIf rewrite for the supported local-reassignment idiom.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(CoverageCodeFixProvider))]
public sealed class CoverageCodeFixProvider : CodeFixProvider
{
    public override ImmutableArray<string> FixableDiagnosticIds => [AnalyzerDiagnostics.UnresolvableId];

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
            return;
        var containingNode = root.FindNode(context.Span);
        var statement = containingNode.FirstAncestorOrSelf<IfStatementSyntax>() ??
                        containingNode.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>()?
                            .DescendantNodes().OfType<IfStatementSyntax>()
                            .FirstOrDefault(item => item.Else is null && TryRewrite(item, out _));
        if (statement is null || statement.Else is not null || !TryRewrite(statement, out var replacement))
            return;

        context.RegisterCodeFix(
            CodeAction.Create(
                "Rewrite conditional query as WhereIf",
                cancellationToken => Task.FromResult(context.Document.WithSyntaxRoot(root.ReplaceNode(statement, replacement))),
                nameof(CoverageCodeFixProvider)),
            context.Diagnostics);
    }

    private static bool TryRewrite(IfStatementSyntax statement, out StatementSyntax replacement)
    {
        replacement = null!;
        var assignment = statement.Statement.DescendantNodesAndSelf()
            .OfType<AssignmentExpressionSyntax>()
            .SingleOrDefault(item => item.Left is IdentifierNameSyntax);
        if (assignment is null ||
            assignment.Right is not InvocationExpressionSyntax whereInvocation ||
            whereInvocation.Expression is not MemberAccessExpressionSyntax whereMember ||
            whereMember.Name.Identifier.ValueText != "Where" ||
            whereInvocation.ArgumentList.Arguments.Count != 1 ||
            assignment.Left is not IdentifierNameSyntax)
            return false;

        var whereIfMember = whereMember.WithName(SyntaxFactory.IdentifierName("WhereIf"));
        var arguments = SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[]
        {
            SyntaxFactory.Argument(statement.Condition.WithoutTrivia()),
            whereInvocation.ArgumentList.Arguments[0]
        }));
        var rewrittenInvocation = whereInvocation
            .WithExpression(whereIfMember)
            .WithArgumentList(arguments);
        replacement = SyntaxFactory.ExpressionStatement(
                assignment.WithRight(rewrittenInvocation))
            .WithTriviaFrom(statement);
        return true;
    }
}
