using Groundwork.Kernel;

namespace Groundwork.Records;

/// <summary>A provider-neutral declaration diagnostic exposed by an authoring surface.</summary>
public sealed class GroundworkDiagnostic
{
    public GroundworkDiagnostic(string code, string message, string path)
    {
        Code = code ?? throw new ArgumentNullException(nameof(code));
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public string Code { get; }

    public string Message { get; }

    public string Path { get; }
}

/// <summary>Raised when a declaration is not portable for the supported targets.</summary>
public sealed class StorageDeclarationException : Exception
{
    public StorageDeclarationException(IEnumerable<GroundworkDiagnostic> diagnostics)
        : this(Snapshot(diagnostics))
    {
    }

    private StorageDeclarationException(GroundworkDiagnostic[] diagnostics)
        : base(CreateMessage(diagnostics)) => Diagnostics = Array.AsReadOnly(diagnostics);

    public IReadOnlyList<GroundworkDiagnostic> Diagnostics { get; }

    private static GroundworkDiagnostic[] Snapshot(IEnumerable<GroundworkDiagnostic> diagnostics)
    {
        if (diagnostics is null)
            throw new ArgumentNullException(nameof(diagnostics));

        var snapshot = diagnostics.ToArray();
        if (snapshot.Any(diagnostic => diagnostic is null))
            throw new ArgumentException("Diagnostics cannot contain null references.", nameof(diagnostics));

        return snapshot;
    }

    private static string CreateMessage(IEnumerable<GroundworkDiagnostic> diagnostics)
    {
        return "The storage declaration is not portable: " +
            string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message));
    }
}

internal static class DiagnosticsCompatibility
{
    public static StorageDeclarationException ToRecords(DeclarationBuildException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new StorageDeclarationException(exception.Findings.Select(finding =>
            new GroundworkDiagnostic(finding.Code, finding.Message, finding.Path)));
    }
}
