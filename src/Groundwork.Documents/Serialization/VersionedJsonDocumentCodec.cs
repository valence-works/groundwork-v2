using System.Text.Json;
using System.Text.Json.Nodes;

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

    public bool IsCurrentVersion(VersionedJsonPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var policy = registry.GetPolicy(payload.DocumentKind);
        return versionFormat.Parse(payload.DocumentKind, "(row)", payload.SchemaVersion,
            policy.MinimumReadableVersion, policy.CurrentVersion) == policy.CurrentVersion;
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
            if (node is not JsonObject)
                throw new JsonException("A canonical document must be a JSON object.");
            var canonicalOptions = jsonOptions is null
                ? new JsonSerializerOptions { WriteIndented = false }
                : new JsonSerializerOptions(jsonOptions) { WriteIndented = false };
            return CanonicalNode(node).ToJsonString(canonicalOptions);
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
