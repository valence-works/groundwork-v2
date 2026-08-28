using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Groundwork.Query.Model;

namespace Groundwork.Query.Linq.Execution;

/// <summary>
/// Turns provider rows into mapped instances through a delegate compiled once per shape and cached,
/// so a page of ten thousand rows costs no member lookup and no <see cref="Activator"/> call. The
/// reflection is all in the build, never in the loop.
/// </summary>
internal static class LinqRowMaterializer
{
    private static readonly MethodInfo ReadMethod =
        typeof(LinqRowMaterializer).GetMethod(nameof(Read), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static Func<IReadOnlyDictionary<string, object?>, T> For<T>(GwTableModel<T>? model, Projection? projection = null) =>
        Materializers<T>.For(model, projection);

    /// <summary>Reads one mapped column, leaving the member at its default when the row omits it.</summary>
    private static TMember Read<TMember>(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (!row.TryGetValue(column, out var value) || value is null)
            return default!;
        if (value is TMember mapped)
            return mapped;
        var target = Nullable.GetUnderlyingType(typeof(TMember)) ?? typeof(TMember);
        return (TMember)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static class Materializers<T>
    {
        private static readonly ConditionalWeakTable<GwTableModel<T>, Func<IReadOnlyDictionary<string, object?>, T>> ByModel = new();
        private static Func<IReadOnlyDictionary<string, object?>, T>? inferred;

        internal static Func<IReadOnlyDictionary<string, object?>, T> For(GwTableModel<T>? model, Projection? projection) =>
            model is null
                ? projection is null
                    ? inferred ??= Build(null, null)
                    : Build(null, projection)
                : ByModel.GetValue(model, static key => Build(key, null));

        private static Func<IReadOnlyDictionary<string, object?>, T> Build(GwTableModel<T>? model, Projection? projection)
        {
            if (typeof(T) == typeof(IReadOnlyDictionary<string, object?>))
                return static row => (T)row;

            var row = Expression.Parameter(typeof(IReadOnlyDictionary<string, object?>), "row");
            if (projection is not null && projection.Columns.Length == 1 && IsScalarType(typeof(T)))
                return Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, T>>(
                    Expression.Call(ReadMethod.MakeGenericMethod(typeof(T)), row,
                        Expression.Constant(projection.Columns[0].Name, typeof(string))),
                    row).Compile();
            var mappings = model is null && projection is not null
                ? projection.Columns.Select((column, index) =>
                {
                    var member = typeof(T).GetMembers(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(candidate =>
                            (candidate is PropertyInfo or FieldInfo) &&
                            string.Equals(candidate.Name, column.Name, StringComparison.OrdinalIgnoreCase));
                    member ??= typeof(T).GetMembers(BindingFlags.Public | BindingFlags.Instance)
                        .Where(candidate => candidate is PropertyInfo { CanWrite: true } or FieldInfo { IsInitOnly: false })
                        .OrderBy(candidate => candidate.MetadataToken)
                        .ElementAtOrDefault(index);
                    return (Member: member?.Name ?? column.Name, Column: column.Name);
                })
                : model is null
                ? typeof(T)
                    .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                    .Where(member => member is PropertyInfo or FieldInfo)
                    .Select(member => (Member: member.Name, Column: member.Name))
                : model.Columns.Select(column => (Member: column.Key, Column: column.Value.Name));

            var bindings = new List<MemberBinding>();
            foreach (var (memberName, columnName) in mappings)
            {
                var member = typeof(T)
                    .GetMember(memberName, BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(candidate => candidate is PropertyInfo { CanWrite: true } or FieldInfo { IsInitOnly: false });
                if (member is null)
                    continue;
                var memberType = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
                bindings.Add(Expression.Bind(member, Expression.Call(
                    ReadMethod.MakeGenericMethod(memberType),
                    row,
                    Expression.Constant(columnName, typeof(string)))));
            }

            var constructor = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(candidate => candidate.GetParameters().Length != 0)
                .Select(candidate => new
                {
                    Constructor = candidate,
                    Arguments = candidate.GetParameters().Select((parameter, index) =>
                    {
                        var column = projection?.Columns.FirstOrDefault(item =>
                            string.Equals(item.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                        column ??= projection is not null && index < projection.Columns.Length
                            ? projection.Columns[index]
                            : null;
                        return column is null
                            ? null
                            : Expression.Call(ReadMethod.MakeGenericMethod(parameter.ParameterType), row,
                                Expression.Constant(column.Name, typeof(string)));
                    }).ToArray()
                })
                .FirstOrDefault(candidate => candidate.Arguments.All(argument => argument is not null));

            var defaultConstructor = typeof(T).GetConstructor(Type.EmptyTypes);
            if (constructor is not null && (bindings.Count == 0 || defaultConstructor is null))
            {
                return Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, T>>(
                    Expression.New(constructor.Constructor, constructor.Arguments!),
                    row).Compile();
            }

            if (defaultConstructor is null)
                throw new InvalidOperationException($"Type '{typeof(T).FullName}' requires a public constructor whose parameters match the query projection.");
            return Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, T>>(
                Expression.MemberInit(Expression.New(defaultConstructor), bindings),
                row).Compile();
        }

        private static bool IsScalarType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
                type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
                type == typeof(TimeSpan) || type == typeof(byte[]);
        }
    }
}
