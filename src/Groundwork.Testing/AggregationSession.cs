using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Testing;

/// <summary>Shared provider adapter for the closed aggregation contract.</summary>
public static class AggregationSessionExecutor
{
    public static AggregationResult Execute(IStorageSession session, AggregationQuery query)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Execute(session.Unit, request => session.Query(request), query);
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
        var profile = unit.AggregationProfiles
            .SingleOrDefault(candidate => string.Equals(candidate.Name, query.ProfileName, StringComparison.Ordinal));
        if (profile is null)
            throw new AggregationValidationException([new(
                "GW-AGG-QUERY-004",
                $"Aggregation profile '{query.ProfileName}' is not declared by storage unit '{unit.Name}'.",
                "profileName")]);

        AggregationProfileValidator.Validate(unit, profile);
        var columns = unit.Columns.ToDictionary(column => column.Name, QueryColumn, StringComparer.Ordinal);
        var order = unit.Key.Columns
            .Where(columns.ContainsKey)
            .Select(name => new OrderTerm(columns[name], OrderDirection.Ascending, NullOrder.First))
            .ToImmutableArray();
        var request = new QueryRequest(
            new TableId(unit.Name),
            Predicate.AlwaysTrue.Instance,
            order,
            Projection.All,
            Paging.OffsetLimit(0, checked(profile.MaxInputRows + 1)));
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
