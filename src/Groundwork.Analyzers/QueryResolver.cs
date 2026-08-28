using System.Collections.Immutable;
using Groundwork.Query.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Groundwork.Analyzers;

internal sealed class QueryResolution
{
    private QueryResolution(
        ImmutableArray<QueryRequest> requests,
        string? failure,
        SyntaxNode diagnosticNode,
        IfStatementSyntax? fixNode,
        string? failureCode)
    {
        Requests = requests;
        Failure = failure;
        DiagnosticNode = diagnosticNode;
        FixNode = fixNode;
        FailureCode = failureCode;
    }

    public ImmutableArray<QueryRequest> Requests { get; }
    public string? Failure { get; }
    public SyntaxNode DiagnosticNode { get; }
    public IfStatementSyntax? FixNode { get; }
    public string? FailureCode { get; }
    public bool IsResolved => Failure is null;

    public static QueryResolution Resolved(IEnumerable<QueryRequest> requests, SyntaxNode node) =>
        new(requests.ToImmutableArray(), null, node, null, null);

    public static QueryResolution Unresolved(
        string failure,
        SyntaxNode node,
        IfStatementSyntax? fixNode = null,
        string? failureCode = null) =>
        new(ImmutableArray<QueryRequest>.Empty, failure, node, fixNode, failureCode);
}

internal static class QueryResolver
{
    internal static readonly ImmutableHashSet<string> TerminalNames =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "ToList", "ToListAsync", "Count", "CountAsync", "Any", "AnyAsync",
            "First", "FirstOrDefault", "Single", "SingleOrDefault",
            "FirstAsync", "FirstOrDefaultAsync", "SingleAsync", "SingleOrDefaultAsync",
            "Sum", "Min", "Max", "SumAsync", "MinAsync", "MaxAsync");

    /// <summary>
    /// Gate that keeps LINQ-shaped terminals on unrelated types out of the analysis: the receiver
    /// must be fed by a Table&lt;T&gt;() call, and must then be the closed query surface itself or a
    /// type that is not enumerable — the closed surface deliberately is not, so a materialized
    /// collection can never be one. Only an unresolvable receiver falls back to the syntactic
    /// discovery alone.
    /// </summary>
    public static bool IsClosedSurfaceCandidate(
        InvocationExpressionSyntax terminal,
        SemanticModel model,
        CancellationToken cancellationToken)
    {
        if (terminal.Expression is not MemberAccessExpressionSyntax member ||
            FindTableInvocation(member.Expression, terminal) is null)
            return false;
        if (model.GetTypeInfo(member.Expression, cancellationToken).Type is not { } receiver ||
            receiver.TypeKind == TypeKind.Error)
            return true;
        return IsClosedSurfaceType(receiver.OriginalDefinition) ||
               receiver.AllInterfaces.Any(item => IsClosedSurfaceType(item.OriginalDefinition)) ||
               !IsEnumerable(receiver);
    }

    private static bool IsClosedSurfaceType(ITypeSymbol definition) =>
        definition is INamedTypeSymbol { Arity: 1, Name: "IGwQueryable" or "GwQueryTable" } named &&
        named.ContainingNamespace.ToDisplayString() == "Groundwork.Query.Linq";

    private static bool IsEnumerable(ITypeSymbol receiver) =>
        receiver.SpecialType == SpecialType.System_Collections_IEnumerable ||
        receiver.AllInterfaces.Any(item => item.SpecialType == SpecialType.System_Collections_IEnumerable);

    /// <summary>Finds the Table&lt;T&gt;() call feeding a receiver, directly or through its local initializer.</summary>
    private static InvocationExpressionSyntax? FindTableInvocation(
        ExpressionSyntax receiver,
        InvocationExpressionSyntax terminal)
    {
        if (GetInvocationChain(receiver).FirstOrDefault(IsTableInvocation) is { } table)
            return table;
        if (receiver is not IdentifierNameSyntax local)
            return null;
        var method = terminal.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
        var initializer = method?.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(item => item.Identifier.ValueText == local.Identifier.ValueText)
            ?.Initializer?.Value;
        return initializer is InvocationExpressionSyntax invocation
            ? GetInvocationChain(invocation).FirstOrDefault(IsTableInvocation)
            : null;
    }

    public static QueryResolution Resolve(
        InvocationExpressionSyntax terminal,
        SemanticModel model,
        AnalyzerSchema schema,
        CancellationToken cancellationToken)
    {
        if (terminal.Expression is not MemberAccessExpressionSyntax terminalMember ||
            !TerminalNames.Contains(terminalMember.Name.Identifier.ValueText))
            return QueryResolution.Unresolved("the terminal query method is not part of the closed query surface", terminal);

        var chain = GetInvocationChain(terminalMember.Expression);
        if (chain.Count > 0 && TryFindTableInvocation(chain, model, cancellationToken, out var tableInvocation, out var tableType))
            return ResolveChain(terminal, chain, tableInvocation, tableType, model, schema, cancellationToken);

        if (terminalMember.Expression is IdentifierNameSyntax localName)
            return ResolveReassignment(terminal, localName.Identifier.ValueText, model, schema, cancellationToken);

        return QueryResolution.Unresolved(
            "the query does not begin at a statically resolvable Table<T>() call; use the closed query surface or runtime coverage",
            terminal);
    }

    private static QueryResolution ResolveChain(
        InvocationExpressionSyntax terminal,
        IReadOnlyList<InvocationExpressionSyntax> chain,
        InvocationExpressionSyntax tableInvocation,
        INamedTypeSymbol tableType,
        SemanticModel model,
        AnalyzerSchema schema,
        CancellationToken cancellationToken)
    {
        if (!schema.TryGetTable(tableType, out var table))
            return QueryResolution.Unresolved("the Table<T>() type has no visible Groundwork schema metadata", terminal);

        var state = new QueryShapeState(table);
        var optionalPredicates = new List<Predicate>();
        foreach (var invocation in chain.Reverse())
        {
            if (invocation == tableInvocation)
                continue;

            if (invocation.Expression is not MemberAccessExpressionSyntax member)
                return QueryResolution.Unresolved("the query contains an invocation outside the closed surface", invocation);

            var name = member.Name.Identifier.ValueText;
            if (name is "Where" or "WhereIf")
            {
                var lambdaArgument = name == "WhereIf"
                    ? invocation.ArgumentList.Arguments.ElementAtOrDefault(1)?.Expression
                    : invocation.ArgumentList.Arguments.LastOrDefault()?.Expression;
                if (lambdaArgument is null || !TryParseLambda(lambdaArgument, model, table, out var predicate, cancellationToken))
                    return QueryResolution.Unresolved($"the {name} predicate is not a closed, statically typed expression", invocation);
                if (name == "WhereIf")
                {
                    optionalPredicates.Add(predicate);
                    if (optionalPredicates.Count > 6)
                        return QueryResolution.Unresolved(
                            "WhereIf shape enumeration is bounded at six conditional filters; use runtime coverage for larger composition",
                            invocation);
                }
                else
                {
                    state.MandatoryPredicates.Add(predicate);
                }
                continue;
            }

            if (name is "OrderBy" or "OrderByDescending" or "ThenBy" or "ThenByDescending")
            {
                var lambda = invocation.ArgumentList.Arguments.LastOrDefault()?.Expression;
                if (lambda is null || !TryParseColumnLambda(lambda, model, table, out var column, cancellationToken))
                    return QueryResolution.Unresolved($"the {name} ordering expression is not a closed column selector", invocation);
                state.Order.Add(new OrderTerm(
                    ToColumnRef(column, table),
                    name.EndsWith("Descending", StringComparison.Ordinal) ? OrderDirection.Descending : OrderDirection.Ascending,
                    name.EndsWith("Descending", StringComparison.Ordinal) ? NullOrder.First : NullOrder.Last));
                continue;
            }

            if (name == "Select")
            {
                var lambda = invocation.ArgumentList.Arguments.LastOrDefault()?.Expression;
                if (lambda is null || !TryParseProjectionLambda(lambda, model, table, out var projection, cancellationToken))
                    return QueryResolution.Unresolved("Select requires a closed mapped-column projection", invocation);
                if (state.HasProjection)
                    return QueryResolution.Unresolved("the query contains more than one Select projection", invocation);
                state.Projection = projection;
                state.HasProjection = true;
                continue;
            }

            if (name == "Distinct")
            {
                if (invocation.ArgumentList.Arguments.Count != 0)
                    return QueryResolution.Unresolved("Distinct does not accept arguments", invocation);
                state.Distinct = true;
                continue;
            }

            if (name == "Take")
            {
                if (invocation.ArgumentList.Arguments.Count != 1 ||
                    !TryGetInt(invocation.ArgumentList.Arguments[0].Expression, model, out var limit))
                    return QueryResolution.Unresolved("Take requires a compile-time integer bound", invocation);
                if (limit < 0)
                    return QueryResolution.Unresolved("Take requires a non-negative integer bound", invocation);
                if (limit == 0)
                {
                    state.IsEmpty = true;
                    state.Offset = null;
                    state.Limit = null;
                }
                else
                    state.Limit = limit;
                continue;
            }

            if (name == "Skip")
            {
                if (invocation.ArgumentList.Arguments.Count != 1 ||
                    !TryGetInt(invocation.ArgumentList.Arguments[0].Expression, model, out var offset))
                    return QueryResolution.Unresolved("Skip requires a compile-time integer bound", invocation);
                if (offset < 0)
                    return QueryResolution.Unresolved("Skip requires a non-negative integer bound", invocation);
                state.Offset = offset;
                continue;
            }

            if (name == "AcceptScan")
            {
                if (state.AcceptedScan is not null ||
                    !TryParseAcceptScan(invocation, model, out var acceptance))
                    return QueryResolution.Unresolved(
                        "AcceptScan requires compile-time id, reason, owner, and yyyy-MM-dd expiry arguments",
                        invocation);
                state.AcceptedScan = acceptance;
                continue;
            }

            return QueryResolution.Unresolved($"method '{name}' is outside the closed query surface", invocation);
        }

        if (state.Offset is not null && state.Limit is null && RequiresBoundedTake(terminal))
            return QueryResolution.Unresolved("Skip requires Take because an offset-only page is not portable; add a bounded Take.", terminal, failureCode: "GW-LINQ-113");
        if (state.HasProjection && IsReductionTerminal(terminal))
            return QueryResolution.Unresolved("Sum, Min, and Max cannot follow Select; apply the reduction before projecting.", terminal, failureCode: "GW-LINQ-112");
        if (!TryTerminalShape(terminal, table, model, cancellationToken, out var result, out var pagingOverride, out var terminalFailure))
            return QueryResolution.Unresolved(terminalFailure!, terminal, failureCode: "GW-LINQ-112");
        return QueryResolution.Resolved(
            BuildRequests(state, optionalPredicates, result, pagingOverride),
            terminal);
    }

    private static QueryResolution ResolveReassignment(
        InvocationExpressionSyntax terminal,
        string localName,
        SemanticModel model,
        AnalyzerSchema schema,
        CancellationToken cancellationToken)
    {
        var method = terminal.FirstAncestorOrSelf<BaseMethodDeclarationSyntax>();
        if (method is null)
            return QueryResolution.Unresolved("the query local is outside a method body", terminal);

        var declaration = method.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(item => item.Identifier.ValueText == localName);
        if (declaration?.Initializer?.Value is not InvocationExpressionSyntax initializer)
            return QueryResolution.Unresolved("the query local has no statically resolvable Table<T>() initializer", terminal);

        var initialResolution = ResolveInitializer(initializer, model, schema, cancellationToken);
        if (!initialResolution.IsResolved)
            return initialResolution;

        var initializerChain = GetInvocationChain(initializer);
        if (!TryFindTableInvocation(initializerChain, model, cancellationToken, out _, out var tableType) ||
            !schema.TryGetTable(tableType, out var table))
            return QueryResolution.Unresolved("the query local initializer has no visible schema table", initializer);

        var assignments = method.DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Where(item => item.Statement.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>().Any(assignment => IsLocalAssignment(assignment, localName)))
            .ToArray();
        var allAssignments = method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(assignment => IsLocalAssignment(assignment, localName))
            .ToArray();
        if (allAssignments.Any(assignment => !assignments.Any(item => item.DescendantNodesAndSelf().Contains(assignment))))
            return QueryResolution.Unresolved("the query local is reassigned outside a bounded conditional and its shape is not known", terminal);
        if (assignments.Any(item => item.Ancestors().Any(ancestor => ancestor is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax)))
            return QueryResolution.Unresolved("the query local is reassigned inside a loop and its shape is unbounded", terminal);

        foreach (var reference in method.DescendantNodes().OfType<IdentifierNameSyntax>()
                     .Where(identifier => identifier.Identifier.ValueText == localName))
        {
            if (reference.Parent is VariableDeclaratorSyntax)
                continue;
            if (reference.Parent is AssignmentExpressionSyntax assignment && assignment.Left == reference)
                continue;
            if (reference.Parent is MemberAccessExpressionSyntax member && member.Expression == reference &&
                member.Parent is InvocationExpressionSyntax invocation &&
                (member.Name.Identifier.ValueText == "Where" || TerminalNames.Contains(member.Name.Identifier.ValueText)))
                continue;
            return QueryResolution.Unresolved("the query local escapes the method before its terminal operation", terminal);
        }

        var state = new QueryShapeState(table);
        var initialRequest = initialResolution.Requests[0];
        if (initialRequest.Where is not Predicate.AlwaysTrue)
            state.MandatoryPredicates.Add(initialRequest.Where);
        state.Order.AddRange(initialRequest.Order);
        state.Offset = initialRequest.Paging.Offset;
        state.Limit = initialRequest.Paging.Limit;
        state.AcceptedScan = initialRequest.AcceptedScan;
        state.Projection = initialRequest.Projection;
        state.HasProjection = !initialRequest.Projection.AllColumns;
        state.Distinct = initialRequest.Distinct;
        var optional = new List<Predicate>();
        foreach (var ifStatement in assignments)
        {
            var assignment = ifStatement.Statement.DescendantNodesAndSelf()
                .OfType<AssignmentExpressionSyntax>()
                .First(item => IsLocalAssignment(item, localName));
            if (assignment.Right is not InvocationExpressionSyntax whereInvocation ||
                whereInvocation.Expression is not MemberAccessExpressionSyntax whereMember ||
                whereMember.Name.Identifier.ValueText != "Where" ||
                whereInvocation.ArgumentList.Arguments.Count != 1 ||
                !TryParseLambda(whereInvocation.ArgumentList.Arguments[0].Expression, model, state.Table, out var predicate, cancellationToken))
                return QueryResolution.Unresolved("the reassignment idiom must assign q = q.Where(lambda)", ifStatement, ifStatement);
            optional.Add(predicate);
            if (optional.Count > 5)
                return QueryResolution.Unresolved("reassignment shape enumeration is bounded at 32 shapes; use WhereIf or runtime coverage", ifStatement, ifStatement);
        }

        if (state.Offset is not null && state.Limit is null && RequiresBoundedTake(terminal))
            return QueryResolution.Unresolved("Skip requires Take because an offset-only page is not portable; add a bounded Take.", terminal, failureCode: "GW-LINQ-113");
        if (state.HasProjection && IsReductionTerminal(terminal))
            return QueryResolution.Unresolved("Sum, Min, and Max cannot follow Select; apply the reduction before projecting.", terminal, failureCode: "GW-LINQ-112");
        if (!TryTerminalShape(terminal, table, model, cancellationToken, out var result, out var pagingOverride, out var terminalFailure))
            return QueryResolution.Unresolved(terminalFailure!, terminal, failureCode: "GW-LINQ-112");
        return QueryResolution.Resolved(BuildRequests(state, optional, result, pagingOverride), terminal);
    }

    private static QueryResolution ResolveInitializer(
        InvocationExpressionSyntax initializer,
        SemanticModel model,
        AnalyzerSchema schema,
        CancellationToken cancellationToken)
    {
        var chain = GetInvocationChain(initializer);
        if (!TryFindTableInvocation(chain, model, cancellationToken, out var tableInvocation, out var tableType))
            return QueryResolution.Unresolved("the query local initializer does not begin at Table<T>()", initializer);
        return ResolveChain(initializer, chain, tableInvocation, tableType, model, schema, cancellationToken);
    }

    /// <summary>Mirrors the runtime terminal semantics of the closed query surface.</summary>
    private static bool TryTerminalShape(
        InvocationExpressionSyntax terminal,
        AnalyzerTable table,
        SemanticModel model,
        CancellationToken cancellationToken,
        out ResultShape result,
        out Paging? pagingOverride,
        out string? failure)
    {
        result = ResultShape.Rows.Instance;
        pagingOverride = null;
        failure = null;
        if (terminal.Expression is not MemberAccessExpressionSyntax member)
            return true;

        switch (member.Name.Identifier.ValueText)
        {
            case "Count":
            case "CountAsync":
                result = ResultShape.TotalCount.Instance;
                pagingOverride = Paging.None;
                return true;
            case "Any":
            case "AnyAsync":
                result = ResultShape.Rows.Instance;
                pagingOverride = Paging.OffsetLimit(0, 1);
                return true;
            case "First":
            case "FirstAsync":
                result = ResultShape.First.Instance;
                pagingOverride = Paging.OffsetLimit(0, 1);
                return true;
            case "FirstOrDefault":
            case "FirstOrDefaultAsync":
                result = ResultShape.FirstOrDefault.Instance;
                pagingOverride = Paging.OffsetLimit(0, 1);
                return true;
            case "Single":
            case "SingleAsync":
                result = ResultShape.Single.Instance;
                pagingOverride = Paging.OffsetLimit(0, 2);
                return true;
            case "SingleOrDefault":
            case "SingleOrDefaultAsync":
                result = ResultShape.SingleOrDefault.Instance;
                pagingOverride = Paging.OffsetLimit(0, 2);
                return true;
            case "Sum":
            case "SumAsync":
                return TryReductionShape(terminal, member.Name.Identifier.ValueText, table, model, cancellationToken,
                    static column => new ResultShape.Sum(column),
                    static type => type is QueryType.Int32 or QueryType.Int64 or QueryType.Decimal,
                    "Int32, Int64, or Decimal", out result, out failure);
            case "Min":
            case "MinAsync":
                return TryReductionShape(terminal, member.Name.Identifier.ValueText, table, model, cancellationToken,
                    static column => new ResultShape.Min(column),
                    IsOrderable,
                    "orderable", out result, out failure);
            case "Max":
            case "MaxAsync":
                return TryReductionShape(terminal, member.Name.Identifier.ValueText, table, model, cancellationToken,
                    static column => new ResultShape.Max(column),
                    IsOrderable,
                    "orderable", out result, out failure);
            default:
                return true;
        }
    }

    private static bool TryReductionShape(
        InvocationExpressionSyntax terminal,
        string operation,
        AnalyzerTable table,
        SemanticModel model,
        CancellationToken cancellationToken,
        Func<ColumnRef, ResultShape> shape,
        Func<QueryType, bool> isSupported,
        string supportedTypeDescription,
        out ResultShape result,
        out string? failure)
    {
        result = ResultShape.Rows.Instance;
        failure = null;
        // The async convenience surface has executor before the selector and an optional
        // cancellation token after it. Find the lambda rather than assuming the selector is the
        // final argument so explicit cancellation tokens receive the same lowering and diagnostics
        // as the synchronous terminal overloads.
        var selector = terminal.ArgumentList.Arguments
            .Select(argument => argument.Expression)
            .FirstOrDefault(expression => expression is LambdaExpressionSyntax);
        if (selector is null || !TryParseColumnLambda(selector, model, table, out var column, cancellationToken))
        {
            failure = operation + " requires a mapped, portable " + supportedTypeDescription + " column selector.";
            return false;
        }

        var reference = ToColumnRef(column, table);
        if (!isSupported(column.Type))
        {
            failure = operation + " requires an " + supportedTypeDescription + " column selector; '" + column.Name + "' is " + column.Type + ".";
            return false;
        }

        result = shape(reference);
        return true;
    }

    private static bool IsOrderable(QueryType type) => type is
        QueryType.Int32 or QueryType.Int64 or QueryType.Decimal or QueryType.String or
        QueryType.DateTimeOffset or QueryType.Guid;

    private static bool IsReductionTerminal(InvocationExpressionSyntax terminal) =>
        terminal.Expression is MemberAccessExpressionSyntax member &&
        member.Name.Identifier.ValueText is "Sum" or "Min" or "Max" or "SumAsync" or "MinAsync" or "MaxAsync";

    private static bool RequiresBoundedTake(InvocationExpressionSyntax terminal) =>
        terminal.Expression is MemberAccessExpressionSyntax member &&
        member.Name.Identifier.ValueText is "ToList" or "ToListAsync" or "Sum" or "Min" or "Max" or
        "SumAsync" or "MinAsync" or "MaxAsync";

    private static IEnumerable<QueryRequest> BuildRequests(
        QueryShapeState state,
        IReadOnlyList<Predicate> optionalPredicates,
        ResultShape result,
        Paging? pagingOverride)
    {
        var shapeCount = 1 << optionalPredicates.Count;
        for (var mask = 0; mask < shapeCount; mask++)
        {
            var terms = new List<Predicate>(state.MandatoryPredicates);
            for (var index = 0; index < optionalPredicates.Count; index++)
                if ((mask & (1 << index)) != 0)
                    terms.Add(optionalPredicates[index]);
            var predicate = state.IsEmpty ? Predicate.AlwaysFalse.Instance : terms.Count switch
            {
                0 => Predicate.AlwaysTrue.Instance,
                1 => terms[0],
                _ => new Predicate.And(terms)
            };
            var paging = pagingOverride is { Limit: int cardinalityLimit }
                ? Paging.OffsetLimit(state.Offset ?? pagingOverride.Offset ?? 0,
                    state.Limit is int queryLimit ? Math.Min(queryLimit, cardinalityLimit) : cardinalityLimit)
                : state.Limit is int limit
                    ? Paging.OffsetLimit(state.Offset ?? 0, limit)
                    : Paging.None;
            yield return new QueryRequest(
                new TableId(state.Table.Name),
                predicate,
                state.Order.ToImmutableArray(),
                state.Projection,
                paging,
                result,
                acceptedScan: state.AcceptedScan,
                distinct: state.Distinct);
        }
    }

    private static bool TryParseAcceptScan(
        InvocationExpressionSyntax invocation,
        SemanticModel model,
        out ScanAcceptance acceptance)
    {
        acceptance = null!;
        var arguments = invocation.ArgumentList.Arguments;
        var idExpression = GetAcceptArgument(arguments, "id", 0);
        var reasonExpression = GetAcceptArgument(arguments, "reason", 1);
        var ownerExpression = GetAcceptArgument(arguments, "owner", 2);
        var expiryExpression = GetAcceptArgument(arguments, "expiresOn", 3);
        if (arguments.Count != 4 ||
            idExpression is null || reasonExpression is null || ownerExpression is null || expiryExpression is null ||
            !TryGetString(idExpression, model, out var id) ||
            !TryGetString(reasonExpression, model, out var reason) ||
            !TryGetString(ownerExpression, model, out var owner) ||
            !TryGetString(expiryExpression, model, out var expiry) ||
            !DateTime.TryParseExact(
                expiry,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var date))
            return false;

        try
        {
            acceptance = ScanAcceptance.Allow(id, reason, owner, new DateTimeOffset(date, TimeSpan.Zero));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ExpressionSyntax? GetAcceptArgument(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        string name,
        int position) =>
        arguments.FirstOrDefault(argument =>
            string.Equals(argument.NameColon?.Name.Identifier.ValueText, name, StringComparison.Ordinal))?.Expression ??
        arguments.ElementAtOrDefault(position)?.Expression;

    private static List<InvocationExpressionSyntax> GetInvocationChain(ExpressionSyntax expression)
    {
        var result = new List<InvocationExpressionSyntax>();
        while (expression is InvocationExpressionSyntax invocation)
        {
            result.Add(invocation);
            expression = invocation.Expression is MemberAccessExpressionSyntax member
                ? member.Expression
                : invocation.Expression;
        }
        return result;
    }

    private static bool TryFindTableInvocation(
        IReadOnlyList<InvocationExpressionSyntax> chain,
        SemanticModel model,
        CancellationToken cancellationToken,
        out InvocationExpressionSyntax tableInvocation,
        out INamedTypeSymbol tableType)
    {
        foreach (var invocation in chain)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member ||
                member.Name is not GenericNameSyntax generic ||
                generic.Identifier.ValueText != "Table" ||
                generic.TypeArgumentList.Arguments.Count != 1)
                continue;
            if (model.GetTypeInfo(generic.TypeArgumentList.Arguments[0], cancellationToken).Type is INamedTypeSymbol named)
            {
                tableInvocation = invocation;
                tableType = named;
                return true;
            }
        }
        tableInvocation = null!;
        tableType = null!;
        return false;
    }

    private static bool IsTableInvocation(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax
        {
            Name: GenericNameSyntax { Identifier.ValueText: "Table", TypeArgumentList.Arguments.Count: 1 }
        };

    private static bool TryParseLambda(
        ExpressionSyntax expression,
        SemanticModel model,
        AnalyzerTable table,
        out Predicate predicate,
        CancellationToken cancellationToken)
    {
        var lambda = expression as LambdaExpressionSyntax;
        if (lambda is null)
        {
            predicate = null!;
            return false;
        }
        var parameter = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => model.GetDeclaredSymbol(simple.Parameter, cancellationToken),
            ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1 => model.GetDeclaredSymbol(parenthesized.ParameterList.Parameters[0], cancellationToken),
            _ => null
        };
        var body = lambda.Body as ExpressionSyntax;
        if (parameter is null || body is null || !TryParsePredicate(body, parameter, model, table, out predicate, cancellationToken))
        {
            predicate = null!;
            return false;
        }
        return true;
    }

    private static bool TryParseColumnLambda(
        ExpressionSyntax expression,
        SemanticModel model,
        AnalyzerTable table,
        out AnalyzerColumn column,
        CancellationToken cancellationToken)
    {
        column = null!;
        if (expression is not LambdaExpressionSyntax lambda || lambda.Body is not ExpressionSyntax body)
            return false;
        var parameter = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => model.GetDeclaredSymbol(simple.Parameter, cancellationToken),
            ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1 => model.GetDeclaredSymbol(parenthesized.ParameterList.Parameters[0], cancellationToken),
            _ => null
        };
        return parameter is not null && TryGetColumn(body, parameter, model, table, out column, cancellationToken);
    }

    private static bool TryParseProjectionLambda(
        ExpressionSyntax expression,
        SemanticModel model,
        AnalyzerTable table,
        out Projection projection,
        CancellationToken cancellationToken)
    {
        projection = null!;
        if (expression is not LambdaExpressionSyntax lambda || lambda.Body is not ExpressionSyntax body)
            return false;
        var parameter = lambda switch
        {
            SimpleLambdaExpressionSyntax simple => model.GetDeclaredSymbol(simple.Parameter, cancellationToken),
            ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1 => model.GetDeclaredSymbol(parenthesized.ParameterList.Parameters[0], cancellationToken),
            _ => null
        };
        if (parameter is null || !TryGetProjectionColumns(body, parameter, model, table, out var columns, cancellationToken) || columns.Count == 0)
            return false;
        projection = Projection.ColumnsOnly(columns.Select(column => ToColumnRef(column, table)));
        return true;
    }

    private static bool TryGetProjectionColumns(
        ExpressionSyntax expression,
        ISymbol parameter,
        SemanticModel model,
        AnalyzerTable table,
        out List<AnalyzerColumn> columns,
        CancellationToken cancellationToken)
    {
        columns = [];
        expression = Unwrap(expression);
        if (TryGetColumn(expression, parameter, model, table, out var column, cancellationToken))
        {
            columns.Add(column);
            return true;
        }

        IEnumerable<ExpressionSyntax>? members = expression switch
        {
            AnonymousObjectCreationExpressionSyntax anonymous => anonymous.Initializers
                .Select(initializer => initializer.Expression ?? initializer.NameEquals?.Name)
                .Where(member => member is not null)
                .Select(member => member!),
            TupleExpressionSyntax tuple => tuple.Arguments.Select(argument => argument.Expression),
            ObjectCreationExpressionSyntax created => ProjectionArgumentsAndInitializers(created),
            _ => null
        };
        if (members is null)
            return false;
        foreach (var member in members)
        {
            if (member is null || !TryGetProjectionColumns(member, parameter, model, table, out var nested, cancellationToken))
                return false;
            columns.AddRange(nested);
        }
        return true;
    }

    private static IEnumerable<ExpressionSyntax> ProjectionArgumentsAndInitializers(ObjectCreationExpressionSyntax created)
    {
        foreach (var argument in created.ArgumentList?.Arguments ?? [])
            yield return argument.Expression;
        if (created.Initializer is not null)
            foreach (var expression in created.Initializer.Expressions)
                yield return expression is AssignmentExpressionSyntax assignment ? assignment.Right : expression;
    }

    private static bool TryParsePredicate(
        ExpressionSyntax expression,
        ISymbol parameter,
        SemanticModel model,
        AnalyzerTable table,
        out Predicate predicate,
        CancellationToken cancellationToken)
    {
        expression = Unwrap(expression);
        if (expression is BinaryExpressionSyntax logical && logical.IsKind(SyntaxKind.LogicalAndExpression))
        {
            if (TryParsePredicate(logical.Left, parameter, model, table, out var left, cancellationToken) &&
                TryParsePredicate(logical.Right, parameter, model, table, out var right, cancellationToken))
            {
                predicate = new Predicate.And([left, right]);
                return true;
            }
        }
        if (expression is BinaryExpressionSyntax disjunction && disjunction.IsKind(SyntaxKind.LogicalOrExpression))
        {
            if (TryParsePredicate(disjunction.Left, parameter, model, table, out var left, cancellationToken) &&
                TryParsePredicate(disjunction.Right, parameter, model, table, out var right, cancellationToken))
            {
                predicate = new Predicate.Or([left, right]);
                return true;
            }
        }
        if (expression is PrefixUnaryExpressionSyntax not && not.IsKind(SyntaxKind.LogicalNotExpression) &&
            TryParsePredicate(not.Operand, parameter, model, table, out var inner, cancellationToken))
        {
            predicate = new Predicate.Not(inner);
            return true;
        }
        if (expression is BinaryExpressionSyntax comparison && TryParseComparison(comparison, parameter, model, table, out predicate, cancellationToken))
            return true;
        if (expression is InvocationExpressionSyntax invocation && TryParseStringMethod(invocation, parameter, model, table, out predicate, cancellationToken))
            return true;

        predicate = null!;
        return false;
    }

    private static bool TryParseComparison(
        BinaryExpressionSyntax expression,
        ISymbol parameter,
        SemanticModel model,
        AnalyzerTable table,
        out Predicate predicate,
        CancellationToken cancellationToken)
    {
        predicate = null!;
        var kind = expression.Kind();
        var leftIsColumn = TryGetColumn(expression.Left, parameter, model, table, out var leftColumn, cancellationToken);
        var rightIsColumn = TryGetColumn(expression.Right, parameter, model, table, out var rightColumn, cancellationToken);
        if (kind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression)
        {
            if (leftIsColumn && TryGetConstant(expression.Right, leftColumn, model, out var value))
            {
                predicate = kind == SyntaxKind.EqualsExpression
                    ? new Predicate.Equal(ToColumnRef(leftColumn, table), value)
                    : new Predicate.Not(new Predicate.Equal(ToColumnRef(leftColumn, table), value));
                return true;
            }
            if (rightIsColumn && TryGetConstant(expression.Left, rightColumn, model, out value))
            {
                predicate = kind == SyntaxKind.EqualsExpression
                    ? new Predicate.Equal(ToColumnRef(rightColumn, table), value)
                    : new Predicate.Not(new Predicate.Equal(ToColumnRef(rightColumn, table), value));
                return true;
            }
            return false;
        }

        if (!leftIsColumn && !rightIsColumn)
            return false;
        var column = leftIsColumn ? leftColumn : rightColumn;
        var constantExpression = leftIsColumn ? expression.Right : expression.Left;
        if (!TryGetConstant(constantExpression, column, model, out var constant))
            return false;
        var lower = kind switch
        {
            SyntaxKind.GreaterThanExpression when leftIsColumn => Bound.Exclusive(constant),
            SyntaxKind.GreaterThanOrEqualExpression when leftIsColumn => Bound.Inclusive(constant),
            SyntaxKind.LessThanExpression when !leftIsColumn => Bound.Exclusive(constant),
            SyntaxKind.LessThanOrEqualExpression when !leftIsColumn => Bound.Inclusive(constant),
            _ => null
        };
        var upper = kind switch
        {
            SyntaxKind.LessThanExpression when leftIsColumn => Bound.Exclusive(constant),
            SyntaxKind.LessThanOrEqualExpression when leftIsColumn => Bound.Inclusive(constant),
            SyntaxKind.GreaterThanExpression when !leftIsColumn => Bound.Exclusive(constant),
            SyntaxKind.GreaterThanOrEqualExpression when !leftIsColumn => Bound.Inclusive(constant),
            _ => null
        };
        if (lower is null && upper is null)
            return false;
        predicate = new Predicate.Range(ToColumnRef(column, table), lower, upper);
        return true;
    }

    private static bool TryParseStringMethod(
        InvocationExpressionSyntax expression,
        ISymbol parameter,
        SemanticModel model,
        AnalyzerTable table,
        out Predicate predicate,
        CancellationToken cancellationToken)
    {
        predicate = null!;
        if (expression.Expression is not MemberAccessExpressionSyntax member ||
            !TryGetColumn(member.Expression, parameter, model, table, out var column, cancellationToken) ||
            expression.ArgumentList.Arguments.Count != 1 ||
            !TryGetString(expression.ArgumentList.Arguments[0].Expression, model, out var value))
            return false;
        var reference = ToColumnRef(column, table);
        if (member.Name.Identifier.ValueText == "StartsWith")
        {
            predicate = new Predicate.StartsWith(reference, value);
            return true;
        }
        if (member.Name.Identifier.ValueText == "Contains")
        {
            predicate = new Predicate.Substring(reference, value, Anchor.Contains);
            return true;
        }
        return false;
    }

    private static bool TryGetColumn(
        ExpressionSyntax expression,
        ISymbol parameter,
        SemanticModel model,
        AnalyzerTable table,
        out AnalyzerColumn column,
        CancellationToken cancellationToken)
    {
        column = null!;
        expression = Unwrap(expression);
        if (expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Value" } value)
            return TryGetColumn(value.Expression, parameter, model, table, out column, cancellationToken);
        if (expression is not MemberAccessExpressionSyntax member ||
            model.GetSymbolInfo(member.Expression, cancellationToken).Symbol is not ISymbol receiver ||
            !SymbolEqualityComparer.Default.Equals(receiver, parameter))
            return false;
        if (table.TryGetColumn(member.Name.Identifier.ValueText, out column!))
            return true;
        return table.TryGetColumn(ToSnakeCase(member.Name.Identifier.ValueText), out column!);
    }

    private static bool TryGetConstant(ExpressionSyntax expression, AnalyzerColumn column, SemanticModel model, out QueryConstant constant)
    {
        if (expression.IsKind(SyntaxKind.NullLiteralExpression) && column.IsNullable)
        {
            constant = QueryConstant.Of(ToColumnRef(column, null), null);
            return true;
        }
        if (TryGetString(expression, model, out var text) && column.Type == QueryType.String)
        {
            constant = QueryConstant.Of(ToColumnRef(column, null), text);
            return true;
        }
        object value = column.Type switch
        {
            QueryType.Boolean => false,
            QueryType.Int32 => 0,
            QueryType.Int64 => 0L,
            QueryType.Decimal => 0m,
            QueryType.Double => 0d,
            QueryType.String => string.Empty,
            QueryType.DateTimeOffset => new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero),
            QueryType.Guid => Guid.Empty,
            QueryType.Binary => Array.Empty<byte>(),
            _ => string.Empty
        };
        constant = QueryConstant.Of(ToColumnRef(column, null), value);
        return true;
    }

    private static bool TryGetString(ExpressionSyntax expression, SemanticModel model, out string value)
    {
        var constant = model.GetConstantValue(expression);
        if (constant.HasValue && constant.Value is string text)
        {
            value = text;
            return true;
        }
        if (model.GetSymbolInfo(expression).Symbol is IFieldSymbol { IsConst: true, ConstantValue: string constText })
        {
            value = constText;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static bool TryGetInt(ExpressionSyntax expression, SemanticModel model, out int value)
    {
        var constant = model.GetConstantValue(expression);
        if (constant.HasValue && constant.Value is int number)
        {
            value = number;
            return true;
        }
        value = 0;
        return false;
    }

    private static ColumnRef ToColumnRef(AnalyzerColumn column, AnalyzerTable? table) => new(
        table is null ? TableId.Empty : new TableId(table.Name),
        column.Name,
        column.Type,
        column.IsNullable,
        column.MaxLength,
        column.Precision,
        column.Scale);

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;
        if (expression is CastExpressionSyntax cast)
            return Unwrap(cast.Expression);
        if (expression is CheckedExpressionSyntax checkedExpression)
            return Unwrap(checkedExpression.Expression);
        return expression;
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new System.Text.StringBuilder();
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsUpper(value[index]) && index > 0)
                builder.Append('_');
            builder.Append(char.ToLowerInvariant(value[index]));
        }
        return builder.ToString();
    }

    private static bool IsLocalAssignment(AssignmentExpressionSyntax assignment, string localName) =>
        assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
        assignment.Left is IdentifierNameSyntax identifier &&
        identifier.Identifier.ValueText == localName;

    private sealed class QueryShapeState
    {
        public QueryShapeState(AnalyzerTable table)
        {
            Table = table;
        }

        public AnalyzerTable Table { get; }
        public List<Predicate> MandatoryPredicates { get; } = new();
        public List<OrderTerm> Order { get; } = new();
        public Projection Projection { get; set; } = Projection.All;
        public bool HasProjection { get; set; }
        public bool Distinct { get; set; }
        public int? Offset { get; set; }
        public int? Limit { get; set; }
        public bool IsEmpty { get; set; }
        public ScanAcceptance? AcceptedScan { get; set; }
    }

}
