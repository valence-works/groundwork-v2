using Groundwork.Kernel;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteDataSourceTests
{
    [Fact]
    public void File_uri_percent_escapes_resolve_to_the_database_sqlite_opens()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gw uri " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = Path.Combine(directory, "store.db");
            var escaped = string.Join('/', database.Split('/').Select(Uri.EscapeDataString));
            Assert.Contains("%20", escaped, StringComparison.Ordinal);

            using (var connection = new SqliteProviderFactory().Create($"Data Source=file:{escaped}"))
                connection.Schema.Apply(Unit());

            Assert.True(File.Exists(database));
            Assert.True(File.Exists(database + ".schema.lock"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_plain_data_source_keeps_a_literal_percent_in_the_file_name()
    {
        var path = Path.Combine(Path.GetTempPath(), "gw%20literal.db");
        Assert.Equal(Path.GetFullPath(path), SqliteDataSource.FullPath(path));
    }

    [Fact]
    public void A_file_uri_without_escapes_resolves_unchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), "gw-plain.db");
        Assert.Equal(Path.GetFullPath(path), SqliteDataSource.FullPath("file:" + path));
    }

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("uri.probe.unit"),
        Name = "uri_probe_unit",
        Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false }],
        Key = new KeyDefinition { Columns = ["id"] }
    };
}
