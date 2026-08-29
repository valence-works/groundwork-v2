using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Groundwork.Query.Linq;

/// <summary>One compile-time generated member binding for a CLR row type.</summary>
public sealed class GwGeneratedRowMember<T>
{
    public GwGeneratedRowMember(string name, string columnName, Type memberType, Func<T, object?> getter)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A member name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(columnName)) throw new ArgumentException("A column name is required.", nameof(columnName));
        Name = name;
        ColumnName = columnName;
        MemberType = memberType ?? throw new ArgumentNullException(nameof(memberType));
        Getter = getter ?? throw new ArgumentNullException(nameof(getter));
    }

    public string Name { get; }
    public string ColumnName { get; }
    public Type MemberType { get; }
    public Func<T, object?> Getter { get; }
}

/// <summary>
/// Compile-time generated accessors for one CLR row type. Applications normally receive this
/// registration from <c>Groundwork.Schema.Generator</c>; they do not construct it themselves.
/// </summary>
public sealed class GwGeneratedRowAccessor<T>
{
    private readonly Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, string>, IReadOnlyCollection<string>, T> materializer;

    public GwGeneratedRowAccessor(
        IEnumerable<GwGeneratedRowMember<T>> members,
        Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, string>, IReadOnlyCollection<string>, T> materializer)
    {
        var supplied = (members ?? throw new ArgumentNullException(nameof(members))).ToArray();
        if (supplied.Length == 0) throw new ArgumentException("At least one generated row member is required.", nameof(members));
        Members = new ReadOnlyCollection<GwGeneratedRowMember<T>>(supplied);
        this.materializer = materializer ?? throw new ArgumentNullException(nameof(materializer));
    }

    public IReadOnlyList<GwGeneratedRowMember<T>> Members { get; }

    public T Materialize(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, string> columns,
        IReadOnlyCollection<string>? optionalColumns = null) =>
        materializer(
            values ?? throw new ArgumentNullException(nameof(values)),
            columns ?? throw new ArgumentNullException(nameof(columns)),
            optionalColumns ?? GwGeneratedRows.EmptyColumns);
}

/// <summary>Process-wide registry populated by generated module initializers.</summary>
public static class GwGeneratedRows
{
    private static readonly ConcurrentDictionary<Type, object> Accessors = new();
    private static readonly ConcurrentDictionary<ProjectionKey, object> Projections = new();
    private static readonly object AmbiguousProjection = new();

    internal static IReadOnlyCollection<string> EmptyColumns { get; } = new HashSet<string>(StringComparer.Ordinal);

    public static void Register<T>(GwGeneratedRowAccessor<T> accessor)
    {
        if (accessor is null) throw new ArgumentNullException(nameof(accessor));
        Accessors.TryAdd(typeof(T), accessor);
    }

    public static bool TryGet<T>(out GwGeneratedRowAccessor<T>? accessor)
    {
        if (Accessors.TryGetValue(typeof(T), out var candidate) && candidate is GwGeneratedRowAccessor<T> typed)
        {
            accessor = typed;
            return true;
        }

        accessor = null;
        return false;
    }

    /// <summary>
    /// Registers a compile-time generated materializer for one projected result shape. Applications
    /// normally receive this registration from <c>Groundwork.Schema.Generator</c>. Registering a
    /// second factory for the same result type and column count marks that shape ambiguous so callers
    /// can use their compatibility materializer instead of selecting a factory nondeterministically.
    /// </summary>
    public static void RegisterProjection(
        Type resultType,
        int columnCount,
        Func<IReadOnlyDictionary<string, object?>, IReadOnlyList<string>, object?> materializer)
    {
        if (resultType is null) throw new ArgumentNullException(nameof(resultType));
        if (columnCount <= 0) throw new ArgumentOutOfRangeException(nameof(columnCount));
        if (materializer is null) throw new ArgumentNullException(nameof(materializer));
        Projections.AddOrUpdate(
            new ProjectionKey(resultType, columnCount),
            materializer,
            static (_, _) => AmbiguousProjection);
    }

    /// <summary>Finds the generated materializer for a projected result type and column count.</summary>
    public static bool TryGetProjection<T>(
        int columnCount,
        out Func<IReadOnlyDictionary<string, object?>, IReadOnlyList<string>, T>? materializer)
    {
        if (Projections.TryGetValue(new ProjectionKey(typeof(T), columnCount), out var value) &&
            value is Func<IReadOnlyDictionary<string, object?>, IReadOnlyList<string>, object?> registered)
        {
            materializer = (values, columns) => (T)registered(values, columns)!;
            return true;
        }

        materializer = null;
        return false;
    }

    private readonly struct ProjectionKey : IEquatable<ProjectionKey>
    {
        internal ProjectionKey(Type type, int columnCount) => (Type, ColumnCount) = (type, columnCount);
        private Type Type { get; }
        private int ColumnCount { get; }
        public bool Equals(ProjectionKey other) => Type == other.Type && ColumnCount == other.ColumnCount;
        public override bool Equals(object? obj) => obj is ProjectionKey other && Equals(other);
        public override int GetHashCode() => (Type.GetHashCode() * 397) ^ ColumnCount;
    }
}

/// <summary>Conversion helpers used by generated materializers.</summary>
public static class GwGeneratedRowValue
{
    /// <summary>Reads and converts one projected value by its compile-time ordinal.</summary>
    public static T ReadProjection<T>(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<string> columns,
        int index)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (columns is null) throw new ArgumentNullException(nameof(columns));
        if ((uint)index >= (uint)columns.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var column = columns[index];
        if (!values.TryGetValue(column, out var value))
            throw new KeyNotFoundException($"The query result did not contain projected column '{column}'.");
        return ConvertValue<T>(value);
    }

    public static T Read<T>(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, string> columns,
        IReadOnlyCollection<string> optionalColumns,
        string member,
        string defaultColumn)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (columns is null) throw new ArgumentNullException(nameof(columns));
        if (optionalColumns is null) throw new ArgumentNullException(nameof(optionalColumns));
        if (!columns.TryGetValue(member, out var column))
        {
            if (optionalColumns.Contains(defaultColumn)) return default!;
            throw new KeyNotFoundException($"No generated column mapping was supplied for member '{member}'.");
        }

        if (!values.TryGetValue(column, out var value))
        {
            if (optionalColumns.Contains(column) || optionalColumns.Contains(defaultColumn)) return default!;
            throw new KeyNotFoundException($"The query result did not contain declared column '{column}'.");
        }

        return ConvertValue<T>(value);
    }

    public static T ConvertValue<T>(object? value)
    {
        if (value is null) return default!;
        if (value is T typed) return typed;

        var target = typeof(T);
        if (target == typeof(int)) return (T)(object)ToInt32(value);
        if (target == typeof(int?)) return (T)(object)(int?)ToInt32(value);
        if (target == typeof(long)) return (T)(object)ToInt64(value);
        if (target == typeof(long?)) return (T)(object)(long?)ToInt64(value);
        if (target == typeof(decimal)) return (T)(object)ToDecimal(value);
        if (target == typeof(decimal?)) return (T)(object)(decimal?)ToDecimal(value);
        if (target == typeof(bool) && value is bool boolean) return (T)(object)boolean;
        if (target == typeof(bool?) && value is bool nullableBoolean) return (T)(object)(bool?)nullableBoolean;
        if (target == typeof(Guid) && value is string guid) return (T)(object)Guid.Parse(guid);
        if (target == typeof(Guid?) && value is string nullableGuid) return (T)(object)(Guid?)Guid.Parse(nullableGuid);
        if (target == typeof(DateTimeOffset) && value is string timestamp)
            return (T)(object)DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (target == typeof(DateTimeOffset?) && value is string nullableTimestamp)
            return (T)(object)(DateTimeOffset?)DateTimeOffset.Parse(nullableTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        throw new InvalidCastException($"Value of type '{value.GetType().FullName}' cannot be materialized as '{target.FullName}'.");
    }

    private static int ToInt32(object value) => value switch
    {
        byte number => number,
        sbyte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        long number => checked((int)number),
        uint number => checked((int)number),
        ulong number => checked((int)number),
        decimal number => checked((int)number),
        _ => throw new InvalidCastException($"Value of type '{value.GetType().FullName}' is not an Int32.")
    };

    private static long ToInt64(object value) => value switch
    {
        byte number => number,
        sbyte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number => number,
        ulong number => checked((long)number),
        decimal number => checked((long)number),
        _ => throw new InvalidCastException($"Value of type '{value.GetType().FullName}' is not an Int64.")
    };

    private static decimal ToDecimal(object value) => value switch
    {
        byte number => number,
        sbyte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number => number,
        ulong number => number,
        float number => (decimal)number,
        double number => (decimal)number,
        decimal number => number,
        _ => throw new InvalidCastException($"Value of type '{value.GetType().FullName}' is not a Decimal.")
    };
}
