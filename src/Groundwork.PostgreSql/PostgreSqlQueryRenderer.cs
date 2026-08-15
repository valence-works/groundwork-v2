using Groundwork.Substrate.Relational;
using Groundwork.Query.Model;

namespace Groundwork.PostgreSql;

/// <summary>PostgreSQL's one native renderer for the normalized v2 query contract.</summary>
public sealed class PostgreSqlQueryRenderer : RelationalQueryRenderer
{
    public PostgreSqlQueryRenderer()
        : base(new PostgreSqlDialect(), parameterBudget: 65_535, supportsIndexHints: false)
    {
    }

    protected override string ProviderName => "PostgreSQL";

    protected override object? AdaptParameter(QueryType type, object? value) => type == QueryType.DateTimeOffset && value is DateTimeOffset timestamp
        ? timestamp.ToUniversalTime().Ticks
        : value;
}
