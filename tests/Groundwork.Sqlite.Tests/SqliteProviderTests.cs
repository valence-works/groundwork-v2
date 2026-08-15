using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Testing;
using Groundwork.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteProviderTests
{
    [Fact]
    public void Provider_passes_provider_neutral_conformance()
    {
        using var store = TemporaryStore.Create();
        var report = ConformanceSuite.Run(new SqliteProviderFactory(), store.ConnectionString);
        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Checks.Where(check => !check.Passed).Select(check => $"{check.Name}: {check.Failure}")));
    }

    [Fact]
    public void Non_nullable_addition_rebuild_preserves_rows_and_unique_indexes()
    {
        using var store = TemporaryStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var original = Model(includePriority: false);
        Assert.True(connection.Schema.Apply(original).Applied);
        var session = connection.OpenSession(original, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(new Dictionary<string, object?>
        {
            ["id"] = "one", ["value"] = "keep", ["uniqueValue"] = "unique"
        })).Status);

        var evolved = Model(includePriority: true);
        var applied = connection.Schema.Apply(evolved);
        Assert.True(applied.Applied);
        var read = connection.OpenSession(evolved, StorageAccess.Global).Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "one" }));
        Assert.NotNull(read);
        Assert.Equal("keep", read!.Values.Values["value"]);
        Assert.Equal(0, read.Values.Values["priority"]);
        Assert.Equal(["by-value", "unique-value"], connection.Catalog.ReadIndexes(evolved.Id).Select(index => index.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(WriteOutcomeStatus.UniqueViolation, connection.OpenSession(evolved, StorageAccess.Global).Insert(
            new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "two", ["value"] = "other", ["uniqueValue"] = "unique", ["priority"] = 1
            })).Status);
    }

    [Fact]
    public void Store_lock_is_held_for_connection_lifetime()
    {
        using var store = TemporaryStore.Create();
        using var first = new SqliteProviderFactory().Create(store.ConnectionString);
        var error = Assert.Throws<InvalidOperationException>(() => new SqliteProviderFactory().Create(store.ConnectionString));
        Assert.Contains("already in use", error.Message, StringComparison.Ordinal);
        first.Dispose();
        using var second = new SqliteProviderFactory().Create(store.ConnectionString);
    }

    private static StorageUnit Model(bool includePriority) => new()
    {
        Id = new StorageUnitId("rebuild"), Name = "rebuild",
        Columns = includePriority
            ? [new() { Name = "id", Type = PortableType.String, IsNullable = false }, new() { Name = "value", Type = PortableType.String }, new() { Name = "uniqueValue", Type = PortableType.String }, new() { Name = "priority", Type = PortableType.Int32, IsNullable = false, Default = new PortableDefault(0) }]
            : [new() { Name = "id", Type = PortableType.String, IsNullable = false }, new() { Name = "value", Type = PortableType.String }, new() { Name = "uniqueValue", Type = PortableType.String }],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition { Name = "by-value", Columns = [new IndexColumn("value")] },
            new IndexDefinition { Name = "unique-value", Columns = [new IndexColumn("uniqueValue")], IsUnique = true }
        ]
    };

    private sealed class TemporaryStore : IDisposable
    {
        private readonly string directory;
        private TemporaryStore(string directory) { this.directory = directory; ConnectionString = $"Data Source={Path.Combine(directory, "store.db")}"; }
        public string ConnectionString { get; }
        public static TemporaryStore Create() { var path = Path.Combine(Path.GetTempPath(), "groundwork-sqlite-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return new(path); }
        public void Dispose() { try { Directory.Delete(directory, recursive: true); } catch { } }
    }
}
