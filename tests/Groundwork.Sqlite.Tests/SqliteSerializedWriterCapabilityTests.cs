using Groundwork.Kernel;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteSerializedWriterCapabilityTests
{
    [Fact]
    public void Sqlite_advertises_the_serialized_writer_capability()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundwork-serialized-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={Path.Combine(directory, "store.db")}");
            var descriptor = Assert.Single(connection.Capabilities, capability => capability.Id == WellKnownCapabilities.SerializedWriter);
            Assert.Equal(BatchWriteCapabilities.SerializedWriterDescriptor.DisplayName, descriptor.DisplayName);
            Assert.Contains("one writing transaction at a time", descriptor.Description, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
