using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Prepares and executes provider-neutral set mutations. Providers retain only command creation and
/// typed assignment binding.
/// </summary>
internal sealed class RelationalSessionSetMutations
{
    private readonly StorageUnit unit;
    private readonly StorageAccess access;
    private readonly RelationalQueryRenderer renderer;
    private readonly string? versionColumn;
    private readonly Func<string, DbCommand> createCommand;
    private readonly Action<DbCommand, string, object?, ColumnDefinition> bindAssignment;
    private readonly IProviderCommandObserver? observer;
    private readonly string operationPrefix;

    internal RelationalSessionSetMutations(
        StorageUnit unit,
        StorageAccess access,
        RelationalQueryRenderer renderer,
        string? versionColumn,
        Func<string, DbCommand> createCommand,
        Action<DbCommand, string, object?, ColumnDefinition> bindAssignment,
        IProviderCommandObserver? observer,
        string operationPrefix)
    {
        this.unit = unit;
        this.access = access;
        this.renderer = renderer;
        this.versionColumn = versionColumn;
        this.createCommand = createCommand;
        this.bindAssignment = bindAssignment;
        this.observer = observer;
        this.operationPrefix = operationPrefix;
    }

    internal Func<RelationalExecution, ValueTask<SetMutationResult>> PrepareUpdateWhere(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments)
    {
        ArgumentNullException.ThrowIfNull(where);
        SetMutationExecutionAdmission.Require(where);
        var physical = SetMutationValidation.ValidateAndPhysicalizeAssignments(unit, assignments);
        var columns = physical.Keys.OrderBy(column => column, StringComparer.Ordinal).ToArray();
        return async execution =>
        {
            var rendered = renderer.RenderUpdateWhere(
                unit.Name,
                Scoped(where),
                columns,
                versionColumn);
            using var command = createCommand(rendered.CommandText);
            RelationalQueryResultReader.AddParameters(command, rendered);
            for (var index = 0; index < columns.Length; index++)
            {
                var column = unit.Columns.First(item => item.Name == columns[index]);
                bindAssignment(
                    command,
                    rendered.AssignmentParameters[index],
                    physical[column.Name],
                    column);
            }
            Observe("update-where", rendered.CommandText);
            return new SetMutationResult(await execution.ExecuteNonQuery(command).ConfigureAwait(false));
        };
    }

    internal Func<RelationalExecution, ValueTask<SetMutationResult>> PrepareDeleteWhere(Predicate where)
    {
        ArgumentNullException.ThrowIfNull(where);
        SetMutationExecutionAdmission.Require(where);
        return async execution =>
        {
            var rendered = renderer.RenderDeleteWhere(unit.Name, Scoped(where));
            using var command = createCommand(rendered.CommandText);
            RelationalQueryResultReader.AddParameters(command, rendered);
            Observe("delete-where", rendered.CommandText);
            return new SetMutationResult(await execution.ExecuteNonQuery(command).ConfigureAwait(false));
        };
    }

    private Predicate Scoped(Predicate where) => RelationalSetMutation.WithScope(
        where,
        unit.Name,
        unit.Columns.Any(column => column.Name == ProviderOwnedColumns.Scope)
            ? ProviderOwnedColumns.Scope
            : null,
        access.Scope?.Value);

    private void Observe(string operation, string commandText) =>
        observer?.Observe(new ProviderCommandEvent(
            operationPrefix + "." + operation,
            commandText,
            ProviderCommandKind.Write,
            IsProbe: false));
}
