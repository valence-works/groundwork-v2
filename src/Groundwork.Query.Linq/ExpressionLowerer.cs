using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using Groundwork.Query.Model;
using AstPredicate = Groundwork.Query.Model.Predicate;

namespace Groundwork.Query.Linq;

/// <summary>Converts the deliberately closed LINQ vocabulary to the existing query AST.</summary>
public static class ExpressionLowerer
{
    private static readonly ConcurrentDictionary<string, Lazy<ClosedAccessorPlan>> ClosedAccessors = new(StringComparer.Ordinal);
    private static int closedAccessorCompilations;

    internal static int ClosedAccessorCount => ClosedAccessors.Count;
    internal static int ClosedAccessorCompilationCount => Volatile.Read(ref closedAccessorCompilations);

    public static Predicate Lower<T>(Expression<Func<T, bool>> expression, GwTableModel<T> model)
    {
        if (expression is null) throw new ArgumentNullException(nameof(expression));
        if (model is null) throw new ArgumentNullException(nameof(model));
        var diagnostics = new List<LinqDiagnostic>();
        var predicate = new LoweringVisitor<T>(expression.Parameters[0], model, diagnostics).Predicate(expression.Body);
        ThrowIfAny(diagnostics.Where(diagnostic => diagnostic.Code != "GW-LINQ-103").ToArray());
        return PredicateNormalizer.Normalize(predicate);
    }

    public static ColumnRef LowerColumn<T, TValue>(Expression<Func<T, TValue>> expression, GwTableModel<T> model)
    {
        if (expression is null) throw new ArgumentNullException(nameof(expression));
        var diagnostics = new List<LinqDiagnostic>();
        var column = new LoweringVisitor<T>(expression.Parameters[0], model, diagnostics).Column(expression.Body);
        ThrowIfAny(diagnostics);
        return column;
    }

    public static IReadOnlyList<LinqDiagnostic> Diagnose<T>(Expression<Func<T, bool>> expression, GwTableModel<T> model)
    {
        if (expression is null) throw new ArgumentNullException(nameof(expression));
        if (model is null) throw new ArgumentNullException(nameof(model));
        var diagnostics = new List<LinqDiagnostic>();
        _ = new LoweringVisitor<T>(expression.Parameters[0], model, diagnostics).Predicate(expression.Body);
        return diagnostics.AsReadOnly();
    }

    internal static object? ClosedValue(Expression expression, ParameterExpression parameter, ICollection<LinqDiagnostic> diagnostics)
    {
        if (ContainsParameter(expression, parameter))
        {
            diagnostics.Add(new LinqDiagnostic("GW-LINQ-107", "The expression contains an opaque helper; mark it [GwQueryFragment]", expression));
            return null;
        }

        try
        {
            var key = ClosedShapeKey(expression);
            var accessor = ClosedAccessors.GetOrAdd(key, _ => new Lazy<ClosedAccessorPlan>(
                () => new ClosedAccessorPlan(expression),
                LazyThreadSafetyMode.ExecutionAndPublication));
            return accessor.Value.Read(expression);
        }
        catch (InvalidProgramException)
        {
            try { return ReadClosed(expression); }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or TargetInvocationException)
            {
                diagnostics.Add(new LinqDiagnostic("GW-LINQ-107", "The expression contains an opaque helper; mark it [GwQueryFragment]", expression)
                { Path = exception.Message });
                return null;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or TargetInvocationException)
        {
            diagnostics.Add(new LinqDiagnostic("GW-LINQ-107", "The expression contains an opaque helper; mark it [GwQueryFragment]", expression)
            { Path = exception.Message });
            return null;
        }
    }

    private sealed class ClosedAccessorPlan
    {
        private readonly Func<object, object?>? closureFieldAccessor;

        public ClosedAccessorPlan(Expression expression)
        {
            if (!TryClosureFieldChain(expression, out var root, out var rewritten)) return;
            var parameter = Expression.Parameter(typeof(object), "closure");
            var body = new ClosureRootReplacer(root!, parameter).Visit(rewritten)!;
            closureFieldAccessor = Expression.Lambda<Func<object, object?>>(Expression.Convert(body, typeof(object)), parameter).Compile();
            Interlocked.Increment(ref closedAccessorCompilations);
        }

        // The only compiled code is a typed getter for a validated compiler closure field chain.
        // Surrounding expressions are interpreted by ReadClosed and cannot execute user code.
        public object? Read(Expression expression)
        {
            if (closureFieldAccessor is not null && TryClosureRoot(expression, out var root) && root.Value is not null)
                return closureFieldAccessor(root.Value);
            return ReadClosed(expression);
        }

        private static bool TryClosureFieldChain(Expression expression, out ConstantExpression? root, out Expression rewritten)
        {
            root = null;
            rewritten = expression;
            var current = Unwrap(expression);
            while (current is MemberExpression member && member.Member is FieldInfo field && IsCompilerClosureField(field))
            {
                current = Unwrap(member.Expression!);
                if (member.Expression is ConstantExpression constant && IsCompilerClosureType(constant.Type) && !field.IsStatic)
                {
                    root = constant;
                    return true;
                }
            }
            return false;
        }

        private static bool TryClosureRoot(Expression expression, out ConstantExpression root)
        {
            ConstantExpression? foundRoot = null;
            new ClosureRootFinder(value => foundRoot = value).Visit(expression);
            root = foundRoot!;
            return foundRoot is not null;
        }

        private sealed class ClosureRootFinder : ExpressionVisitor
        {
            private readonly Action<ConstantExpression> found;
            public ClosureRootFinder(Action<ConstantExpression> found) => this.found = found;
            protected override Expression VisitConstant(ConstantExpression node)
            {
                if (IsCompilerClosureType(node.Type)) found(node);
                return node;
            }
        }

        private sealed class ClosureRootReplacer : ExpressionVisitor
        {
            private readonly ConstantExpression source;
            private readonly ParameterExpression replacement;
            public ClosureRootReplacer(ConstantExpression source, ParameterExpression replacement) { this.source = source; this.replacement = replacement; }
            protected override Expression VisitConstant(ConstantExpression node) => node == source ? Expression.Convert(replacement, source.Type) : node;
        }
    }

    private static string ClosedShapeKey(Expression expression)
    {
        var builder = new System.Text.StringBuilder();
        new ClosedShapeVisitor(builder).Visit(expression);
        return builder.ToString();
    }

    private sealed class ClosedShapeVisitor : ExpressionVisitor
    {
        private readonly System.Text.StringBuilder builder;
        public ClosedShapeVisitor(System.Text.StringBuilder builder) => this.builder = builder;
        public override Expression? Visit(Expression? node)
        {
            if (node is null) return null;
            builder.Append('[').Append(node.NodeType).Append('|').Append(node.Type.AssemblyQualifiedName).Append(']');
            return base.Visit(node);
        }
        protected override Expression VisitConstant(ConstantExpression node)
        {
            builder.Append("const:").Append(node.Type.AssemblyQualifiedName);
            return node;
        }
        protected override Expression VisitMember(MemberExpression node)
        {
            builder.Append("member:").Append(node.Member.DeclaringType?.AssemblyQualifiedName).Append('|').Append(node.Member.Name).Append('|').Append(node.Member.MemberType);
            return base.VisitMember(node);
        }
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            builder.Append("method:").Append(node.Method.DeclaringType?.AssemblyQualifiedName).Append('|').Append(node.Method.Name);
            return base.VisitMethodCall(node);
        }
    }

    private static void ThrowIfAny(IReadOnlyList<LinqDiagnostic> diagnostics)
    {
        if (diagnostics.Count != 0) throw new LinqTranslationException(diagnostics);
    }

    private static bool ContainsParameter(Expression expression, ParameterExpression parameter)
    {
        var found = false;
        new ParameterFinder(parameter, () => found = true).Visit(expression);
        return found;
    }

    private static object? ReadClosed(Expression expression)
    {
        if (expression is UnaryExpression conversion && conversion.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked)
        {
            var value = ReadClosed(conversion.Operand);
            if (value is null) return null;
            var target = Nullable.GetUnderlyingType(conversion.Type) ?? conversion.Type;
            if (target == typeof(object)) return value;
            return target.IsInstanceOfType(value) ? value : Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        }
        expression = Unwrap(expression);
        switch (expression)
        {
            case ConstantExpression constant: return constant.Value;
            case MemberExpression member:
            {
                var instance = member.Expression is null ? null : ReadClosed(member.Expression);
                if (member.Expression is null && member.Member.DeclaringType == typeof(DateTimeOffset) && member.Member.Name == "UtcNow")
                    return DateTimeOffset.UtcNow;
                if (member.Member is FieldInfo field && IsCompilerClosureField(field) && !field.IsStatic)
                    return field.GetValue(instance);
                throw new InvalidOperationException("Only compiler closure fields and approved BCL values are readable.");
            }
            case NewArrayExpression array:
                return array.Expressions.Select(ReadClosed).ToArray();
            case NewExpression created when created.Constructor?.DeclaringType == typeof(DateTime) || created.Constructor?.DeclaringType == typeof(DateTimeOffset):
                return created.Constructor!.Invoke(created.Arguments.Select(ReadClosed).ToArray());
            case BinaryExpression binary:
                return ReadBinary(binary);
            case UnaryExpression unary when unary.NodeType == ExpressionType.Not:
                return !(bool)ReadClosed(unary.Operand)!;
            case UnaryExpression unary when unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked:
            {
                var value = ReadClosed(unary.Operand);
                if (value is null) return null;
                var target = Nullable.GetUnderlyingType(unary.Type) ?? unary.Type;
                return target.IsInstanceOfType(value) ? value : Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
            }
            case UnaryExpression unary:
                return ReadClosed(unary.Operand);
            case MethodCallExpression call when call.Method.Name == "op_Implicit" && call.Arguments.Count == 1 && call.Arguments[0].Type.IsArray:
                return ReadClosed(call.Arguments[0]);
            case MethodCallExpression call when call.Object is not null &&
                call.Object.Type == typeof(DateTimeOffset) &&
                call.Method.Name is "AddDays" or "AddHours" or "AddMinutes" or "AddSeconds" or "AddTicks":
                return call.Method.Invoke(ReadClosed(call.Object), call.Arguments.Select(ReadClosed).ToArray());
            default:
                throw new InvalidOperationException("The closed term contains an opaque method, property, or constructor.");
        }
    }

    private static bool IsCompilerClosureType(Type? type) => type is not null &&
        type.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Length != 0 &&
        type.IsNestedPrivate && type.Name.Contains("DisplayClass", StringComparison.Ordinal);

    private static bool IsCompilerClosureField(FieldInfo field) => !field.IsStatic && IsCompilerClosureType(field.DeclaringType);

    private static object? ReadBinary(BinaryExpression expression)
    {
        var left = ReadClosed(expression.Left);
        var right = ReadClosed(expression.Right);
        return expression.NodeType switch
        {
            ExpressionType.Add or ExpressionType.AddChecked => Add(left, right),
            ExpressionType.Subtract or ExpressionType.SubtractChecked => Subtract(left, right),
            ExpressionType.Multiply or ExpressionType.MultiplyChecked => Multiply(left, right),
            ExpressionType.Divide => Divide(left, right),
            ExpressionType.Equal => Equals(left, right),
            ExpressionType.NotEqual => !Equals(left, right),
            ExpressionType.GreaterThan => CompareClosed(left, right) > 0,
            ExpressionType.GreaterThanOrEqual => CompareClosed(left, right) >= 0,
            ExpressionType.LessThan => CompareClosed(left, right) < 0,
            ExpressionType.LessThanOrEqual => CompareClosed(left, right) <= 0,
            ExpressionType.AndAlso => (bool)left! && (bool)right!,
            ExpressionType.OrElse => (bool)left! || (bool)right!,
            _ => throw new InvalidOperationException()
        };
    }

    private static int CompareClosed(object? left, object? right) => left is IComparable comparable ? comparable.CompareTo(right) : throw new InvalidOperationException();

    private static object Add(object? left, object? right) => (left, right) switch
    {
        (int a, int b) => a + b,
        (long a, long b) => a + b,
        (decimal a, decimal b) => a + b,
        (string a, string b) => a + b,
        (string a, object b) => a + (b?.ToString() ?? string.Empty),
        (DateTimeOffset a, TimeSpan b) => a + b,
        _ => throw new InvalidOperationException()
    };
    private static object Subtract(object? left, object? right) => (left, right) switch
    {
        (int a, int b) => a - b,
        (long a, long b) => a - b,
        (decimal a, decimal b) => a - b,
        (DateTimeOffset a, TimeSpan b) => a - b,
        _ => throw new InvalidOperationException()
    };
    private static object Multiply(object? left, object? right) => (left, right) switch
    {
        (int a, int b) => a * b,
        (long a, long b) => a * b,
        (decimal a, decimal b) => a * b,
        _ => throw new InvalidOperationException()
    };
    private static object Divide(object? left, object? right) => (left, right) switch
    {
        (int a, int b) => a / b,
        (long a, long b) => a / b,
        (decimal a, decimal b) => a / b,
        _ => throw new InvalidOperationException()
    };

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression unary &&
               (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked || unary.NodeType == ExpressionType.Quote))
            expression = unary.Operand;
        return expression;
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        private readonly ParameterExpression parameter;
        private readonly Action found;
        public ParameterFinder(ParameterExpression parameter, Action found) { this.parameter = parameter; this.found = found; }
        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node == parameter) found();
            return node;
        }
    }

    private sealed class LoweringVisitor<T>
    {
        private readonly ParameterExpression parameter;
        private readonly GwTableModel<T> model;
        private readonly ICollection<LinqDiagnostic> diagnostics;

        public LoweringVisitor(ParameterExpression parameter, GwTableModel<T> model, ICollection<LinqDiagnostic> diagnostics)
        {
            this.parameter = parameter; this.model = model; this.diagnostics = diagnostics;
        }

        public AstPredicate Predicate(Expression source)
        {
            var expression = Unwrap(source);
            switch (expression)
            {
                case BinaryExpression binary when binary.NodeType == ExpressionType.AndAlso:
                    return new AstPredicate.And(new[] { Predicate(binary.Left), Predicate(binary.Right) });
                case BinaryExpression binary when binary.NodeType == ExpressionType.OrElse:
                    return new AstPredicate.Or(new[] { Predicate(binary.Left), Predicate(binary.Right) });
                case UnaryExpression unary when unary.NodeType == ExpressionType.Not:
                    return new AstPredicate.Not(Predicate(unary.Operand));
                case BinaryExpression binary when IsComparison(binary.NodeType):
                    return Comparison(binary);
                case MemberExpression member when member.Type == typeof(bool):
                    return BoolMember(member, true);
                case MethodCallExpression call:
                    return MethodPredicate(call);
                case MemberExpression fragmentMember when fragmentMember.Member.GetCustomAttribute<GwQueryFragmentAttribute>() is not null:
                    return Predicate(InlineFragment(fragmentMember, fragmentMember.Member is PropertyInfo property ? property.GetValue(null, null) : null));
                case ConstantExpression constant when constant.Type == typeof(bool):
                    return (bool)constant.Value! ? AstPredicate.AlwaysTrue.Instance : AstPredicate.AlwaysFalse.Instance;
                default:
                    Add("GW-LINQ-107", "The expression contains an opaque helper; mark it [GwQueryFragment]", expression);
                    return AstPredicate.AlwaysFalse.Instance;
            }
        }

        private AstPredicate BoolMember(MemberExpression member, bool expected)
        {
            var expression = Unwrap(member.Expression!);
            if (member.Member.Name == "HasValue" && TryColumn(expression) is { } nullable)
            {
                var nullValue = QueryConstant.Of(nullable, null);
                return expected ? new AstPredicate.Not(new AstPredicate.Equal(nullable, nullValue)) : new AstPredicate.Equal(nullable, nullValue);
            }
            if (TryColumn(member) is { } column && column.Type == QueryType.Boolean)
                return expected ? new AstPredicate.Equal(column, QueryConstant.Of(column, true)) : new AstPredicate.Not(new AstPredicate.Equal(column, QueryConstant.Of(column, true)));
            Add("GW-LINQ-101", "A member method/property on a column is not portable; declare a computed column; expressions over columns are not portable", member);
            return AstPredicate.AlwaysFalse.Instance;
        }

        public ColumnRef Column(Expression source)
        {
            var expression = Unwrap(source);
            if (expression is MemberExpression member && member.Expression == parameter && model.Columns.TryGetValue(member.Member.Name, out var column))
                return column;
            if (expression is MemberExpression nested && nested.Expression is MemberExpression inner &&
                model.Columns.ContainsKey(inner.Member.Name) && nested.Member.Name is "Value")
                return model.Column(inner.Member.Name);
            Add("GW-LINQ-101", "The selected expression is not a mapped column; declare a computed column; expressions over columns are not portable", expression);
            return new ColumnRef(model.Table, "__invalid", QueryType.String);
        }

        private Predicate Comparison(BinaryExpression binary)
        {
            var left = Unwrap(binary.Left);
            var right = Unwrap(binary.Right);
            var leftColumn = TryColumn(left);
            var rightColumn = TryColumn(right);
            if (leftColumn is not null && rightColumn is not null)
            {
                Add("GW-LINQ-103", "Column-to-column comparison is allowed, but never index-covered — add `.AcceptScan(...)`", binary);
                return new AstPredicate.ColumnCompare(leftColumn, Compare(binary.NodeType), rightColumn);
            }
            if (TryDatePart(left, out var leftDateColumn, out var leftDatePart) && rightColumn is null)
                return DatePartComparison(binary, leftDateColumn, leftDatePart, right);
            if (TryDatePart(right, out var rightDateColumn, out var rightDatePart) && leftColumn is null)
                return DatePartComparison(binary, rightDateColumn, rightDatePart, left, reverse: true);

            var column = leftColumn ?? rightColumn;
            if (column is null)
            {
                if (HasUnsupportedColumnMethod(left) || HasUnsupportedColumnMethod(right) || HasUnsupportedColumnMember(left) || HasUnsupportedColumnMember(right)) Add("GW-LINQ-101", "A member method/property on a column is not portable; declare a computed column; expressions over columns are not portable", binary);
                else if (IsNavigation(left) || IsNavigation(right)) Add("GW-LINQ-104", "Navigation and Join are not portable; v2 has no joins; use a declared element set or two queries", binary);
                else if (HasColumn(binary)) Add("GW-LINQ-102", "Arithmetic on columns is not portable; declare a computed column; expressions over columns are not portable", binary);
                else Add("GW-LINQ-107", "The expression contains an opaque helper; mark it [GwQueryFragment]", binary);
                return AstPredicate.AlwaysFalse.Instance;
            }
            var term = ClosedConstant(leftColumn is not null ? right : left, column, binary);
            if (term is null && !HasNullConstant(leftColumn is not null ? right : left)) return AstPredicate.AlwaysFalse.Instance;
            var operation = Compare(binary.NodeType);
            if (leftColumn is null) operation = Invert(operation);
            if (operation == CompareOp.Equal) return new AstPredicate.Equal(column, term!);
            if (operation == CompareOp.NotEqual) return new AstPredicate.Not(new AstPredicate.Equal(column, term!));
            return operation switch
            {
                CompareOp.GreaterThan => new AstPredicate.Range(column, Bound.Exclusive(term!), null),
                CompareOp.GreaterThanOrEqual => new AstPredicate.Range(column, Bound.Inclusive(term!), null),
                CompareOp.LessThan => new AstPredicate.Range(column, null, Bound.Exclusive(term!)),
                _ => new AstPredicate.Range(column, null, Bound.Inclusive(term!))
            };
        }

        private Predicate DatePartComparison(BinaryExpression binary, ColumnRef column, string part, Expression other, bool reverse = false)
        {
            var value = ClosedValue(other, parameter, diagnostics);
            if (part == "Year" && value is int year)
            {
                var lower = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero);
                var upper = lower.AddYears(1);
                return new AstPredicate.And(new AstPredicate[] { new AstPredicate.Range(column, Bound.Inclusive(QueryConstant.Of(column, lower)), Bound.Exclusive(QueryConstant.Of(column, upper))) });
            }
            if (part == "Date" && value is DateTime date)
            {
                var lower = new DateTimeOffset(date.Date, TimeSpan.Zero);
                var upper = lower.AddDays(1);
                return new AstPredicate.Range(column, Bound.Inclusive(QueryConstant.Of(column, lower)), Bound.Exclusive(QueryConstant.Of(column, upper)));
            }
            Add("GW-LINQ-107", "The date-part comparison is not a supported UTC range.", binary);
            return AstPredicate.AlwaysFalse.Instance;
        }

        private Predicate MethodPredicate(MethodCallExpression call)
        {
            if (TryFragment(call, out var fragment)) return Predicate(fragment.Body);
            if (ContainsGroupBy(call))
            {
                Add("GW-LINQ-105", "GroupBy is not portable; use `.LatestPer(...)` for grouped top-1", call);
                return AstPredicate.AlwaysFalse.Instance;
            }
            if (call.Method.Name == "Join")
            {
                Add("GW-LINQ-104", "Navigation and Join are not portable; v2 has no joins; use a declared element set or two queries", call);
                return AstPredicate.AlwaysFalse.Instance;
            }
            if (call.Method.Name == "GroupBy")
            {
                Add("GW-LINQ-105", "GroupBy is not portable; use `.LatestPer(...)` for grouped top-1", call);
                return AstPredicate.AlwaysFalse.Instance;
            }
            var target = call.Object is null ? null : Unwrap(call.Object);
            if (call.Method.Name == "Contains" && target is MemberExpression setMember && model.ElementSets.ContainsKey(setMember.Member.Name) && call.Arguments.Count == 1)
            {
                var column = model.ElementSet(setMember.Member.Name);
                var value = ClosedValue(call.Arguments[0], parameter, diagnostics);
                return new AstPredicate.ElementOf(column, new[] { QueryConstant.Of(value) }, SetQuantifier.Any);
            }
            if (target is MemberExpression stringMember && TryColumn(stringMember) is { Type: QueryType.String } stringColumn)
            {
                if (call.Arguments.Count != 2 || call.Arguments[1] is not ConstantExpression comparison || comparison.Value is not StringComparison policy)
                {
                    Add("GW-LINQ-108", "String matching must use an explicit comparison overload; use Ordinal/OrdinalIgnoreCase matching the column's folding", call);
                    return AstPredicate.AlwaysFalse.Instance;
                }
                if (policy is not StringComparison.Ordinal and not StringComparison.OrdinalIgnoreCase)
                {
                    Add("GW-LINQ-108", "Culture-sensitive string matching is not portable; use Ordinal/OrdinalIgnoreCase matching the column's folding", call);
                    return AstPredicate.AlwaysFalse.Instance;
                }
                var expected = policy == StringComparison.Ordinal ? QueryStringComparisonPolicy.Ordinal : QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase;
                if (stringColumn.StringComparison != expected)
                {
                    Add("GW-LINQ-108", "String matching must agree with the column's declared folding; use Ordinal/OrdinalIgnoreCase matching the column's folding", call);
                    return AstPredicate.AlwaysFalse.Instance;
                }
                var value = ClosedValue(call.Arguments[0], parameter, diagnostics) as string ?? string.Empty;
                return call.Method.Name switch
                {
                    "StartsWith" => new AstPredicate.StartsWith(stringColumn, value),
                    "Contains" => new AstPredicate.Substring(stringColumn, value, Anchor.Contains),
                    "EndsWith" => new AstPredicate.Substring(stringColumn, value, Anchor.EndsWith),
                    _ => Opaque(call)
                };
            }
            if (call.Method.Name == "Contains" && call.Arguments.Count == 2 && call.Method.IsStatic)
            {
                var column = TryColumn(call.Arguments[1]);
                var values = ClosedValue(call.Arguments[0], parameter, diagnostics) as IEnumerable;
                if (column is not null && values is not null)
                    return new AstPredicate.In(column, values.Cast<object?>().Select(value => QueryConstant.Of(column, value)));
            }
            if (call.Method.Name == "Contains" && call.Arguments.Count == 1 && target is not null)
            {
                var column = TryColumn(call.Arguments[0]);
                var values = ClosedValue(target, parameter, diagnostics) as IEnumerable;
                if (column is not null && values is not null)
                    return new AstPredicate.In(column, values.Cast<object?>().Select(value => QueryConstant.Of(column, value)));
            }
            var collectionExpression = target ?? (call.Method.IsStatic && call.Arguments.Count != 0 ? Unwrap(call.Arguments[0]) : null);
            var lambdaArgument = call.Method.IsStatic && call.Arguments.Count != 0 ? call.Arguments[call.Arguments.Count - 1] : call.Arguments.SingleOrDefault();
            if (call.Method.Name is "Any" or "All" && collectionExpression is MemberExpression collection && model.ElementSets.ContainsKey(collection.Member.Name) && lambdaArgument is not null)
            {
                var lambda = (LambdaExpression)Unwrap(lambdaArgument);
                if (lambda.Body is BinaryExpression equality && equality.NodeType == ExpressionType.Equal)
                {
                    var constant = equality.Left == lambda.Parameters[0] ? equality.Right : equality.Right == lambda.Parameters[0] ? equality.Left : null;
                    if (constant is not null)
                        return new AstPredicate.ElementOf(model.ElementSet(collection.Member.Name), new[] { QueryConstant.Of(ClosedValue(constant, parameter, diagnostics)) }, call.Method.Name == "Any" ? SetQuantifier.Any : SetQuantifier.All);
                }
                Add("GW-LINQ-106", "Nested Any/All predicates must be equality-only; declare the element set", call);
                return AstPredicate.AlwaysFalse.Instance;
            }
            Add("GW-LINQ-107", "The expression contains an opaque helper; mark it [GwQueryFragment]", call);
            return AstPredicate.AlwaysFalse.Instance;
        }

        private LambdaExpression InlineFragment(Expression source, object? value)
        {
            var fragment = value as LambdaExpression ?? throw new LinqTranslationException(new[]
            {
                new LinqDiagnostic("GW-LINQ-107", "A GwQueryFragment must return an expression; mark it [GwQueryFragment]", source)
            });
            return (LambdaExpression)new ParameterReplacer(fragment.Parameters[0], parameter).Visit(fragment)!;
        }

        private bool TryFragment(MethodCallExpression call, out LambdaExpression fragment)
        {
            fragment = null!;
            if (call.Method.GetCustomAttribute<GwQueryFragmentAttribute>() is null || call.Arguments.Count != 0) return false;
            fragment = InlineFragment(call, call.Method.Invoke(null, null));
            return true;
        }

        private Predicate Opaque(Expression expression)
        {
            Add("GW-LINQ-107", "The expression contains an opaque helper; mark it [GwQueryFragment]", expression);
            return AstPredicate.AlwaysFalse.Instance;
        }

        private QueryConstant? ClosedConstant(Expression expression, ColumnRef column, Expression span)
        {
            if (IsForbiddenDate(expression))
            {
                Add("GW-LINQ-109", "DateTime.Now/Today or an unspecified instant is not portable; use DateTimeOffset.UtcNow", expression);
                return null;
            }
            var value = ClosedValue(expression, parameter, diagnostics);
            try { return QueryConstant.Of(column, value); }
            catch (ArgumentException)
            {
                Add("GW-LINQ-110", "The value does not round-trip into the declared column type; the value has more scale/range than `decimal(10,2)`", span);
                return null;
            }
        }

        private bool IsForbiddenDate(Expression expression)
        {
            expression = Unwrap(expression);
            if (expression is MemberExpression member &&
                ((member.Member.DeclaringType == typeof(DateTime) && member.Member.Name is "Now" or "Today") ||
                 (member.Member.DeclaringType == typeof(DateTimeOffset) && member.Member.Name == "Now"))) return true;
            return expression.Type == typeof(DateTime) && ClosedValue(expression, parameter, diagnostics) is DateTime date && date.Kind == DateTimeKind.Unspecified;
        }

        private ColumnRef? TryColumn(Expression source)
        {
            source = Unwrap(source);
            if (source is MemberExpression member && member.Expression == parameter && model.Columns.TryGetValue(member.Member.Name, out var column)) return column;
            if (source is MemberExpression value && value.Member.Name == "Value" && value.Expression is MemberExpression nullable && nullable.Expression == parameter && model.Columns.TryGetValue(nullable.Member.Name, out var nullableColumn)) return nullableColumn;
            return null;
        }

        private bool TryDatePart(Expression source, out ColumnRef column, out string part)
        {
            column = null!; part = string.Empty; source = Unwrap(source);
            if (source is MemberExpression member && member.Expression is MemberExpression inner && inner.Expression == parameter &&
                model.Columns.TryGetValue(inner.Member.Name, out column!) && member.Member.Name is "Year" or "Date") { part = member.Member.Name; return true; }
            return false;
        }
        private bool HasColumn(Expression source) => ContainsParameter(source, parameter);
        private bool IsNavigation(Expression source) => Unwrap(source) is MemberExpression member && member.Expression is MemberExpression nested && HasColumn(nested);
        private bool HasUnsupportedColumnMethod(Expression source) => Unwrap(source) is MethodCallExpression call && call.Object is not null && HasColumn(call.Object) && call.Method.Name is "ToLower" or "ToUpper" or "Substring" or "Trim";
        private bool HasUnsupportedColumnMember(Expression source) => Unwrap(source) is MemberExpression member && member.Member.Name == "Length" && HasColumn(member.Expression!);
        private static bool ContainsGroupBy(MethodCallExpression call) => call.Method.Name == "GroupBy" || call.Object is MethodCallExpression nested && ContainsGroupBy(nested) || call.Arguments.Any(argument => Unwrap(argument) is MethodCallExpression nested && ContainsGroupBy(nested));
        private bool HasNullConstant(Expression source) => Unwrap(source) is ConstantExpression constant && constant.Value is null;
        private void Add(string code, string message, Expression expression) => diagnostics.Add(new LinqDiagnostic(code, message, expression));

        private static bool IsComparison(ExpressionType type) => type is ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual;
        private static CompareOp Compare(ExpressionType type) => type switch
        {
            ExpressionType.Equal => CompareOp.Equal, ExpressionType.NotEqual => CompareOp.NotEqual,
            ExpressionType.LessThan => CompareOp.LessThan, ExpressionType.LessThanOrEqual => CompareOp.LessThanOrEqual,
            ExpressionType.GreaterThan => CompareOp.GreaterThan, _ => CompareOp.GreaterThanOrEqual
        };
        private static CompareOp Invert(CompareOp op) => op switch
        {
            CompareOp.LessThan => CompareOp.GreaterThan, CompareOp.LessThanOrEqual => CompareOp.GreaterThanOrEqual,
            CompareOp.GreaterThan => CompareOp.LessThan, CompareOp.GreaterThanOrEqual => CompareOp.LessThanOrEqual,
            _ => op
        };
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression from;
        private readonly ParameterExpression to;
        public ParameterReplacer(ParameterExpression from, ParameterExpression to) { this.from = from; this.to = to; }
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
