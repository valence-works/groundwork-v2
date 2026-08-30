using System.Data;
using System.Data.Common;
using Groundwork.PostgreSql;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// Runtime admission pre-checks history and search-key catalogs by the shared
/// <see cref="RelationalDialect"/> table-name constants. If a dialect's infrastructure DDL ever
/// diverges from those constants, the pre-check reports the catalog as absent and drift admission
/// silently turns off — so every dialect's DDL must name both constants.
/// </summary>
public sealed class RuntimeAdmissionInfrastructureTests
{
    public static TheoryData<string> Dialects => new("sqlite", "postgresql", "sqlserver");

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Dialect_infrastructure_ddl_creates_the_catalogs_admission_pre_checks(string provider)
    {
        RelationalDialect dialect = provider switch
        {
            "sqlite" => new SqliteDialect(),
            "postgresql" => new PostgreSqlDialect(),
            _ => new SqlServerDialect()
        };
        var connection = new RecordingConnection();

        dialect.EnsureInfrastructure(connection);

        var ddl = string.Join("\n", connection.Commands.Where(text =>
            text.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(RelationalDialect.SchemaHistoryTable, ddl, StringComparison.Ordinal);
        Assert.Contains(RelationalDialect.SearchKeyAlgorithmsTable, ddl, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_table_existence_resolves_via_the_default_schema()
    {
        var connection = new RecordingConnection();

        Assert.False(new SqlServerDialect().TableExists(connection, null, RelationalDialect.SchemaHistoryTable));

        var sql = Assert.Single(connection.Commands);
        Assert.Contains("OBJECT_ID", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dbo", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServer_infrastructure_ddl_is_guarded_by_one_session_lock()
    {
        var connection = new RecordingConnection();

        new SqlServerDialect().EnsureInfrastructure(connection);

        Assert.Equal(3, connection.Commands.Count);
        Assert.Contains("sp_getapplock", connection.Commands[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("groundwork:infrastructure", connection.Parameters[0], StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE", connection.Commands[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sp_releaseapplock", connection.Commands[2], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("groundwork:infrastructure", connection.Parameters[1], StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_schema_application_lock_is_database_scoped_and_diagnosable()
    {
        var dialect = new SqlServerDialect();
        var first = new RecordingConnection(0, "Exclusive", 0);
        var second = new RecordingConnection();

        dialect.AcquireApplicationLock(first, "groundwork:schema:SQLServer:first");
        Assert.True(dialect.VerifyApplicationLock(first, "groundwork:schema:SQLServer:first"));
        dialect.ReleaseApplicationLock(first, "groundwork:schema:SQLServer:first");
        dialect.AcquireApplicationLock(second, "groundwork:schema:SQLServer:second");

        Assert.Equal(
            ["groundwork:schema", "groundwork:schema", "groundwork:schema"],
            first.Parameters);
        Assert.Equal("groundwork:schema", Assert.Single(second.Parameters));

        var unavailable = new RecordingConnection(-1);
        var refusal = Assert.Throws<InvalidOperationException>(() =>
            dialect.AcquireApplicationLock(unavailable, "groundwork:schema:SQLServer:third"));
        Assert.StartsWith("GW-SQLSERVER-LOCK-001:", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("groundwork:schema", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("result -1", refusal.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingConnection : DbConnection
    {
        private readonly Queue<object?> scalarResults;

        public RecordingConnection(params object?[] scalarResults) =>
            this.scalarResults = new Queue<object?>(scalarResults);

        public List<string> Commands { get; } = [];
        public List<string> Parameters { get; } = [];

        public object? NextScalarResult() => scalarResults.Count == 0 ? null : scalarResults.Dequeue();

#pragma warning disable CS8765
        public override string ConnectionString { get; set; } = string.Empty;
#pragma warning restore CS8765
        public override string Database => "recording";
        public override string DataSource => "recording";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new RecordingCommand(this);
    }

    private sealed class RecordingCommand(RecordingConnection owner) : DbCommand
    {
#pragma warning disable CS8765
        public override string CommandText { get; set; } = string.Empty;
#pragma warning restore CS8765
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbParameterCollection DbParameterCollection { get; } = new RecordingParameterCollection(owner);
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery()
        {
            owner.Commands.Add(CommandText);
            return 0;
        }
        public override object? ExecuteScalar()
        {
            owner.Commands.Add(CommandText);
            return owner.NextScalarResult();
        }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new RecordingParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
#pragma warning disable CS8765
        public override string ParameterName { get; set; } = string.Empty;
        public override string SourceColumn { get; set; } = string.Empty;
#pragma warning restore CS8765
        public override object? Value { get; set; }
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class RecordingParameterCollection(RecordingConnection owner) : DbParameterCollection
    {
        private readonly List<object> parameters = [];

        public override int Count => parameters.Count;
        public override object SyncRoot => parameters;
        public override int Add(object value)
        {
            parameters.Add(value);
            if (value is DbParameter parameter && parameter.Value is string text)
                owner.Parameters.Add(text);
            return parameters.Count - 1;
        }
        public override void AddRange(Array values)
        {
            foreach (var value in values)
                Add(value!);
        }
        public override void Clear() => parameters.Clear();
        public override bool Contains(object value) => parameters.Contains(value);
        public override bool Contains(string value) => IndexOf(value) >= 0;
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)parameters).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => parameters.GetEnumerator();
        public override int IndexOf(object value) => parameters.IndexOf(value);
        public override int IndexOf(string parameterName) => parameters.FindIndex(parameter =>
            string.Equals(((DbParameter)parameter).ParameterName, parameterName, StringComparison.Ordinal));
        public override void Insert(int index, object value) => parameters.Insert(index, value);
        public override void Remove(object value) => parameters.Remove(value);
        public override void RemoveAt(int index) => parameters.RemoveAt(index);
        public override void RemoveAt(string parameterName) => parameters.RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => (DbParameter)parameters[index];
        protected override DbParameter GetParameter(string parameterName) => (DbParameter)parameters[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => parameters[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => parameters[IndexOf(parameterName)] = value;
    }
}
