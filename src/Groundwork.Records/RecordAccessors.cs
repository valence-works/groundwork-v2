using System.Linq.Expressions;
using System.Reflection;

namespace Groundwork.Records;

internal sealed class RecordAccessor<T>
{
    private static readonly RecordAccessor<T> cached = Create();

    private readonly Func<T, object?>[] getters;
    private readonly Func<IReadOnlyDictionary<string, object?>, T> materializer;

    private RecordAccessor(
        IReadOnlyList<RecordMember> members,
        Func<T, object?>[] getters,
        Func<IReadOnlyDictionary<string, object?>, T> materializer)
    {
        Members = members;
        this.getters = getters;
        this.materializer = materializer;
    }

    public IReadOnlyList<RecordMember> Members { get; }

    public static int CompilationCount { get; private set; }

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

    public T Read(RowValues values) => materializer(values.Values);

    private static RecordAccessor<T> Create()
    {
        CompilationCount++;
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
            .OrderBy(member => member.Member.MetadataToken)
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

    private static Func<IReadOnlyDictionary<string, object?>, T> BuildMaterializer(
        IReadOnlyList<RecordMember> members)
    {
        var source = Expression.Parameter(typeof(IReadOnlyDictionary<string, object?>), "values");
        var constructor = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Select(candidate => new
            {
                Candidate = candidate,
                Parameters = candidate.GetParameters(),
                Score = candidate.GetParameters().Count(parameter =>
                    members.Any(member => string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)))
            })
            .Where(candidate => candidate.Parameters.All(parameter =>
                members.Any(member => string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Parameters.Length)
            .Select(candidate => candidate.Candidate)
            .FirstOrDefault();

        if (constructor is not null)
        {
            var arguments = constructor.GetParameters().Select(parameter =>
            {
                var member = members.Single(member =>
                    string.Equals(member.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                return ReadAndConvert(source, member.ColumnName, parameter.ParameterType);
            });
            return Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, T>>(
                Expression.New(constructor, arguments), source).Compile();
        }

        var value = Expression.Variable(typeof(T), "value");
        var expressions = new List<Expression> { Expression.Assign(value, Expression.New(typeof(T))) };
        foreach (var member in members)
        {
            var target = member.Member switch
            {
                PropertyInfo property when property.SetMethod is not null => Expression.Property(value, property),
                FieldInfo field when !field.IsInitOnly => Expression.Field(value, field),
                _ => null
            };
            if (target is not null)
                expressions.Add(Expression.Assign(target, ReadAndConvert(source, member.ColumnName, member.MemberType)));
        }

        if (expressions.Count == 1)
            throw new ArgumentException(
                $"'{typeof(T).FullName}' must expose a public constructor or writable public members.", nameof(T));

        expressions.Add(value);
        return Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, T>>(
            Expression.Block(new[] { value }, expressions), source).Compile();
    }

    private static Expression ReadAndConvert(
        ParameterExpression source,
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
        if (nullable is null)
            return Expression.Convert(read, destination);

        var converted = Expression.Convert(read, nullable);
        return Expression.Condition(
            Expression.Equal(read, Expression.Constant(null, typeof(object))),
            Expression.Constant(null, destination),
            Expression.Convert(converted, destination));
    }

    private static object? ReadValue(IReadOnlyDictionary<string, object?> values, string column)
    {
        if (!values.TryGetValue(column, out var value))
            throw new KeyNotFoundException($"The query result did not contain declared column '{column}'.");
        return value;
    }

    private static string LowerFirst(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

internal sealed record RecordMember(string Name, string ColumnName, Type MemberType, MemberInfo Member);
