using Groundwork.Query.Model;
using Groundwork.Substrate.Relational;

namespace Groundwork.SqlServer;

/// <summary>SQL Server's one native renderer for the normalized v2 query contract.</summary>
public sealed class SqlServerQueryRenderer : RelationalQueryRenderer
{
    public SqlServerQueryRenderer()
        : base(new SqlServerDialect(), parameterBudget: 2_100, supportsIndexHints: true)
    {
    }

    protected override string ProviderName => "SQL Server";

    protected override string RenderCountExpression() => "COUNT_BIG(*) OVER()";

    protected override bool RequiresOrderForOffset => true;

    protected override string RenderIndexHint(string indexName) =>
        "WITH (INDEX(" + Dialect.QuoteIdentifier(indexName) + "))";

    protected override string RenderPaging(
        Paging paging,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (paging.Offset is null && paging.Limit is null)
            return string.Empty;

        var offset = paging.Offset is int suppliedOffset
            ? AddPagingParameter(parameters, ref parameterIndex, suppliedOffset)
            : "0";
        var text = " OFFSET @" + offset + " ROWS";
        if (paging.Limit is int limit)
            text += " FETCH NEXT @" + AddPagingParameter(parameters, ref parameterIndex, limit) + " ROWS ONLY";
        return text;
    }

    private static string AddPagingParameter(
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex,
        int value)
    {
        var name = "p" + parameterIndex++;
        parameters.Add(new QueryRenderParameter(name, QueryType.Int32, value));
        return name;
    }
}
