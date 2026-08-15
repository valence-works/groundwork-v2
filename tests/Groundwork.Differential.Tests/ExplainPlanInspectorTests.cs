using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using MongoDB.Bson;
using Xunit;

namespace Groundwork.Differential.Tests;

public sealed class ExplainPlanInspectorTests
{
    [Fact]
    public void PostgreSql_requires_an_index_or_index_only_scan_with_the_exact_name()
    {
        const string plan = """[{"Plan":{"Node Type":"Sort","Plans":[{"Node Type":"Index Only Scan","Index Name":"ix_expected"}]}}]""";
        Assert.True(PostgreSqlExplainPlanInspector.ChoseIndex(plan, "ix_expected"));
        Assert.False(PostgreSqlExplainPlanInspector.ChoseIndex(plan, "ix_other"));
        Assert.False(PostgreSqlExplainPlanInspector.ChoseIndex("""[{"Plan":{"Node Type":"Bitmap Index Scan","Index Name":"ix_expected"}}]""", "ix_expected"));
        Assert.False(PostgreSqlExplainPlanInspector.ChoseIndex(string.Empty, "ix_expected"));
    }

    [Fact]
    public void SqlServer_requires_an_index_seek_with_the_exact_name()
    {
        const string plan = """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan"><BatchSequence><RelOp PhysicalOp="Index Seek"><IndexScan><Object Index="[ix_expected]" /></IndexScan></RelOp></BatchSequence></ShowPlanXML>""";
        Assert.True(SqlServerExplainPlanInspector.ChoseIndex(plan, "ix_expected"));
        Assert.False(SqlServerExplainPlanInspector.ChoseIndex(plan, "ix_other"));
        Assert.False(SqlServerExplainPlanInspector.ChoseIndex(plan.Replace("Index Seek", "Index Scan", StringComparison.Ordinal), "ix_expected"));
        Assert.False(SqlServerExplainPlanInspector.ChoseIndex(string.Empty, "ix_expected"));
    }

    [Fact]
    public void Sqlite_requires_using_the_exact_index()
    {
        const string plan = "SEARCH things USING COVERING INDEX ix_expected (numberValue=?)";
        Assert.True(SqliteExplainPlanInspector.ChoseIndex(plan, "ix_expected"));
        Assert.False(SqliteExplainPlanInspector.ChoseIndex(plan, "ix_other"));
        Assert.False(SqliteExplainPlanInspector.ChoseIndex("SCAN things", "ix_expected"));
    }

    [Fact]
    public void Mongo_requires_ixscan_in_the_winning_plan_with_the_exact_name()
    {
        var plan = BsonDocument.Parse("""{"queryPlanner":{"winningPlan":{"stage":"FETCH","inputStage":{"stage":"IXSCAN","indexName":"ix_expected"}}},"executionStats":{"executionStages":{"stage":"COLLSCAN"}}}""");
        Assert.True(MongoExplainPlanInspector.ChoseIndex(plan, "ix_expected"));
        Assert.False(MongoExplainPlanInspector.ChoseIndex(plan, "ix_other"));
        Assert.False(MongoExplainPlanInspector.ChoseIndex(BsonDocument.Parse("""{"queryPlanner":{"winningPlan":{"stage":"COLLSCAN"}}}"""), "ix_expected"));
    }
}
