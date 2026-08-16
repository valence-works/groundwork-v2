namespace Groundwork.Documents.Serialization;

public enum DocumentSchemaVersionFailure
{
    UnknownDocumentKind,
    MalformedStamp,
    TooOld,
    Future,
    InvalidPolicy,
    InvalidVersionFormat,
    InvalidUpcasterChain,
    InvalidContent,
    UpcastFailed
}

/// <summary>Thrown when a document schema-version stamp or compatibility path is unsafe.</summary>
public sealed class DocumentSchemaVersionException : Exception
{
    internal DocumentSchemaVersionException(
        DocumentSchemaVersionFailure failure,
        string message,
        string? documentKind = null,
        string? documentId = null,
        string? schemaVersion = null,
        int? parsedVersion = null,
        int? minimumReadableVersion = null,
        int? currentVersion = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        DocumentKind = documentKind;
        DocumentId = documentId;
        SchemaVersion = schemaVersion;
        ParsedVersion = parsedVersion;
        MinimumReadableVersion = minimumReadableVersion;
        CurrentVersion = currentVersion;
    }

    public DocumentSchemaVersionFailure Failure { get; }
    public string? DocumentKind { get; }
    public string? DocumentId { get; }
    public string? SchemaVersion { get; }
    public int? ParsedVersion { get; }
    public int? MinimumReadableVersion { get; }
    public int? CurrentVersion { get; }
}
