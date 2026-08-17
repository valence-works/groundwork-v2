using Groundwork.Samples.Quickstart;
using Xunit;

namespace Groundwork.Docs.Acceptance.Tests;

public sealed class QuickstartTests
{
    [Fact]
    public void Public_quickstart_executes_against_real_sqlite()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"groundwork-docs-quickstart-{Guid.NewGuid():N}.db");

        try
        {
            var result = Program.Run(databasePath);

            Assert.Equal("ada@example.test", result.Email);
            Assert.Equal("Ada Lovelace", result.Name);
        }
        finally
        {
            if (File.Exists(databasePath))
                File.Delete(databasePath);
        }
    }
}
