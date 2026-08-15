using System.Linq.Expressions;
using System.Reflection;
using Groundwork.Kernel;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;
using DeclarationState = Groundwork.Kernel.StorageDeclarationState;

namespace Groundwork.Records;

/// <summary>Starts a strongly typed storage declaration.</summary>
public static class RecordTable
{
    public static RecordTableBuilder<T> For<T>(string name) => new(name);
}

/// <summary>A built typed authoring result whose Definition contains only kernel declarations.</summary>
public sealed class RecordTable<T>
{
    internal RecordTable(KernelStorageUnit definition) => Definition = definition;

    public KernelStorageUnit Definition { get; }
}

/// <summary>Expression-based authoring state for a CLR row type.</summary>
public sealed class RecordTableBuilder<T>
{
    private readonly DeclarationState state;

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

    public RecordTable<T> Build(PortabilityValidationContext? context = null)
    {
        try
        {
            return new(state.Build(context));
        }
        catch (DeclarationBuildException exception)
        {
            throw DiagnosticsCompatibility.ToRecords(exception);
        }
    }

    private void AddInferredColumns()
    {
        var count = 0;
        foreach (var member in typeof(T)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => member.MemberType is MemberTypes.Property or MemberTypes.Field)
            .OrderBy(member => member.MetadataToken))
        {
            var type = MemberType(member);
            var portableType = ToPortableType(type, member);
            var columnName = GetColumnName(member);
            var nullable = IsNullable(member, type);
            state.AddColumn(new ColumnBuilder().InferNullable(nullable).Build(columnName, portableType));
            count++;
        }

        if (count == 0)
            throw new ArgumentException($"'{typeof(T).FullName}' has no public instance columns.", nameof(T));
    }

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

    private static PortableType ToPortableType(Type type, MemberInfo member)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return PortableType.String;
        if (type == typeof(int)) return PortableType.Int32;
        if (type == typeof(long)) return PortableType.Int64;
        if (type == typeof(decimal)) return PortableType.Decimal;
        if (type == typeof(bool)) return PortableType.Boolean;
        if (type == typeof(DateTimeOffset)) return PortableType.DateTimeOffset;
        if (type == typeof(Guid)) return PortableType.Guid;
        if (type == typeof(byte[])) return PortableType.Binary;
        if (type == typeof(object)) return PortableType.Json;

        throw new NotSupportedException($"Member '{member.Name}' has unsupported type '{type}'.");
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
