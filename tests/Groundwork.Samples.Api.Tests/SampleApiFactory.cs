using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Groundwork.Samples.Api.Tests;

/// <summary>
/// Hosts the sample against its own SQLite file. The development schema switch is on, which is the
/// only supported way to get a database without running <c>groundwork apply</c> first — the same
/// path a developer takes with <c>dotnet run</c>.
/// </summary>
public sealed class SampleApiFactory : WebApplicationFactory<Program>
{
    private readonly string database = Path.Combine(
        Path.GetTempPath(), $"groundwork-sample-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var provider = Environment.GetEnvironmentVariable("GROUNDWORK_SAMPLE_PROVIDER") ?? "sqlite";
        var configuredConnection = Environment.GetEnvironmentVariable("GROUNDWORK_SAMPLE_CONNECTION");
        var connectionString = configuredConnection
            ?? (provider == "sqlite" ? $"Data Source={database}" : $"groundwork-sample-{Guid.NewGuid():N}");
        builder.UseEnvironment("Development");
        builder.UseSetting("Groundwork:Provider", provider);
        builder.UseSetting("Groundwork:ConnectionString", connectionString);
        builder.UseSetting("Groundwork:DevelopmentApplySchema", "true");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        foreach (var path in new[] { database, database + ".schema.lock" })
            File.Delete(path);
    }
}
