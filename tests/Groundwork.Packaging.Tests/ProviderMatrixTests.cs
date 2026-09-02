using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Groundwork.Packaging.Tests;

public sealed class ProviderMatrixTests
{
    [Fact]
    public async Task Generated_matrices_are_current()
    {
        var root = RepositoryRoot.Find();
        var start = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("eng/generate-provider-matrices.sh");
        start.ArgumentList.Add("check");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start provider matrix generator.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"Provider matrix check failed.\n{await output}\n{await error}");
    }

    [Fact]
    public void Generated_matrix_contains_all_required_provider_profiles_and_contract_columns()
    {
        var root = RepositoryRoot.Find();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root, "docs/v2/generated/provider-capability-matrix.json")));

        var providers = document.RootElement.GetProperty("Providers")
            .EnumerateArray()
            .Select(provider => provider.GetProperty("Id").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("sqlite", providers);
        Assert.Contains("postgresql", providers);
        Assert.Contains("sqlserver", providers);
        Assert.Contains("mysql", providers);
        Assert.Contains("inmemory", providers);
        Assert.Contains("mongodb-replica-set", providers);
        Assert.Contains("mongodb-standalone", providers);

        var providerRows = document.RootElement.GetProperty("Providers")
            .EnumerateArray()
            .ToDictionary(provider => provider.GetProperty("Id").GetString()!, StringComparer.Ordinal);
        Assert.DoesNotContain("groundwork.storage.batched-native", providerRows["mysql"]
            .GetProperty("Capabilities").EnumerateArray().Select(capability => capability.GetProperty("Id").GetString()));
        Assert.DoesNotContain("groundwork.storage.exact-retention-affected-keys", providerRows["mysql"]
            .GetProperty("Capabilities").EnumerateArray().Select(capability => capability.GetProperty("Id").GetString()));
        Assert.DoesNotContain("groundwork.column.provider-sequence", providerRows["mongodb-standalone"]
            .GetProperty("Capabilities").EnumerateArray().Select(capability => capability.GetProperty("Id").GetString()));
        Assert.DoesNotContain("groundwork.storage.append-idempotency", providerRows["mongodb-standalone"]
            .GetProperty("Capabilities").EnumerateArray().Select(capability => capability.GetProperty("Id").GetString()));
        Assert.Contains("groundwork.operational.atomic-commit", providerRows["sqlite"]
            .GetProperty("Capabilities").EnumerateArray().Select(capability => capability.GetProperty("Id").GetString()));

        var capabilities = providerRows.Values
            .SelectMany(provider => provider.GetProperty("Capabilities").EnumerateArray())
            .Select(capability => capability.GetProperty("Id").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("groundwork.storage.exact-retention-affected-keys", capabilities);
        Assert.Contains("groundwork.schema.enforced-constraints", capabilities);
        Assert.Contains("groundwork.operational.atomic-commit", capabilities);

        var expectedPackages = File.ReadAllLines(Path.Combine(root, "eng/public-packages.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .Select(line => line.Split('|', 2, StringSplitOptions.TrimEntries)[0])
            .ToArray();
        var generatedPackages = document.RootElement.GetProperty("Packages")
            .EnumerateArray()
            .Select(package => package.GetProperty("Id").GetString())
            .ToArray();
        Assert.Equal(expectedPackages, generatedPackages);
    }
}
