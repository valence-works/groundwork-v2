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
        : base(CreateMessage(diagnostics))
    {
        Diagnostics = Array.AsReadOnly((diagnostics ?? throw new ArgumentNullException(nameof(diagnostics))).ToArray());
    }

    public IReadOnlyList<GroundworkDiagnostic> Diagnostics { get; }

    private static string CreateMessage(IEnumerable<GroundworkDiagnostic> diagnostics)
    {
        if (diagnostics is null)
            throw new ArgumentNullException(nameof(diagnostics));

        return "The storage declaration is not portable: " +
            string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Code + ": " + diagnostic.Message));
    }
}
