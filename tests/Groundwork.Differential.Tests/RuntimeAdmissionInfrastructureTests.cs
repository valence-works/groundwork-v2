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

    private sealed class RecordingConnection : DbConnection
    {
        public List<string> Commands { get; } = [];

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
        protected override DbParameterCollection DbParameterCollection { get; } = new RecordingParameterCollection();
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
            return null;
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

    private sealed class RecordingParameterCollection : DbParameterCollection
    {
        private readonly List<object> parameters = [];

        public override int Count => parameters.Count;
        public override object SyncRoot => parameters;
        public override int Add(object value)
        {
            parameters.Add(value);
            return parameters.Count - 1;
        }
        public override void AddRange(Array values) => parameters.AddRange(values.Cast<object>());
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
