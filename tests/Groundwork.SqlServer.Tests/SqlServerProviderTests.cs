using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;
using Groundwork.Kernel;
using Groundwork.SqlServer;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class SqlServerProviderTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    [Fact]
    public void Provider_passes_provider_neutral_conformance()
    {
        fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(fixture.ConnectionString);
        var report = ConformanceSuite.Run(new SqlServerProviderFactory(), fixture.ConnectionString);
        Assert.True(report.Passed, string.Join(Environment.NewLine,
            report.Checks.Where(check => !check.Passed).Select(check => $"{check.Name}: {check.Failure}")));
    }

    [Fact]
    public void Customer_email_320_is_a_native_unique_index()
    {
        fixture.Reset();
        using var connection = new SqlServerProviderFactory().Create(fixture.ConnectionString);
        var name = "customer_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name), Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
                new() { Name = "email", Type = PortableType.String, MaxLength = 320, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes = [new IndexDefinition { Name = "by-email", Columns = [new IndexColumn("email")], IsUnique = true }]
        };

        Assert.True(connection.Schema.Apply(unit).Applied);
        var indexes = connection.Catalog.ReadIndexes(unit.Id);
        var email = Assert.Single(indexes, index => index.Name == "by-email");
        Assert.True(email.IsUnique);
        Assert.Equal("email", Assert.Single(email.Columns).Column);
    }

    [Fact]
    public void Unbounded_primary_string_is_refused_before_connection_open()
    {
        using var connection = new SqlServerProviderFactory().Create(
            "Server=invalid-host.invalid,1433;Database=master;User Id=sa;Password=Groundwork!2026;Encrypt=False;TrustServerCertificate=True");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("unbounded-key"), Name = "unbounded-key",
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false }],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        var exception = Assert.Throws<SqlServerKeyBudgetException>(() => connection.Schema.Diff(unit));
        Assert.Contains("bounded String key column", exception.Message, StringComparison.Ordinal);
    }

}

public sealed class SqlServerFixture : IAsyncLifetime
{
    private MsSqlContainer? container;

    public string ConnectionString { get; private set; } = string.Empty;

    public void Reset()
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            WHERE t.name IN (N'conformance-global',N'conformance-scoped',N'__groundwork_schema_history',N'__groundwork_schema_fences')
               OR t.name LIKE N'customer[_]%';
            IF @sql <> N'' EXEC sys.sp_executesql @sql;
            """;
        command.ExecuteNonQuery();
    }

    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return;

        container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU21-ubuntu-22.04")
            .WithPassword("Groundwork!2026")
            .Build();
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (container is not null) await container.DisposeAsync();
    }
}
