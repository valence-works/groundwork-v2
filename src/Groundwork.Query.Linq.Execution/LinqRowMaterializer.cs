using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

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

    internal static Func<IReadOnlyDictionary<string, object?>, T> For<T>(GwTableModel<T>? model) =>
        Materializers<T>.For(model);

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

        internal static Func<IReadOnlyDictionary<string, object?>, T> For(GwTableModel<T>? model) =>
            model is null
                ? inferred ??= Build(null)
                : ByModel.GetValue(model, static key => Build(key));

        private static Func<IReadOnlyDictionary<string, object?>, T> Build(GwTableModel<T>? model)
        {
            if (typeof(T) == typeof(IReadOnlyDictionary<string, object?>))
                return static row => (T)row;

            var row = Expression.Parameter(typeof(IReadOnlyDictionary<string, object?>), "row");
            var mappings = model is null
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

            return Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, T>>(
                Expression.MemberInit(Expression.New(typeof(T)), bindings),
                row).Compile();
        }
    }
}
