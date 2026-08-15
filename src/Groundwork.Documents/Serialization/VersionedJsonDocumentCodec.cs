using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Documents.Store;

namespace Groundwork.Documents.Serialization;

public sealed record VersionedJsonContent(string SchemaVersion, string ContentJson);

/// <summary>Minimal versioned JSON input used by typed document mappings.</summary>
public sealed record VersionedJsonPayload(string DocumentKind, string SchemaVersion, string ContentJson);

/// <summary>Serializes canonical current JSON and validates/upcasts persisted JSON before materialization.</summary>
public sealed class VersionedJsonDocumentCodec
{
    private readonly DocumentJsonUpcasterRegistry registry;
    private readonly DocumentSchemaVersionFormat versionFormat;
    private readonly JsonSerializerOptions? jsonOptions;
    private readonly JsonDocumentOptions documentOptions;

    public VersionedJsonDocumentCodec(
        IEnumerable<DocumentSchemaVersionPolicy> policies,
        IEnumerable<IDocumentJsonUpcaster> upcasters,
        DocumentSchemaVersionFormat versionFormat,
        JsonSerializerOptions? jsonOptions = null)
    {
        registry = new DocumentJsonUpcasterRegistry(policies, upcasters);
        this.versionFormat = versionFormat ?? throw new ArgumentNullException(nameof(versionFormat));
        this.jsonOptions = jsonOptions;
        documentOptions = jsonOptions is null ? default : new JsonDocumentOptions
        {
            AllowTrailingCommas = jsonOptions.AllowTrailingCommas,
            CommentHandling = jsonOptions.ReadCommentHandling,
            MaxDepth = jsonOptions.MaxDepth
        };
        foreach (var policy in registry.Policies)
            this.versionFormat.ValidateRoundTrips(policy);
    }

    public VersionedJsonContent Serialize<TDocument>(string documentKind, TDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var policy = registry.GetPolicy(documentKind);
        var stamp = versionFormat.Stamp(policy, policy.CurrentVersion);
        var raw = JsonSerializer.Serialize(document, jsonOptions);
        return new VersionedJsonContent(stamp, Canonicalize(raw));
    }

    public SaveDocumentRequest CreateSaveRequest<TDocument>(string documentKind, string id, TDocument document, long? expectedVersion = null)
    {
        var serialized = Serialize(documentKind, document);
        return new SaveDocumentRequest(documentKind, id, serialized.SchemaVersion, serialized.ContentJson, expectedVersion);
    }

    public bool IsCurrentVersion(DocumentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var policy = registry.GetPolicy(envelope.DocumentKind);
        return versionFormat.Parse(envelope.DocumentKind, envelope.Id, envelope.SchemaVersion,
            policy.MinimumReadableVersion, policy.CurrentVersion) == policy.CurrentVersion;
    }

    public TDocument Deserialize<TDocument>(DocumentEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var policy = registry.GetPolicy(envelope.DocumentKind);
        var version = ReadSupportedVersion(envelope, policy);
        if (version == policy.CurrentVersion)
            return DeserializeContent<TDocument>(envelope, policy, version, envelope.ContentJson);
        JsonObject content;
        try
        {
            content = JsonNode.Parse(envelope.ContentJson, documentOptions: documentOptions) as JsonObject
                ?? throw InvalidContent(envelope, policy, version, "does not contain a JSON object and cannot be upcasted");
        }
        catch (JsonException exception) { throw InvalidContent(envelope, policy, version, "does not contain valid JSON and cannot be upcasted", exception); }
        var upcasted = registry.UpcastToCurrent(envelope.DocumentKind, version, content);
        return DeserializeContent<TDocument>(envelope, policy, version, upcasted);
    }

    /// <summary>Deserializes a typed payload without inventing an identity or timestamp.</summary>
    public TDocument Deserialize<TDocument>(VersionedJsonPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var policy = registry.GetPolicy(payload.DocumentKind);
        var version = versionFormat.Parse(payload.DocumentKind, "(row)", payload.SchemaVersion,
            policy.MinimumReadableVersion, policy.CurrentVersion);
        if (version < policy.MinimumReadableVersion)
            throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.TooOld,
                $"A document of kind '{payload.DocumentKind}' carries schema version {version}, below minimum readable version {policy.MinimumReadableVersion}.",
                payload.DocumentKind, schemaVersion: payload.SchemaVersion, parsedVersion: version,
                minimumReadableVersion: policy.MinimumReadableVersion, currentVersion: policy.CurrentVersion);
        if (version > policy.CurrentVersion)
            throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.Future,
                $"A document of kind '{payload.DocumentKind}' carries future schema version {version}; this build supports up to {policy.CurrentVersion}.",
                payload.DocumentKind, schemaVersion: payload.SchemaVersion, parsedVersion: version,
                minimumReadableVersion: policy.MinimumReadableVersion, currentVersion: policy.CurrentVersion);
        if (version == policy.CurrentVersion)
            return DeserializePayloadContent<TDocument>(payload, policy, version, payload.ContentJson);

        JsonObject content;
        try
        {
            content = JsonNode.Parse(payload.ContentJson, documentOptions: documentOptions) as JsonObject
                ?? throw InvalidPayloadContent(payload, policy, version, "does not contain a JSON object and cannot be upcasted");
        }
        catch (JsonException exception)
        {
            throw InvalidPayloadContent(payload, policy, version, "does not contain valid JSON and cannot be upcasted", exception);
        }
        return DeserializePayloadContent<TDocument>(payload, policy, version,
            registry.UpcastToCurrent(payload.DocumentKind, version, content));
    }

    private TDocument DeserializeContent<TDocument>(DocumentEnvelope envelope, DocumentSchemaVersionPolicy policy, int version, string content)
    {
        try { return JsonSerializer.Deserialize<TDocument>(content, jsonOptions) ?? throw InvalidContent(envelope, policy, version, "deserialized to null content"); }
        catch (DocumentSchemaVersionException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        { throw InvalidContent(envelope, policy, version, "could not be deserialized", exception); }
    }

    private TDocument DeserializeContent<TDocument>(DocumentEnvelope envelope, DocumentSchemaVersionPolicy policy, int version, JsonObject content)
    {
        try { return content.Deserialize<TDocument>(jsonOptions) ?? throw InvalidContent(envelope, policy, version, "upcasted to null content"); }
        catch (DocumentSchemaVersionException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        { throw InvalidContent(envelope, policy, version, "could not be deserialized after upcasting", exception); }
    }

    private TDocument DeserializePayloadContent<TDocument>(VersionedJsonPayload payload, DocumentSchemaVersionPolicy policy, int version, string content)
    {
        try { return JsonSerializer.Deserialize<TDocument>(content, jsonOptions) ?? throw InvalidPayloadContent(payload, policy, version, "deserialized to null content"); }
        catch (DocumentSchemaVersionException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        { throw InvalidPayloadContent(payload, policy, version, "could not be deserialized", exception); }
    }

    private TDocument DeserializePayloadContent<TDocument>(VersionedJsonPayload payload, DocumentSchemaVersionPolicy policy, int version, JsonObject content)
    {
        try { return content.Deserialize<TDocument>(jsonOptions) ?? throw InvalidPayloadContent(payload, policy, version, "upcasted to null content"); }
        catch (DocumentSchemaVersionException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        { throw InvalidPayloadContent(payload, policy, version, "could not be deserialized after upcasting", exception); }
    }

    private int ReadSupportedVersion(DocumentEnvelope envelope, DocumentSchemaVersionPolicy policy)
    {
        var version = versionFormat.Parse(envelope.DocumentKind, envelope.Id, envelope.SchemaVersion,
            policy.MinimumReadableVersion, policy.CurrentVersion);
        if (version < policy.MinimumReadableVersion)
            throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.TooOld,
                $"Document '{envelope.Id}' of kind '{envelope.DocumentKind}' carries schema version {version}, below minimum readable version {policy.MinimumReadableVersion}.",
                envelope.DocumentKind, envelope.Id, envelope.SchemaVersion, version, policy.MinimumReadableVersion, policy.CurrentVersion);
        if (version > policy.CurrentVersion)
            throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.Future,
                $"Document '{envelope.Id}' of kind '{envelope.DocumentKind}' carries future schema version {version}; this build supports up to {policy.CurrentVersion}.",
                envelope.DocumentKind, envelope.Id, envelope.SchemaVersion, version, policy.MinimumReadableVersion, policy.CurrentVersion);
        return version;
    }

    private static DocumentSchemaVersionException InvalidContent(DocumentEnvelope envelope, DocumentSchemaVersionPolicy policy, int version, string detail, Exception? inner = null) =>
        new(DocumentSchemaVersionFailure.InvalidContent,
            $"Document '{envelope.Id}' of kind '{envelope.DocumentKind}' at schema version {version} {detail}.",
            envelope.DocumentKind, envelope.Id, envelope.SchemaVersion, version, policy.MinimumReadableVersion, policy.CurrentVersion, inner);

    private static DocumentSchemaVersionException InvalidPayloadContent(VersionedJsonPayload payload, DocumentSchemaVersionPolicy policy, int version, string detail, Exception? inner = null) =>
        new(DocumentSchemaVersionFailure.InvalidContent,
            $"A document of kind '{payload.DocumentKind}' at schema version {version} {detail}.",
            payload.DocumentKind, schemaVersion: payload.SchemaVersion, parsedVersion: version,
            minimumReadableVersion: policy.MinimumReadableVersion, currentVersion: policy.CurrentVersion, innerException: inner);

    private string Canonicalize(string json)
    {
        try
        {
            var node = JsonNode.Parse(json, documentOptions: documentOptions)
                ?? throw new JsonException("Document JSON was null.");
            return CanonicalNode(node).ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch (JsonException exception)
        {
            throw new DocumentSchemaVersionException(DocumentSchemaVersionFailure.InvalidContent,
                "The document could not be serialized as a canonical JSON object.", innerException: exception);
        }
    }

    private static JsonNode CanonicalNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var canonical = new JsonObject();
            foreach (var property in obj.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                canonical[property.Key] = property.Value is null ? null : CanonicalNode(property.Value);
            return canonical;
        }
        if (node is JsonArray array)
        {
            var canonical = new JsonArray();
            foreach (var item in array)
                canonical.Add(item is null ? null : CanonicalNode(item));
            return canonical;
        }
        return node.DeepClone();
    }
}

public sealed record SaveDocumentRequest(
    string DocumentKind,
    string Id,
    string SchemaVersion,
    string ContentJson,
    long? ExpectedVersion = null);
