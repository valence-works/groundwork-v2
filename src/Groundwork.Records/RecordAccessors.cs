using System.Linq.Expressions;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Groundwork.Query.Linq;

namespace Groundwork.Records;

internal sealed class RecordAccessor<T>
{
    private static readonly RecordAccessor<T> cached = Create();

    private readonly Func<T, object?>[] getters;
    private readonly Func<IReadOnlyDictionary<string, object?>, IReadOnlySet<string>, T> materializer;

    private RecordAccessor(
        IReadOnlyList<RecordMember> members,
        Func<T, object?>[] getters,
        Func<IReadOnlyDictionary<string, object?>, IReadOnlySet<string>, T> materializer)
    {
        Members = members;
        this.getters = getters;
        this.materializer = materializer;
    }

    public IReadOnlyList<RecordMember> Members { get; }

    public static int CompilationCount { get; private set; }

    public static int ReflectionInspectionCount { get; private set; }

    public static int DynamicCodeGenerationCount => CompilationCount;

    public static RecordAccessor<T> Instance => cached;

    public RowValues Write(T value, IReadOnlyList<Groundwork.Kernel.ColumnDefinition> columns)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < Members.Count; index++)
        {
            var member = Members[index];
            if (columns.Any(column => string.Equals(column.Name, member.ColumnName, StringComparison.Ordinal)))
                result[member.ColumnName] = getters[index](value);
        }

        return new RowValues(result);
    }

    public T Read(RowValues values, IReadOnlySet<string>? optionalColumns = null) =>
        materializer(values.Values, optionalColumns ?? EmptyColumns.Instance);

    private static RecordAccessor<T> Create()
    {
        if (GwGeneratedRows.TryGet<T>(out var generated))
        {
            var generatedAccessor = generated!;
            var generatedMembers = generatedAccessor.Members
                .Select(member => new RecordMember(member.Name, member.ColumnName, member.MemberType, null))
                .ToArray();
            var generatedGetters = generatedAccessor.Members.Select(member => member.Getter).ToArray();
            var columns = generatedMembers.ToDictionary(member => member.Name, member => member.ColumnName, StringComparer.Ordinal);
            return new RecordAccessor<T>(
                generatedMembers,
                generatedGetters,
                (values, optional) => generatedAccessor.Materialize(values, columns, optional));
        }

        return CreateCompatibilityWhenAvailable();
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Native AOT refuses before this call; the managed preview fallback intentionally retains reflection compatibility.")]
    [UnconditionalSuppressMessage(
        "Aot",
        "IL3050",
        Justification = "RuntimeFeature.IsDynamicCodeSupported guards the managed-only compatibility accessor.")]
    private static RecordAccessor<T> CreateCompatibilityWhenAvailable()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            throw new NotSupportedException(
                $"Type '{typeof(T).FullName}' has no generated Groundwork record accessor. " +
                "Add Groundwork.Schema.Generator for Native AOT record mapping.");
        }

        return CreateCompatibility();
    }

    [RequiresDynamicCode("Builds and compiles a compatibility accessor for an ungenerated CLR row type.")]
    [RequiresUnreferencedCode("Reflects over an ungenerated CLR row type. Add Groundwork.Schema.Generator to use the trim-safe path.")]
    private static RecordAccessor<T> CreateCompatibility()
    {
        CompilationCount++;
        ReflectionInspectionCount++;
        var members = typeof(T)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(member => member.MemberType is MemberTypes.Property or MemberTypes.Field)
            .Select(member => member switch
            {
                PropertyInfo property when property.GetMethod is not null =>
                    new RecordMember(member.Name, LowerFirst(member.Name), property.PropertyType, member),
                FieldInfo field => new RecordMember(member.Name, LowerFirst(member.Name), field.FieldType, member),
                _ => null
            })
            .Where(member => member is not null)
            .Cast<RecordMember>()
            .OrderBy(member => member.Member!.MetadataToken)
            .ToArray();
        if (members.Length == 0)
            throw new ArgumentException($"'{typeof(T).FullName}' has no public instance columns.", nameof(T));

        var parameter = Expression.Parameter(typeof(T), "value");
        var getters = members.Select(member =>
        {
            var access = member.Member switch
            {
                PropertyInfo property => Expression.Property(parameter, property),
                FieldInfo field => Expression.Field(parameter, field),
                _ => throw new InvalidOperationException()
            };
            return Expression.Lambda<Func<T, object?>>(Expression.Convert(access, typeof(object)), parameter).Compile();
        }).ToArray();

        var materializer = BuildMaterializer(members);
        return new RecordAccessor<T>(members, getters, materializer);
    }

    [RequiresDynamicCode("Builds and compiles a compatibility accessor for an ungenerated CLR row type.")]
    [RequiresUnreferencedCode("Reflects over an ungenerated CLR row type.")]
    private static Func<IReadOnlyDictionary<string, object?>, IReadOnlySet<string>, T> BuildMaterializer(
        IReadOnlyList<RecordMember> members)
    {
        var source = Expression.Parameter(typeof(IReadOnlyDictionary<string, object?>), "values");
        var optional = Expression.Parameter(typeof(IReadOnlySet<string>), "optionalColumns");
        var constructor = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Select(candidate => new
            {
                Candidate = candidate,
                Parameters = candidate.GetParameters(),
                BoundMembers = candidate.GetParameters().Select(parameter =>
                    members.SingleOrDefault(member => string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))).ToArray()
            })
            .Where(candidate => candidate.BoundMembers.All(member => member is not null))
            .Where(candidate => candidate.Parameters.Zip(candidate.BoundMembers)
                .All(pair => pair.Second is not null && pair.First.ParameterType == pair.Second.MemberType))
            .Where(candidate => members.All(member =>
                candidate.BoundMembers.Contains(member) || IsWritable(member.Member!)))
            .OrderByDescending(candidate => candidate.Parameters.Length)
            .Select(candidate => candidate.Candidate)
            .FirstOrDefault();

        if (constructor is null && (!typeof(T).IsValueType || members.Any(member => !IsWritable(member.Member!))))
            throw new ArgumentException(
                $"'{typeof(T).FullName}' must expose a public constructor and/or writable public members that initialize every declared member.", nameof(T));

        var value = Expression.Variable(typeof(T), "value");
        var parameters = constructor?.GetParameters() ?? [];
        var bound = parameters.Select(parameter => members.Single(member =>
            string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))).ToHashSet();
        var arguments = parameters.Select(parameter =>
        {
            var member = members.Single(member =>
                string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
            return ReadAndConvert(source, optional, member.ColumnName, parameter.ParameterType);
        });
        var expressions = new List<Expression>
        {
            Expression.Assign(value, constructor is null
                ? Expression.New(typeof(T))
                : Expression.New(constructor, arguments))
        };
        foreach (var member in members)
        {
            if (bound.Contains(member))
                continue;
            var target = member.Member switch
            {
                PropertyInfo property when property.SetMethod?.IsPublic == true => Expression.Property(value, property),
                FieldInfo field when !field.IsInitOnly => Expression.Field(value, field),
                _ => null
            };
            if (target is not null)
                expressions.Add(Expression.Assign(target, ReadAndConvert(source, optional, member.ColumnName, member.MemberType)));
        }

        expressions.Add(value);
        return Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, IReadOnlySet<string>, T>>(
            Expression.Block(new[] { value }, expressions), source, optional).Compile();
    }

    [RequiresDynamicCode("Builds a compatibility conversion expression.")]
    [RequiresUnreferencedCode("Builds a compatibility conversion expression by member name.")]
    private static Expression ReadAndConvert(
        ParameterExpression source,
        ParameterExpression optional,
        string column,
        Type destination)
    {
        var read = Expression.Call(
            typeof(RecordAccessor<T>),
            nameof(ReadValue),
            Type.EmptyTypes,
            source,
            Expression.Constant(column));
        var nullable = Nullable.GetUnderlyingType(destination);
        Expression converted;
        if (nullable is null)
            converted = Expression.Convert(read, destination);
        else
        {
            var nullableValue = Expression.Convert(read, nullable);
            converted = Expression.Condition(
                Expression.Equal(read, Expression.Constant(null, typeof(object))),
                Expression.Constant(null, destination),
                Expression.Convert(nullableValue, destination));
        }

        return Expression.Condition(
            Expression.Call(source, nameof(IReadOnlyDictionary<string, object?>.ContainsKey), Type.EmptyTypes, Expression.Constant(column)),
            converted,
            Expression.Condition(
                Expression.Call(optional, nameof(IReadOnlySet<string>.Contains), Type.EmptyTypes, Expression.Constant(column)),
                Expression.Default(destination),
                Expression.Convert(Expression.Call(typeof(RecordAccessor<T>), nameof(ThrowMissing), Type.EmptyTypes, Expression.Constant(column)), destination)));
    }

    private static object? ReadValue(IReadOnlyDictionary<string, object?> values, string column)
    {
        if (!values.TryGetValue(column, out var value))
            throw new KeyNotFoundException($"The query result did not contain declared column '{column}'.");
        return value;
    }

    private static object ThrowMissing(string column) =>
        throw new KeyNotFoundException($"The query result did not contain declared column '{column}'.");

    private static bool IsWritable(MemberInfo member) => member switch
    {
        PropertyInfo property => property.SetMethod?.IsPublic == true,
        FieldInfo field => !field.IsInitOnly,
        _ => false
    };

    private static string LowerFirst(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

internal sealed class EmptyColumns : HashSet<string>
{
    public static EmptyColumns Instance { get; } = new();
    private EmptyColumns() : base(StringComparer.Ordinal) { }
}

internal sealed record RecordMember(string Name, string ColumnName, Type MemberType, MemberInfo? Member);
