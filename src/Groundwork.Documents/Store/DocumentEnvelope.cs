namespace Groundwork.Documents.Store;

/// <summary>The persisted JSON body and metadata returned by a document read.</summary>
public sealed record DocumentEnvelope(
    string DocumentKind,
    string Id,
    string SchemaVersion,
    long Version,
    string ContentJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
