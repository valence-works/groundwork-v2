using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Groundwork.Analyzers;

/// <summary>Compile-time defense for the closed Groundwork LINQ expression vocabulary.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LinqAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        AnalyzerIds.Select(AnalyzerDiagnostics.For).ToImmutableArray();

    private static readonly string[] AnalyzerIds =
    {
        "GW-LINQ-101", "GW-LINQ-102", "GW-LINQ-103", "GW-LINQ-104", "GW-LINQ-105",
        "GW-LINQ-106", "GW-LINQ-107", "GW-LINQ-108", "GW-LINQ-109", "GW-LINQ-110"
    };

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeLambda, SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression);
    }

    private static void AnalyzeLambda(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not LambdaExpressionSyntax lambda || lambda.Body is not ExpressionSyntax body)
            return;
        var visitor = new Visitor(context, lambda);
        visitor.Visit(body);
    }

    private sealed class Visitor : CSharpSyntaxWalker
    {
        private readonly SyntaxNodeAnalysisContext context;
        private readonly LambdaExpressionSyntax lambda;

        public Visitor(SyntaxNodeAnalysisContext context, LambdaExpressionSyntax lambda)
        {
            this.context = context; this.lambda = lambda;
        }

        public override void VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var symbol = context.SemanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            var name = symbol?.Name ?? node.Expression switch
            {
                MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                _ => string.Empty
            };
            if (name == "StartsWith" && node.ArgumentList.Arguments.Count == 1)
                Report("GW-LINQ-108", node, "GW-LINQ-108: bare StartsWith is not portable; use the overload with StringComparison.Ordinal/OrdinalIgnoreCase matching the column's folding");
            else if (name is "ToLower" or "ToUpper" or "Substring" or "Trim")
                Report("GW-LINQ-101", node, "GW-LINQ-101: declare a computed column; expressions over columns are not portable");
            else if (name is "Join" or "GroupJoin")
                Report("GW-LINQ-104", node, "GW-LINQ-104: v2 has no joins; use a declared element set or two queries");
            else if (name == "GroupBy")
                Report("GW-LINQ-105", node, "GW-LINQ-105: use `.LatestPer(...)` for grouped top-1");
            else if (name is "Any" or "All" && node.ArgumentList.Arguments.Count > 0 && node.ArgumentList.Arguments[node.ArgumentList.Arguments.Count - 1].Expression is LambdaExpressionSyntax nested && !IsEquality(nested.Body))
                Report("GW-LINQ-106", node, "GW-LINQ-106: declare the element set");
            else if (symbol is not null && symbol.ContainingType?.SpecialType == SpecialType.None && !IsKnownQueryMethod(symbol))
                Report("GW-LINQ-107", node, "GW-LINQ-107: opaque helpers are not portable; mark it `[GwQueryFragment]`");
            base.VisitInvocationExpression(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var symbol = context.SemanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is IPropertySymbol property && property.Name == "Length" && HasLambdaParameter(node.Expression))
                Report("GW-LINQ-101", node, "GW-LINQ-101: declare a computed column; expressions over columns are not portable");
            if (symbol is IPropertySymbol instant && instant.ContainingType?.ToDisplayString() == "System.DateTime" && instant.Name is "Now" or "Today")
                Report("GW-LINQ-109", node, "GW-LINQ-109: use `DateTimeOffset.UtcNow`");
            if (node.Name.Identifier.ValueText is not ("Value" or "Date" or "Year") &&
                node.Expression is MemberAccessExpressionSyntax nested && HasLambdaParameter(nested))
                Report("GW-LINQ-104", node, "GW-LINQ-104: v2 has no joins; use a declared element set or two queries");
            base.VisitMemberAccessExpression(node);
        }

        public override void VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.NumericLiteralExpression) && node.Token.Value is decimal)
            {
                var text = node.Token.Text.TrimEnd('m', 'M');
                var decimalPoint = text.IndexOf('.');
                if (decimalPoint >= 0 && text.Length - decimalPoint - 1 > 2)
                    Report("GW-LINQ-110", node, "GW-LINQ-110: the value has more scale/range than `decimal(10,2)`");
            }
            base.VisitLiteralExpression(node);
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.AddExpression) || node.IsKind(SyntaxKind.SubtractExpression) || node.IsKind(SyntaxKind.MultiplyExpression) || node.IsKind(SyntaxKind.DivideExpression))
            {
                if (HasLambdaParameter(node)) Report("GW-LINQ-102", node, "GW-LINQ-102: declare a computed column; expressions over columns are not portable");
            }
            else if (node.IsKind(SyntaxKind.EqualsExpression) || node.IsKind(SyntaxKind.NotEqualsExpression) || node.IsKind(SyntaxKind.LessThanExpression) || node.IsKind(SyntaxKind.GreaterThanExpression) || node.IsKind(SyntaxKind.LessThanOrEqualExpression) || node.IsKind(SyntaxKind.GreaterThanOrEqualExpression))
            {
                if (HasDistinctMemberOperands(node.Left, node.Right)) Report("GW-LINQ-103", node, "GW-LINQ-103: allowed, but never index-covered — add `.AcceptScan(...)`");
            }
            base.VisitBinaryExpression(node);
        }

        private bool HasLambdaParameter(SyntaxNode node)
        {
            var names = lambda switch
            {
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText),
                SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter.Identifier.ValueText },
                _ => Enumerable.Empty<string>()
            };
            return node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(identifier => names.Contains(identifier.Identifier.ValueText, StringComparer.Ordinal));
        }
        private bool HasDistinctMemberOperands(ExpressionSyntax left, ExpressionSyntax right) => left is MemberAccessExpressionSyntax && right is MemberAccessExpressionSyntax && HasLambdaParameter(left) && HasLambdaParameter(right);
        private static bool IsEquality(CSharpSyntaxNode node) => node is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.EqualsExpression);
        private static bool IsKnownQueryMethod(IMethodSymbol symbol) => symbol.ContainingType?.ToDisplayString().StartsWith("System.Linq.Enumerable", StringComparison.Ordinal) == true;
        private void Report(string code, SyntaxNode node, string message) => context.ReportDiagnostic(Diagnostic.Create(AnalyzerDiagnostics.For(code), node.GetLocation(), message));
    }
}
