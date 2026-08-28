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
        "GW-LINQ-106", "GW-LINQ-107", "GW-LINQ-108", "GW-LINQ-109", "GW-LINQ-110", "GW-LINQ-111", "GW-LINQ-112", "GW-LINQ-113"
    };

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeLambda, SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeLambda(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not LambdaExpressionSyntax lambda || lambda.Body is not ExpressionSyntax body)
            return;
        if (!IsClosedSurfaceLambda(context, lambda))
            return;
        var visitor = new Visitor(context, lambda);
        visitor.Visit(body);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not InvocationExpressionSyntax invocation) return;
        var method = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (!IsGroundworkWhereMethod(method)) return;
        var argumentIndex = method!.Name == "WhereIf" ? 1 : 0;
        if (invocation.ArgumentList.Arguments.Count <= argumentIndex) return;
        var argument = invocation.ArgumentList.Arguments[argumentIndex].Expression;
        var symbol = context.SemanticModel.GetSymbolInfo(argument).Symbol;
        if (symbol is not IPropertySymbol property) return;
        if (!property.IsStatic || !IsValidFragmentProperty(property) || !HasFragmentAttribute(property))
            context.ReportDiagnostic(Diagnostic.Create(AnalyzerDiagnostics.For("GW-LINQ-107"), argument.GetLocation(), "GW-LINQ-107: a query fragment must be an attributed Expression<Func<T, bool>> property"));
    }

    private static bool IsClosedSurfaceLambda(SyntaxNodeAnalysisContext context, LambdaExpressionSyntax lambda)
    {
        foreach (var invocation in lambda.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            if (!invocation.ArgumentList.Arguments.Any(argument => argument.Expression == lambda))
                continue;
            var method = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (IsClosedSurfaceMethod(method))
                return true;
        }

        foreach (var declaration in lambda.Ancestors().OfType<MemberDeclarationSyntax>())
        {
            var symbol = context.SemanticModel.GetDeclaredSymbol(declaration);
            if (symbol is not null && HasFragmentAttribute(symbol)) return true;
        }
        return false;
    }

    private static bool HasFragmentAttribute(ISymbol symbol) => symbol.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "Groundwork.Query.Linq.GwQueryFragmentAttribute" &&
        attribute.AttributeClass.ContainingAssembly.Identity.Name == "Groundwork.Query.Linq");

    private static bool IsClosedSurfaceMethod(IMethodSymbol? method) => method is not null &&
        method.ContainingAssembly.Identity.Name == "Groundwork.Query.Linq" &&
        method.ContainingType?.ToDisplayString().StartsWith("Groundwork.Query.Linq.", StringComparison.Ordinal) == true &&
        method.Name is "Where" or "WhereIf" or "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending" or "Select" or "LatestPer";

    private static bool IsGroundworkWhereMethod(IMethodSymbol? method) => method is not null &&
        method.ContainingAssembly.Identity.Name == "Groundwork.Query.Linq" &&
        method.Name is "Where" or "WhereIf" &&
        method.ContainingType?.OriginalDefinition.ContainingNamespace.ToDisplayString() == "Groundwork.Query.Linq" &&
        method.ContainingType.OriginalDefinition.Name is "GwQueryTable" or "IGwQueryable" or "GwQueryable";

    private static bool IsValidFragmentProperty(IPropertySymbol property)
    {
        if (property.Type is not INamedTypeSymbol expression || expression.Name != "Expression" || expression.TypeArguments.Length != 1) return false;
        return expression.TypeArguments[0] is INamedTypeSymbol func && func.Name == "Func" && func.TypeArguments.Length == 2 && func.TypeArguments[1].SpecialType == SpecialType.System_Boolean;
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
            if (name is "StartsWith" or "Contains" or "EndsWith" && IsStringInvocation(node) && node.ArgumentList.Arguments.Count == 1)
                ReportStringComparison(node, "bare string matching is not portable; use the overload with StringComparison.Ordinal/OrdinalIgnoreCase matching the column's folding");
            else if (name is "StartsWith" or "Contains" or "EndsWith" && IsStringInvocation(node) && node.ArgumentList.Arguments.Count == 2)
            {
                var declared = DeclaredStringComparison(node);
                var supplied = context.SemanticModel.GetConstantValue(node.ArgumentList.Arguments[1].Expression).Value?.ToString();
                if (declared is not null && (declared == "Unsupported" || supplied is null || !string.Equals(declared, supplied, StringComparison.Ordinal)))
                    Report("GW-LINQ-108", node, $"GW-LINQ-108: string matching must agree with the column's declared folding; use StringComparison.{declared}");
            }
            else if (name is "ToLower" or "ToUpper" or "Substring" or "Trim")
                Report("GW-LINQ-101", node, "GW-LINQ-101: declare a computed column; expressions over columns are not portable");
            else if (name is "Join" or "GroupJoin")
                Report("GW-LINQ-104", node, "GW-LINQ-104: v2 has no joins; use a declared element set or two queries");
            else if (name == "GroupBy")
                Report("GW-LINQ-105", node, "GW-LINQ-105: use `.LatestPer(...)` for grouped top-1");
            else if (IsEnumerableAnyAll(symbol, node) && node.ArgumentList.Arguments[node.ArgumentList.Arguments.Count - 1].Expression is LambdaExpressionSyntax nested && !IsSupportedElementEquality(nested, node))
                Report("GW-LINQ-106", node, "GW-LINQ-106: declare the element set");
            else if (name is "AddDays" or "AddHours" or "AddMinutes" or "AddSeconds" && symbol?.ContainingType?.ToDisplayString() == "System.DateTimeOffset")
            {
                // Approved BCL instant arithmetic remains a closed term.
            }
            else if (symbol is not null && symbol.ContainingType?.SpecialType == SpecialType.None && !HasFragmentAttribute(symbol) && !IsKnownQueryMethod(symbol, node))
                Report("GW-LINQ-107", node, "GW-LINQ-107: opaque helpers are not portable; mark it `[GwQueryFragment]`");
            base.VisitInvocationExpression(node);
        }

        public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
        {
            var symbol = context.SemanticModel.GetSymbolInfo(node).Symbol;
            if (symbol is IPropertySymbol property && property.Name == "Length" && HasLambdaParameter(node.Expression))
                Report("GW-LINQ-101", node, "GW-LINQ-101: declare a computed column; expressions over columns are not portable");
            if (symbol is IPropertySymbol instant &&
                ((instant.ContainingType?.ToDisplayString() == "System.DateTime" && instant.Name is "Now" or "Today") ||
                 (instant.ContainingType?.ToDisplayString() == "System.DateTimeOffset" && instant.Name == "Now")))
                Report("GW-LINQ-109", node, "GW-LINQ-109: use `DateTimeOffset.UtcNow`");
            if (symbol is IPropertySymbol && node.Name.Identifier.ValueText is not ("Value" or "Date" or "Year") &&
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
        private bool IsStringInvocation(InvocationExpressionSyntax node) => node.Expression is MemberAccessExpressionSyntax member && context.SemanticModel.GetTypeInfo(member.Expression).Type?.SpecialType == SpecialType.System_String;
        private bool IsKnownQueryMethod(IMethodSymbol symbol, InvocationExpressionSyntax node)
        {
            if (symbol.Name == "Contains")
            {
                if (symbol.ReturnType.SpecialType != SpecialType.System_Boolean || node.Expression is not MemberAccessExpressionSyntax member)
                    return false;
                var original = symbol.ReducedFrom ?? symbol;
                if (IsTrustedFrameworkType(original.ContainingType, "System.Linq.Enumerable"))
                {
                    var reduced = symbol.ReducedFrom is not null;
                    var expectedArguments = reduced ? 1 : 2;
                    if (original.Parameters.Length != 2 || node.ArgumentList.Arguments.Count != expectedArguments ||
                        (!reduced && !symbol.IsStatic)) return false;
                    return HasSupportedMembershipOperands(
                        reduced ? member.Expression : node.ArgumentList.Arguments[0].Expression,
                        node.ArgumentList.Arguments[node.ArgumentList.Arguments.Count - 1].Expression);
                }
                if (IsTrustedFrameworkType(original.ContainingType, "System.MemoryExtensions"))
                {
                    var reduced = symbol.ReducedFrom is not null;
                    if (original.Parameters.Length != 2 ||
                        !original.Parameters[0].Type.ToDisplayString().StartsWith("System.ReadOnlySpan<", StringComparison.Ordinal) ||
                        node.ArgumentList.Arguments.Count != (reduced ? 1 : 2) || (!reduced && !symbol.IsStatic)) return false;
                    return HasSupportedMembershipOperands(
                        reduced ? member.Expression : node.ArgumentList.Arguments[0].Expression,
                        node.ArgumentList.Arguments[node.ArgumentList.Arguments.Count - 1].Expression);
                }
                return symbol.ReducedFrom is null && !symbol.IsStatic && symbol.Parameters.Length == 1 &&
                    node.ArgumentList.Arguments.Count == 1 && IsBclCollectionType(symbol.ContainingType) &&
                    (HasSupportedMembershipOperands(member.Expression, node.ArgumentList.Arguments[0].Expression) ||
                     HasSupportedElementMembershipOperands(member.Expression, node.ArgumentList.Arguments[0].Expression));
            }
            if (!IsEnumerableAnyAll(symbol, node)) return false;
            var elementCollection = symbol.ReducedFrom is null ? node.ArgumentList.Arguments[0].Expression : (node.Expression as MemberAccessExpressionSyntax)?.Expression;
            var lambda = node.ArgumentList.Arguments[node.ArgumentList.Arguments.Count - 1].Expression as LambdaExpressionSyntax;
            return elementCollection is MemberAccessExpressionSyntax && lambda is not null && IsSupportedElementEquality(lambda, node);
        }

        private bool IsSupportedElementEquality(LambdaExpressionSyntax nested, InvocationExpressionSyntax invocation)
        {
            if (nested.Body is not BinaryExpressionSyntax equality || !equality.IsKind(SyntaxKind.EqualsExpression)) return false;
            var parameter = nested switch
            {
                SimpleLambdaExpressionSyntax simple => context.SemanticModel.GetDeclaredSymbol(simple.Parameter),
                ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 1 } parenthesized => context.SemanticModel.GetDeclaredSymbol(parenthesized.ParameterList.Parameters[0]),
                _ => null
            };
            if (parameter is null) return false;
            var leftIsElement = IsParameterOperand(equality.Left, parameter);
            var rightIsElement = IsParameterOperand(equality.Right, parameter);
            if (leftIsElement == rightIsElement) return false;
            var value = leftIsElement ? equality.Right : equality.Left;
            return !ReferencesParameter(value, parameter) && !ReferencesOuterParameter(nested.Body, invocation);
        }

        private bool IsParameterOperand(ExpressionSyntax operand, IParameterSymbol parameter)
        {
            while (operand is ParenthesizedExpressionSyntax parenthesized) operand = parenthesized.Expression;
            return operand is IdentifierNameSyntax identifier && SymbolEqualityComparer.Default.Equals(context.SemanticModel.GetSymbolInfo(identifier).Symbol, parameter);
        }

        private bool ReferencesParameter(SyntaxNode source, IParameterSymbol parameter) => source.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Any(identifier => SymbolEqualityComparer.Default.Equals(context.SemanticModel.GetSymbolInfo(identifier).Symbol, parameter));

        private bool HasSupportedMembershipOperands(ExpressionSyntax collection, ExpressionSyntax item) =>
            !HasLambdaParameter(collection) && IsDirectOuterColumn(item);

        private bool HasSupportedElementMembershipOperands(ExpressionSyntax collection, ExpressionSyntax item) =>
            IsDirectOuterColumn(collection) && !HasLambdaParameter(item);

        private bool IsDirectOuterColumn(ExpressionSyntax source)
        {
            while (source is ParenthesizedExpressionSyntax parenthesized) source = parenthesized.Expression;
            if (source is not MemberAccessExpressionSyntax member) return false;
            if (member.Name.Identifier.ValueText == "Value" && member.Expression is MemberAccessExpressionSyntax nullable)
                member = nullable;
            if (member.Expression is not IdentifierNameSyntax receiver) return false;
            var outerParameters = lambda switch
            {
                SimpleLambdaExpressionSyntax simple => new[] { context.SemanticModel.GetDeclaredSymbol(simple.Parameter) },
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Select(parameter => context.SemanticModel.GetDeclaredSymbol(parameter)),
                _ => Enumerable.Empty<IParameterSymbol?>()
            };
            var symbol = context.SemanticModel.GetSymbolInfo(receiver).Symbol;
            return outerParameters.Any(parameter => parameter is not null && SymbolEqualityComparer.Default.Equals(symbol, parameter));
        }

        private bool IsEnumerableAnyAll(IMethodSymbol? symbol, InvocationExpressionSyntax node) => symbol is not null &&
            IsTrustedFrameworkType((symbol.ReducedFrom ?? symbol).ContainingType, "System.Linq.Enumerable") &&
            symbol.Name is "Any" or "All" && (symbol.ReducedFrom ?? symbol).Parameters.Length == 2 &&
            node.ArgumentList.Arguments.Count == (symbol.ReducedFrom is null ? 2 : 1) &&
            node.ArgumentList.Arguments[node.ArgumentList.Arguments.Count - 1].Expression is LambdaExpressionSyntax;
        private bool IsBclCollectionType(INamedTypeSymbol? type)
        {
            var trustedCollection = context.Compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
            return type is not null && trustedCollection is not null &&
                SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, trustedCollection.ContainingAssembly) &&
                type.ContainingNamespace.ToDisplayString() == "System.Collections.Generic";
        }
        private bool IsTrustedFrameworkType(INamedTypeSymbol? type, string metadataName)
        {
            var trustedType = context.Compilation.GetTypeByMetadataName(metadataName);
            return type is not null && trustedType is not null &&
                SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, trustedType.OriginalDefinition);
        }
        private bool ReferencesOuterParameter(SyntaxNode body, InvocationExpressionSyntax? invocation = null)
        {
            var outerNames = lambda switch
            {
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText),
                SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter.Identifier.ValueText },
                _ => Enumerable.Empty<string>()
            };
            var nestedNames = invocation?.ArgumentList.Arguments
                .Select(argument => argument.Expression)
                .OfType<LambdaExpressionSyntax>()
                .SelectMany(nested => nested switch
                {
                    ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText),
                    SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter.Identifier.ValueText },
                    _ => Enumerable.Empty<string>()
                }) ?? Enumerable.Empty<string>();
            return body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(identifier =>
                outerNames.Contains(identifier.Identifier.ValueText, StringComparer.Ordinal) &&
                !nestedNames.Contains(identifier.Identifier.ValueText, StringComparer.Ordinal));
        }
        private void ReportStringComparison(InvocationExpressionSyntax node, string message)
        {
            var declared = DeclaredStringComparison(node);
            if (declared is null)
            {
                Report("GW-LINQ-108", node, "GW-LINQ-108: " + message);
                return;
            }
            var diagnostic = Diagnostic.Create(AnalyzerDiagnostics.For("GW-LINQ-108"), node.GetLocation(),
                properties: ImmutableDictionary<string, string?>.Empty.Add("gwComparison", declared),
                messageArgs: new object?[] { "GW-LINQ-108: " + message });
            context.ReportDiagnostic(diagnostic);
        }
        private string? DeclaredStringComparison(InvocationExpressionSyntax node)
        {
            if (node.Expression is not MemberAccessExpressionSyntax member) return null;
            var symbol = context.SemanticModel.GetSymbolInfo(member.Expression).Symbol;
            var declaredMember = symbol as ISymbol;
            var attribute = declaredMember?.GetAttributes().FirstOrDefault(item => item.AttributeClass?.Name is "GwStringComparisonAttribute" or "GwStringComparison");
            if (attribute is null) return null;
            return attribute.ConstructorArguments.FirstOrDefault().Value is int value
                ? value switch { 4 => "Ordinal", 5 => "OrdinalIgnoreCase", _ => "Unsupported" }
                : "Unsupported";
        }
        private void Report(string code, SyntaxNode node, string message) => context.ReportDiagnostic(Diagnostic.Create(AnalyzerDiagnostics.For(code), node.GetLocation(), message));
    }
}
