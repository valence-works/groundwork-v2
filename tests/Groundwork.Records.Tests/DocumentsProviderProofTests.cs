using Groundwork.Documents;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Store;
using Groundwork.Sqlite;
using Xunit;

namespace Groundwork.Records.Tests;

[Collection("Records provider integration")]
public sealed class DocumentsProviderProofTests
{
    [Fact]
    public void SQLite_document_write_matches_an_equivalent_ordinary_row_write()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-documents-proof-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create("Data Source=" + path);
            AssertEquivalentWrite(connection, "documents_sqlite_" + Guid.NewGuid().ToString("N"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [SkippableFact]
    public void PostgreSQL_document_write_matches_an_equivalent_ordinary_row_write()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL Documents proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertEquivalentWrite(connection, "documents_pg_" + Guid.NewGuid().ToString("N"));
    }

    [SkippableFact]
    public void SQLServer_document_write_matches_an_equivalent_ordinary_row_write()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server Documents proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertEquivalentWrite(connection, "documents_sqlserver_" + Guid.NewGuid().ToString("N"));
    }

    [SkippableFact]
    public void MongoDB_document_write_matches_an_equivalent_ordinary_row_write()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB Documents proof.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        AssertEquivalentWrite(connection, "documents_mongo_" + Guid.NewGuid().ToString("N"));
    }

    private static void AssertEquivalentWrite(IStorageProviderConnection connection, string storageName)
    {
        var unit = DocumentUnit.For<ProviderDocument>("provider-document", storageName)
            .Id(document => document.Id)
            .Project(document => document.Name)
            .Build();
        Assert.True(connection.Schema.Apply(unit.StorageUnit).Applied);

        var value = new ProviderDocument(Guid.NewGuid(), "Ada");
        var documentObserver = new ProviderCommandObserver();
        var documentWrite = unit.Upsert(value, new WriteOptions { Observer = documentObserver });
        var documentOutcome = unit.Execute(connection, documentWrite);

        var ordinaryObserver = new ProviderCommandObserver();
        var ordinaryWrite = RowWrite.Upsert(
            unit.StorageUnit,
            new StorageValues(documentWrite.Values!.Values),
            new WriteOptions { Observer = ordinaryObserver });
        var ordinaryOutcome = connection.OpenSession(unit.StorageUnit, StorageAccess.Global)
            .Upsert(ordinaryWrite.Values!, ordinaryWrite.Options);

        Assert.True(documentOutcome.Succeeded);
        Assert.True(ordinaryOutcome.Succeeded);
        Assert.Equal(documentWrite.Values!.Values, ordinaryWrite.Values!.Values);
        Assert.Equal(documentWrite.Options.Precondition, ordinaryWrite.Options.Precondition);
        Assert.Single(documentObserver.Commands);
        Assert.Single(ordinaryObserver.Commands);
        Assert.False(documentObserver.Commands[0].IsProbe);
        Assert.False(ordinaryObserver.Commands[0].IsProbe);
        Assert.Equal(documentObserver.Commands[0], ordinaryObserver.Commands[0]);
    }

    private sealed record ProviderDocument(Guid Id, string Name);
}
