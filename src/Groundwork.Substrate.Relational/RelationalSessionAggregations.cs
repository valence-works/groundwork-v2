using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Store;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Executes the provider-neutral relational aggregation lifecycle while the provider supplies its
/// SQL dialect and physical value decoder.
/// </summary>
internal sealed class RelationalSessionAggregations
{
    private readonly StorageUnit unit;
    private readonly StorageAccess access;
    private readonly DbConnection connection;
    private readonly RelationalDialect dialect;
    private readonly Func<object, ColumnDefinition, object?> decode;
    private readonly IProviderCommandObserver? observer;
    private readonly string observerOperation;

    internal RelationalSessionAggregations(
        StorageUnit unit,
        StorageAccess access,
        DbConnection connection,
        RelationalDialect dialect,
        Func<object, ColumnDefinition, object?> decode,
        IProviderCommandObserver? observer,
        string observerOperation)
    {
        this.unit = unit;
        this.access = access;
        this.connection = connection;
        this.dialect = dialect;
        this.decode = decode;
        this.observer = observer;
        this.observerOperation = observerOperation;
    }

    internal ValueTask<AggregationResult> Aggregate(
        AggregationQuery query,
        DbTransaction? transaction,
        RelationalExecution execution)
    {
        ArgumentNullException.ThrowIfNull(query);
        StorageAccessValidation.EnsurePointOperation(access, "aggregate");
        var profile = AggregationProfileValidator.ResolveOrThrow(unit, query);
        object? Decode(string name, object? value)
        {
            var column = unit.Columns.FirstOrDefault(item => item.Name == name);
            return column is null ? value : decode(value ?? DBNull.Value, column);
        }

        return unit.Scope == ScopePolicy.Scoped
            ? RelationalAggregationExecutor.ExecuteScoped(
                connection,
                transaction,
                dialect,
                unit,
                profile,
                query,
                Decode,
                ProviderOwnedColumns.Scope,
                access.Scope!,
                execution,
                observer,
                observerOperation)
            : RelationalAggregationExecutor.Execute(
                connection,
                transaction,
                dialect,
                unit,
                profile,
                query,
                Decode,
                execution,
                observer,
                observerOperation);
    }
}
