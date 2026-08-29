using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Groundwork.Query.Linq;
using Groundwork.Query.Model;

namespace Groundwork.Query.Linq.Execution;

/// <summary>
/// Turns provider rows into mapped instances through a source-generated accessor when the CLR row
/// participates in the generated schema. Ungenerated preview models retain the cached compatibility
/// materializer; either way, a page does not rebuild the row shape in its per-row loop.
/// </summary>
internal static class LinqRowMaterializer
{
    private static int dynamicCodeGenerationCount;

    private static readonly MethodInfo ReadMethod =
        typeof(LinqRowMaterializer).GetMethod(nameof(Read), BindingFlags.NonPublic | BindingFlags.Static)!;

    internal static Func<IReadOnlyDictionary<string, object?>, T> For<T>(GwTableModel<T>? model, Projection? projection = null) =>
        Materializers<T>.For(model, projection);

    internal static int DynamicCodeGenerationCount => Volatile.Read(ref dynamicCodeGenerationCount);

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

            var projectionColumns = projection is { AllColumns: false }
                ? projection.Columns.Select(column => column.Name).ToArray()
                : null;
            if (projectionColumns is { Length: 1 } && IsScalarType(typeof(T)))
                return row => GwGeneratedRowValue.ReadProjection<T>(row, projectionColumns, 0);

            if (projectionColumns is not null &&
                GwGeneratedRows.TryGetProjection<T>(projectionColumns.Length, out var generatedProjection))
            {
                var materializeProjection = generatedProjection!;
                return row => materializeProjection(row, projectionColumns);
            }

            if (GwGeneratedRows.TryGet<T>(out var generated))
            {
                var generatedAccessor = generated!;
                var columns = model is not null
                    ? model.Columns.ToDictionary(column => column.Key, column => column.Value.Name, StringComparer.Ordinal)
                    : projection is null or { AllColumns: true }
                        ? generatedAccessor.Members.ToDictionary(
                            member => member.Name,
                            member => member.ColumnName,
                            StringComparer.Ordinal)
                    : generatedAccessor.Members.Select((member, index) => new
                        {
                            member.Name,
                            Column = projection?.Columns.FirstOrDefault(column =>
                                string.Equals(column.Name, member.Name, StringComparison.OrdinalIgnoreCase))?.Name
                                ?? projection?.Columns.ElementAtOrDefault(index)?.Name
                        })
                        .Where(item => item.Column is not null)
                        .ToDictionary(item => item.Name, item => item.Column!, StringComparer.Ordinal);
                var optionalColumns = columns.Values.ToHashSet(StringComparer.Ordinal);
                return row => generatedAccessor.Materialize(row, columns, optionalColumns);
            }

            Interlocked.Increment(ref dynamicCodeGenerationCount);

            var row = Expression.Parameter(typeof(IReadOnlyDictionary<string, object?>), "row");
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
            var mappingArray = mappings.ToArray();

            var bindings = new List<MemberBinding>();
            foreach (var (memberName, columnName) in mappingArray)
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
                    Parameters = candidate.GetParameters().Select((parameter, index) =>
                    {
                        var column = projection?.Columns.FirstOrDefault(item =>
                            string.Equals(item.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                        column ??= projection is not null && index < projection.Columns.Length
                            ? projection.Columns[index]
                            : null;
                        var member = column is null
                            ? null
                            : mappingArray.FirstOrDefault(item =>
                                string.Equals(item.Column, column.Name, StringComparison.OrdinalIgnoreCase)).Member;
                        return new
                        {
                            Column = column,
                            Member = member,
                            Argument = column is null
                                ? null
                                : Expression.Call(ReadMethod.MakeGenericMethod(parameter.ParameterType), row,
                                    Expression.Constant(column.Name, typeof(string)))
                        };
                    }).ToArray()
                })
                .Select(candidate => new
                {
                    candidate.Constructor,
                    candidate.Parameters,
                    Arguments = candidate.Parameters.Select(parameter => parameter.Argument).ToArray()
                })
                .FirstOrDefault(candidate => candidate.Arguments.All(argument => argument is not null));

            var defaultConstructor = typeof(T).GetConstructor(Type.EmptyTypes);
            if (constructor is not null && (bindings.Count == 0 || defaultConstructor is null))
            {
                var constructorMembers = constructor.Parameters
                    .Select(parameter => parameter.Member)
                    .Where(member => member is not null)
                    .ToHashSet(StringComparer.Ordinal);
                var remainingBindings = bindings
                    .Where(binding => !constructorMembers.Contains(binding.Member.Name))
                    .ToArray();
                var created = Expression.New(constructor.Constructor, constructor.Arguments!);
                Expression body = remainingBindings.Length == 0
                    ? created
                    : Expression.MemberInit(created, remainingBindings);
                return Expression.Lambda<Func<IReadOnlyDictionary<string, object?>, T>>(
                    body,
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
