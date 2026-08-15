using System.Globalization;
using System.Text;

namespace Groundwork.Diagnostics;



/// <summary>Failure raised by the opt-in native explain-plan assertion mode.</summary>
public sealed class ExplainAssertionException : InvalidOperationException
{
    internal ExplainAssertionException(string message, string artifactPath) : base(message) =>
        ArtifactPath = artifactPath;

    /// <summary>Path containing the unmodified native plan returned by the provider.</summary>
    public string ArtifactPath { get; }
}

/// <summary>
/// Test/CI-only assertion plumbing shared by native providers. The mode is disabled unless
/// <c>GW_EXPLAIN_ASSERT</c> is <c>1</c> or <c>true</c>.
/// </summary>
public static class ExplainAssertTestMode
{
    private static long sequence;

    public static bool Enabled => IsEnabled(Environment.GetEnvironmentVariable("GW_EXPLAIN_ASSERT"));

    /// <summary>Whether a rendered query carrying a proven index should be explained.</summary>
    public static bool ShouldAssert(string? selectedIndex) =>
        Enabled && !string.IsNullOrWhiteSpace(selectedIndex);

    /// <summary>Retains the raw plan and fails when it does not prove the expected physical index.</summary>
    public static void AssertChosenIndex(
        string provider,
        string logicalIndex,
        string physicalIndex,
        bool hinted,
        string rawPlan,
        bool chosen)
    {
        if (!Enabled) return;
        Verify(provider, logicalIndex, physicalIndex, hinted, rawPlan, chosen, ArtifactDirectory(), Console.WriteLine);
    }

    internal static bool IsEnabled(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    internal static string Verify(
        string provider,
        string logicalIndex,
        string physicalIndex,
        bool hinted,
        string rawPlan,
        bool chosen,
        string artifactDirectory,
        Action<string> writeOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalIndex);
        ArgumentNullException.ThrowIfNull(rawPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArgumentNullException.ThrowIfNull(writeOutput);

        Directory.CreateDirectory(artifactDirectory);
        var number = Interlocked.Increment(ref sequence).ToString("D6", CultureInfo.InvariantCulture);
        var artifact = Path.Combine(
            artifactDirectory,
            number + "-" + Safe(provider) + "-" + (hinted ? "hinted" : "optimizer-selected") + "-" + Safe(logicalIndex) + PlanExtension(provider));
        File.WriteAllText(artifact, rawPlan, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var choice = hinted ? "hinted" : "optimizer-selected";
        var diagnostic = $"[Groundwork explain-assert] provider={provider}; choice={choice}; logical-index={logicalIndex}; physical-index={physicalIndex}; artifact={artifact}";
        if (!chosen)
            throw new ExplainAssertionException(
                diagnostic + "; result=FAILED: the native winning plan did not use the required index.", artifact);

        writeOutput(diagnostic + "; result=passed");
        return artifact;
    }

    private static string ArtifactDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("GW_EXPLAIN_ARTIFACT_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.CurrentDirectory, "TestResults", "groundwork-explain")
            : Path.GetFullPath(configured);
    }

    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray());
    }

    private static string PlanExtension(string provider) => provider switch
    {
        "PostgreSQL" or "MongoDB" => ".json",
        "SQL Server" => ".xml",
        _ => ".txt"
    };
}
