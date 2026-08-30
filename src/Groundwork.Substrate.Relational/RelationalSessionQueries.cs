using System.Data.Common;
using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Executes the complete provider-neutral relational query lifecycle. A provider supplies its SQL
/// renderer, physical index catalog, value decoder, and explain-plan assertion.
/// </summary>
internal sealed class RelationalSessionQueries
{
    private readonly StorageUnit unit;
    private readonly StorageAccess access;
    private readonly DbConnection connection;
    private readonly RelationalQueryRenderer renderer;
    private readonly Func<IReadOnlyDictionary<string, string>> physicalIndexNames;
    private readonly Func<object, ColumnDefinition, object?> decode;
    private readonly Func<RelationalQueryCommand, QueryRenderOptions, RelationalExecution, ValueTask> assertExplainPlan;
    private readonly IProviderCommandObserver? observer;
    private readonly string operationPrefix;

    internal RelationalSessionQueries(
        StorageUnit unit,
        StorageAccess access,
        DbConnection connection,
        RelationalQueryRenderer renderer,
        Func<IReadOnlyDictionary<string, string>> physicalIndexNames,
        Func<object, ColumnDefinition, object?> decode,
        Func<RelationalQueryCommand, QueryRenderOptions, RelationalExecution, ValueTask> assertExplainPlan,
        IProviderCommandObserver? observer,
        string operationPrefix)
    {
        this.unit = unit;
        this.access = access;
        this.connection = connection;
        this.renderer = renderer;
        this.physicalIndexNames = physicalIndexNames;
        this.decode = decode;
        this.assertExplainPlan = assertExplainPlan;
        this.observer = observer;
        this.operationPrefix = operationPrefix;
    }

    internal async ValueTask<QueryMaterializedResult> Query(
        QueryRequest request,
        QueryRenderOptions? options,
        DbTransaction? transaction,
        RelationalExecution execution)
    {
        var prepared = RelationalSessionPolicy.PrepareQuery(
            unit,
            access,
            request,
            options,
            physicalIndexNames());
        var command = renderer.Render(prepared.ExecutionRequest, prepared.RenderOptions);
        Observe("query", command);
        var rows = await RelationalQueryResultReader.Read(
            connection,
            command,
            (name, value) => DecodeQueryValue(
                name,
                value,
                prepared.ExecutionSource,
                prepared.RenderOptions),
            transaction,
            execution).ConfigureAwait(false);
        await assertExplainPlan(command, prepared.RenderOptions, execution).ConfigureAwait(false);
        return QueryResultMaterializer.Materialize(
            prepared.ExecutionSource,
            prepared.RenderOptions,
            rows,
            command.SelectedIndex,
            command.IndexHintApplied,
            sourceIncludesRequestedOffset: true,
            sourceIncludesContinuation: true,
            sourceIncludesDistinct: true);
    }

    internal async ValueTask<CrossScopeQueryResult> QueryAcrossScopes(
        QueryRequest request,
        QueryRenderOptions? options,
        RelationalExecution execution)
    {
        var audit = StorageAccessValidation.BeginPrivilegedQuery(access, unit);
        CrossScopeQueryResult result;
        try
        {
            var prepared = RelationalSessionPolicy.PrepareCrossScopeQuery(
                unit,
                access,
                request,
                options,
                physicalIndexNames());
            var command = renderer.Render(prepared.ExecutionRequest, prepared.RenderOptions);
            Observe("query-across-scopes", command);
            var rows = await RelationalQueryResultReader.Read(
                connection,
                command,
                (name, value) =>
                {
                    if (name == "__groundwork_total_count")
                        return value;
                    var column = unit.Columns.FirstOrDefault(item => item.Name == name);
                    return column is null ? value : decode(value ?? DBNull.Value, column);
                },
                transaction: null,
                execution).ConfigureAwait(false);
            await assertExplainPlan(command, prepared.RenderOptions, execution).ConfigureAwait(false);
            var materialized = QueryResultMaterializer.Materialize(
                prepared.ExecutionSource,
                prepared.RenderOptions,
                rows,
                command.SelectedIndex,
                command.IndexHintApplied,
                sourceIncludesRequestedOffset: true,
                sourceIncludesContinuation: true);
            result = CrossScopeQueryMaterializer.FromNativePage(
                materialized,
                rows,
                ProviderOwnedColumns.Scope);
        }
        catch (Exception exception)
        {
            audit.Failure(exception);
            throw;
        }

        audit.Success();
        return result;
    }

    private object? DecodeQueryValue(
        string name,
        object? value,
        QueryRequest request,
        QueryRenderOptions options)
    {
        if (name == "__groundwork_total_count")
            return value;
        if (request.Result is ResultShape.Sum { Column.Type: QueryType.Int32 } sum &&
            string.Equals(name, sum.Column.Name, StringComparison.Ordinal))
        {
            return value is null
                ? null
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        var column = RelationalQueryResultReader.ResolveColumnDefinition(unit, request, options, name);
        return column is null ? value : decode(value ?? DBNull.Value, column);
    }

    private void Observe(string operation, RelationalQueryCommand command) =>
        observer?.Observe(new ProviderCommandEvent(
            operationPrefix + "." + operation,
            command.CommandText,
            ProviderCommandKind.Read,
            IsProbe: false));
}
