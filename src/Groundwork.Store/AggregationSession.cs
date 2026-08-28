using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>Shared provider adapter for the closed aggregation contract.</summary>
public static class AggregationSessionExecutor
{
    public static AggregationResult Execute(IStorageSession session, AggregationQuery query)
    {
        ArgumentNullException.ThrowIfNull(session);
        var result = Execute(session.Unit, request => session.Query(request), query);
        if (session.Access.Scope is not StorageScope scope)
            return result;

        var profile = AggregationProfileValidator.ResolveOrThrow(session.Unit, query);
        return new AggregationResult(
            result.Rows,
            result.ShapeFingerprint,
            AggregationQueryFingerprint.Create(session.Unit, profile, query, scope));
    }

    /// <summary>Adapter overload for provider-native session contracts.</summary>
    public static AggregationResult Execute(
        StorageUnit unit,
        Func<QueryRequest, QueryMaterializedResult> queryRows,
        AggregationQuery query)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(queryRows);
        ArgumentNullException.ThrowIfNull(query);
        var profile = AggregationProfileValidator.ResolveOrThrow(unit, query);
        AggregationProfileValidator.Validate(unit, profile);
        // Admit the complete query before handing its predicate to a provider renderer. This keeps
        // the closed aggregation surface's refusal codes provider-neutral (for example, a
        // StartsWith source predicate must not reach a provider's search-key renderer first).
        AggregationExecutor.ValidateQuery(unit, profile, query);
        var groups = AggregationGrouping.EffectiveGroups(profile);
        var requiredColumns = groups
            .Select(AggregationGrouping.SourceColumn)
            .Concat(profile.Aggregates.SelectMany(aggregate => aggregate switch
            {
                Aggregate.Min min => [min.Column],
                Aggregate.Max max => [max.Column],
                Aggregate.Sum sum => [sum.Column],
                Aggregate.SetUnion set => [set.Column],
                Aggregate.FirstBy first => [first.Column, first.OrderColumn],
                Aggregate.Count => Array.Empty<string>(),
                _ => Array.Empty<string>()
            }))
            .Concat(unit.Key.Columns)
            .ToHashSet(StringComparer.Ordinal);
        var columns = unit.Columns
            .Where(column => requiredColumns.Contains(column.Name))
            .ToDictionary(column => column.Name, QueryColumn, StringComparer.Ordinal);
        var order = unit.Key.Columns
            .Where(columns.ContainsKey)
            .Select(name => new OrderTerm(columns[name], OrderDirection.Ascending, NullOrder.First))
            .ToImmutableArray();
        var probeLimit = profile.MaxInputRows == int.MaxValue ? int.MaxValue : profile.MaxInputRows + 1;
        var request = new QueryRequest(
            new TableId(unit.Name),
            AggregationGrouping.EffectiveSourcePredicate(unit, profile, query),
            order,
            Projection.All,
            Paging.OffsetLimit(0, probeLimit));
        var source = queryRows(request);
        return AggregationExecutor.Execute(unit, profile, source.Rows, query);
    }

    private static ColumnRef QueryColumn(ColumnDefinition column) => new(
        column.Name,
        column.Type switch
        {
            PortableType.Boolean => QueryType.Boolean,
            PortableType.Int32 => QueryType.Int32,
            PortableType.Int64 => QueryType.Int64,
            PortableType.Decimal => QueryType.Decimal,
            PortableType.String => QueryType.String,
            PortableType.DateTimeOffset => QueryType.DateTimeOffset,
            PortableType.Guid => QueryType.Guid,
            PortableType.Binary => QueryType.Binary,
            _ => throw new AggregationValidationException([new(
                "GW-AGG-TYPE-004",
                $"Column '{column.Name}' cannot be used by the aggregation scan.",
                "columns")])
        },
        column.IsNullable,
        column.MaxLength,
        (byte?)column.Precision,
        (byte?)column.Scale);
}
