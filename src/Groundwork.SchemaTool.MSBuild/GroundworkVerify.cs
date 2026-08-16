using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Groundwork.SchemaTool.MSBuild;

public sealed class GroundworkVerify : Microsoft.Build.Utilities.Task
{
    [Required]
    public string SchemaFile { get; set; } = string.Empty;

    public string? CoverageFile { get; set; }

    public override bool Execute()
    {
        try
        {
            var result = SchemaVerifier.Verify(
                File.ReadAllText(SchemaFile),
                string.IsNullOrWhiteSpace(CoverageFile) ? null : File.ReadAllText(CoverageFile));
            foreach (var error in result.Errors)
                Log.LogError(null, error.Code, null, SchemaFile, 0, 0, 0, 0, $"{error.Message} ({error.Path})");
            return result.Succeeded;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or FormatException or ArgumentException)
        {
            Log.LogError(null, "GW-SCHEMA-TOOL-001", null, SchemaFile, 0, 0, 0, 0, exception.Message);
            return false;
        }
    }
}
