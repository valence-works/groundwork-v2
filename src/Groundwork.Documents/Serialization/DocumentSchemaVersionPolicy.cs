namespace Groundwork.Documents.Serialization;

/// <summary>Declares the contiguous readable compatibility window for one document kind.</summary>
public sealed record DocumentSchemaVersionPolicy
{
    public DocumentSchemaVersionPolicy(string documentKind, int minimumReadableVersion, int currentVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentKind);
        if (minimumReadableVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(minimumReadableVersion), "Schema versions start at 1.");
        if (currentVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(currentVersion), "Schema versions start at 1.");
        if (minimumReadableVersion > currentVersion)
            throw new ArgumentOutOfRangeException(nameof(minimumReadableVersion), "The minimum readable version cannot exceed the current version.");
        DocumentKind = documentKind;
        MinimumReadableVersion = minimumReadableVersion;
        CurrentVersion = currentVersion;
    }

    public string DocumentKind { get; }
    public int MinimumReadableVersion { get; }
    public int CurrentVersion { get; }
}
