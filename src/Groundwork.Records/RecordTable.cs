using System.Linq.Expressions;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using Groundwork.Kernel;
using Groundwork.Query.Linq;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;
using DeclarationState = Groundwork.Kernel.StorageDeclarationState;

namespace Groundwork.Records;

/// <summary>Starts a strongly typed storage declaration.</summary>
public static class RecordTable
{
    [RequiresUnreferencedCode(
        "Infers a declaration from CLR members. Use the generated schema declaration directly in trimmed applications.")]
    public static RecordTableBuilder<T> For<T>(string name) => new(name);

    /// <summary>
    /// Binds a source-generated row accessor to its generated storage declaration without using
    /// reflection or runtime code generation.
    /// </summary>
    /// <remarks>
    /// Native AOT applications get the accessor registration and declaration from
    /// <c>Groundwork.Schema.Generator</c>. This entry point fails closed when that registration is
    /// absent instead of falling back to the reflection-based fluent declaration path.
    /// </remarks>
    public static RecordTable<T> FromGenerated<T>(KernelStorageUnit definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!GwGeneratedRows.TryGet<T>(out _))
        {
            throw new InvalidOperationException(
                $"Type '{typeof(T).FullName}' has no generated Groundwork record accessor. " +
                "Add Groundwork.Schema.Generator and annotate the row with [GwTable].");
        }

        return new RecordTable<T>(definition);
    }
}

/// <summary>Expression-based authoring state for a CLR row type.</summary>
public sealed class RecordTableBuilder<T>
{
    private readonly DeclarationState state;
    private readonly List<RecordReferenceBinding> references = [];
    private readonly List<MemberInfo> unsupportedMembers = [];

    [RequiresUnreferencedCode("Infers a declaration from the public members of the CLR row type.")]
    internal RecordTableBuilder(string name)
    {
        state = new DeclarationState(name, name);
        AddInferredColumns();
    }

    public RecordTableBuilder<T> Key<TKey>(Expression<Func<T, TKey>> selector)
    {
        state.SetKey(MemberNames(selector, allowComposite: true));
        return this;
    }

    /// <summary>Opts the unit into a system-owned Int64 optimistic-concurrency token.</summary>
    public RecordTableBuilder<T> OptimisticConcurrency(string tokenColumn = "version")
    {
        state.SetOptimisticConcurrency(tokenColumn);
        return this;
    }

    /// <summary>Alias for <see cref="OptimisticConcurrency"/>.</summary>
    public RecordTableBuilder<T> Optimistic(string tokenColumn = "version") =>
        OptimisticConcurrency(tokenColumn);

    public RecordTableBuilder<T> Column<TValue>(
        Expression<Func<T, TValue>> selector,
        Action<ColumnBuilder>? configure = null)
    {
        var member = SingleMember(selector);
        var columnName = GetColumnName(member);
        var column = state.Columns.Single(existing => string.Equals(existing.Name, columnName, StringComparison.Ordinal));
        var builder = new ColumnBuilder().InferNullable(column.IsNullable);
        builder.Apply(column);
        configure?.Invoke(builder);
        state.ReplaceColumn(builder.Build(columnName, column.Type));
        return this;
    }

    public RecordTableBuilder<T> UniqueIndex<TValue>(
        string name,
        Expression<Func<T, TValue>> selector)
    {
        state.AddIndex(name, [new IndexColumn(GetColumnName(SingleMember(selector)))], unique: true);
        return this;
    }

    public RecordTableBuilder<T> Index<TValue>(
        string name,
        Expression<Func<T, TValue>> selector,
        SortDirection direction = SortDirection.Ascending)
    {
        state.AddIndex(name, [new IndexColumn(GetColumnName(SingleMember(selector)), direction)], unique: false);
        return this;
    }

    /// <summary>
    /// Declares and binds one direct navigation to a target record table. The source columns are
    /// interpreted in the target table's declared key order.
    /// </summary>
    public RecordTableBuilder<T> Reference<TTarget, TColumns>(
        string name,
        Expression<Func<T, TTarget>> navigation,
        RecordTable<TTarget> target,
        Expression<Func<T, TColumns>> columns)
    {
        ArgumentNullException.ThrowIfNull(target);
        var navigationMember = SingleMember(navigation);
        if (MemberType(navigationMember) != typeof(TTarget))
            throw new ArgumentException("The navigation member type must exactly match the target record type.", nameof(navigation));
        if (references.Any(reference => string.Equals(reference.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException($"Record reference '{name}' is already bound.", nameof(name));

        state.AddReference(name, target.Definition, MemberNames(columns, allowComposite: true));
        references.Add(new RecordReferenceBinding(name, navigation, navigationMember, target));
        return this;
    }

    /// <summary>Declares a closed aggregation profile alongside this typed table.</summary>
    public RecordTableBuilder<T> Aggregate(
        string name,
        Action<Groundwork.Kernel.AggregationBuilder> configure)
    {
        if (configure is null)
            throw new ArgumentNullException(nameof(configure));
        state.AddAggregation(BuildAggregation(name, configure));
        return this;
    }

    [RequiresDynamicCode("Initializes a generated accessor or the managed compatibility accessor for the CLR row type.")]
    [RequiresUnreferencedCode("Completes a reflection-inferred record declaration whose CLR members may be trimmed.")]
    public RecordTable<T> Build(PortabilityValidationContext? context = null)
    {
        var unbound = unsupportedMembers.FirstOrDefault(member =>
            references.All(reference => reference.NavigationMember != member));
        if (unbound is not null)
            throw new NotSupportedException($"Member '{unbound.Name}' has unsupported type '{MemberType(unbound)}'; bind it as a declared record reference or remove it from the row type.");
        try
        {
            return new(state.Build(context), references);
        }
        catch (DeclarationBuildException exception)
        {
            throw DiagnosticsCompatibility.ToRecords(exception);
        }
    }

    [RequiresUnreferencedCode("Infers a declaration from the public members of the CLR row type.")]
    private void AddInferredColumns()
    {
        var count = 0;
        foreach (var member in typeof(T)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => member.MemberType is MemberTypes.Property or MemberTypes.Field)
            .OrderBy(member => member.MetadataToken))
        {
            var type = MemberType(member);
            if (!TryToPortableType(type, out var portableType))
            {
                unsupportedMembers.Add(member);
                continue;
            }
            var columnName = GetColumnName(member);
            var nullable = IsNullable(member, type);
            state.AddColumn(new ColumnBuilder().InferNullable(nullable).Build(columnName, portableType));
            count++;
        }

        if (count == 0)
            throw new ArgumentException($"'{typeof(T).FullName}' has no public instance columns.", nameof(T));
    }

    private static Groundwork.Kernel.AggregationProfile BuildAggregation(
        string name,
        Action<Groundwork.Kernel.AggregationBuilder> configure)
        => Groundwork.Kernel.AggregationProfile.Create(name, configure);

    private static IReadOnlyList<string> MemberNames<TKey>(
        Expression<Func<T, TKey>> selector,
        bool allowComposite)
    {
        if (selector is null)
            throw new ArgumentNullException(nameof(selector));
        var body = Unwrap(selector.Body);
        if (allowComposite && body is NewExpression composite)
            return Array.AsReadOnly(composite.Arguments.Select(argument => GetColumnName(SingleMember(argument))).ToArray());

        return [GetColumnName(SingleMember(body))];
    }

    private static MemberInfo SingleMember<TValue>(Expression<Func<T, TValue>> selector) =>
        SingleMember(selector?.Body ?? throw new ArgumentNullException(nameof(selector)));

    private static MemberInfo SingleMember(Expression expression)
    {
        expression = Unwrap(expression);
        if (expression is MemberExpression member && member.Expression is ParameterExpression)
            return member.Member;

        throw new ArgumentException("The selector must directly select a public property or field.", nameof(expression));
    }

    private static Expression Unwrap(Expression expression)
    {
        while (expression is UnaryExpression unary &&
            (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            expression = unary.Operand;

        return expression;
    }

    private static Type MemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => throw new ArgumentException("Only properties and fields can be declared.", nameof(member))
    };

    private static bool TryToPortableType(Type type, out PortableType portableType)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        portableType = type == typeof(string) ? PortableType.String
            : type == typeof(int) ? PortableType.Int32
            : type == typeof(long) ? PortableType.Int64
            : type == typeof(decimal) ? PortableType.Decimal
            : type == typeof(double) ? PortableType.Double
            : type == typeof(bool) ? PortableType.Boolean
            : type == typeof(DateTimeOffset) ? PortableType.DateTimeOffset
            : type == typeof(Guid) ? PortableType.Guid
            : type == typeof(byte[]) ? PortableType.Binary
            : type == typeof(object) ? PortableType.Json
            : default;
        return type == typeof(string) || type == typeof(int) || type == typeof(long) ||
            type == typeof(decimal) || type == typeof(double) || type == typeof(bool) || type == typeof(DateTimeOffset) ||
            type == typeof(Guid) || type == typeof(byte[]) || type == typeof(object);
    }

    private static bool IsNullable(MemberInfo member, Type type)
    {
        if (type.IsValueType)
            return Nullable.GetUnderlyingType(type) is not null;

        var nullable = ReadNullableAttribute(member) ?? ReadNullableAttribute(member.DeclaringType!);
        if (nullable.HasValue)
            return nullable.Value == 2;

        var context = ReadNullableContext(member.GetCustomAttributesData()) ??
            ReadNullableContext(member.DeclaringType!);
        return context != 1;
    }

    private static byte? ReadNullableAttribute(MemberInfo member) =>
        member.GetCustomAttributesData()
            .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.NullableAttribute")
            .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value)
            .Select(value => value switch
            {
                byte flag when flag is 1 or 2 => (byte?)flag,
                IReadOnlyCollection<CustomAttributeTypedArgument> flags when flags.Count > 0 && flags.First().Value is byte flag && flag is 1 or 2 => flag,
                _ => null
            })
            .FirstOrDefault();

    private static byte? ReadNullableContext(IEnumerable<CustomAttributeData> attributes) =>
        attributes
            .Where(attribute => attribute.AttributeType.FullName == "System.Runtime.CompilerServices.NullableContextAttribute")
            .Select(attribute => attribute.ConstructorArguments.FirstOrDefault().Value)
            .Select(value => value is byte flag ? (byte?)flag : null)
            .FirstOrDefault(value => value.HasValue);

    private static byte? ReadNullableContext(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            var context = ReadNullableContext(current.GetCustomAttributesData());
            if (context.HasValue)
                return context;
        }

        return ReadNullableContext(type.Assembly.GetCustomAttributesData());
    }

    private static string GetColumnName(MemberInfo member)
    {
        var name = member.Name;
        return name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}

internal static class ColumnBuilderExtensions
{
    public static void Apply(this ColumnBuilder builder, ColumnDefinition definition)
    {
        if (definition.MaxLength.HasValue)
            builder.MaxLength(definition.MaxLength.Value);
        if (definition.Precision.HasValue && definition.Scale.HasValue)
            builder.Precision(definition.Precision.Value, definition.Scale.Value);
        if (definition.Collation.HasValue)
            builder.Collation(definition.Collation.Value);
        if (definition.Default is not null)
            builder.Default(definition.Default.Value);
        if (definition.Generation == ColumnGeneration.ProviderSequence)
            builder.ProviderSequence();
    }
}
