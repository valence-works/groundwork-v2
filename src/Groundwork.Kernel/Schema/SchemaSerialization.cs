using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Groundwork.Kernel.Schema;

/// <summary>Compiles a first-class subject and provider metadata into one schema target.</summary>
public static class PhysicalSchemaTargetCompiler
{
    public static PhysicalSchemaTarget Compile(
        SchemaSubject subject,
        ProviderIdentity provider,
        IEnumerable<ProviderPhysicalSchemaDefinition>? providerDefinitions = null)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(provider);
        return new PhysicalSchemaTarget(subject, provider, providerDefinitions);
    }
}

/// <summary>
/// Signals applied schema state that this build cannot reproduce: the recorded target fingerprint
/// disagrees with the one its own subject snapshot now yields, which a release note marks as a
/// persisted schema boundary. The catalog is not migrated in place — it is discarded.
/// </summary>
public sealed class GroundworkSchemaBoundaryException : InvalidOperationException
{
    public const string Code = "GW-SCHEMA-006";

    public GroundworkSchemaBoundaryException(PhysicalSchemaTargetIdentity target)
        : base($"{Code}: applied schema state for '{target}' was recorded under a different persisted " +
               "schema boundary, so its target fingerprint no longer matches its own subject snapshot. " +
               "Discard that catalog and create a fresh one from the current declarations; Groundwork " +
               "ships no in-place migration, compatibility alias, dual-write, or fallback between them.") =>
        Target = target;

    public PhysicalSchemaTargetIdentity Target { get; }
}

/// <summary>Canonical JSON persistence for the CAS schema history snapshot.</summary>
public static partial class PhysicalSchemaAppliedStateSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static readonly JsonTypeInfo<StatePayload> PayloadTypeInfo =
        (JsonTypeInfo<StatePayload>)Options.GetTypeInfo(typeof(StatePayload));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(AppliedStateJsonContext.Default.Options)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            TypeInfoResolver = AppliedStateJsonContext.Default.WithAddedModifier(ModifyTypeInfo)
        };
        options.Converters.Insert(0, new PortableDefaultJsonConverter());
        options.Converters.Insert(1, new StringReadOnlySetJsonConverter());
        options.Converters.Insert(2, new AggregationPredicateReadOnlySetJsonConverter());
        return options;
    }

    private static void ModifyTypeInfo(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(StorageUnit))
            return;
        var references = typeInfo.Properties.Single(property =>
            string.Equals(property.Name, nameof(StorageUnit.References), StringComparison.OrdinalIgnoreCase));
        references.ShouldSerialize = static (_, value) =>
            value is IReadOnlyList<ReferenceDefinition> { Count: > 0 };
        var checks = typeInfo.Properties.Single(property =>
            string.Equals(property.Name, nameof(StorageUnit.CheckConstraints), StringComparison.OrdinalIgnoreCase));
        checks.ShouldSerialize = static (_, value) =>
            value is IReadOnlyList<CheckConstraintDefinition> { Count: > 0 };
    }

    public static string Serialize(PhysicalSchemaAppliedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var payload = new StatePayload
        {
            Definition = state.Snapshot.Subject.Definition,
            Evolution = state.Snapshot.Subject.Evolution,
            Provider = state.Provider,
            TargetFingerprint = state.TargetFingerprint,
            PlannedAt = state.PlannedAt,
            AppliedAt = state.AppliedAt,
            SemanticOperations = state.Snapshot.SemanticOperations.ToArray(),
            ProviderDefinitions = state.Snapshot.ProviderDefinitions.ToArray(),
            AppliedOperations = state.AppliedOperations.ToArray()
        };
        return JsonSerializer.Serialize(payload, PayloadTypeInfo);
    }

    public static PhysicalSchemaAppliedState Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var payload = JsonSerializer.Deserialize(json, PayloadTypeInfo)
            ?? throw new ArgumentException("Applied schema state JSON is empty.", nameof(json));
        if (payload.Definition is null || payload.Provider is null || payload.Evolution is null)
            throw new ArgumentException("Applied schema state JSON is missing its subject or provider.", nameof(json));

        var subject = new SchemaSubject(payload.Definition, payload.Evolution);
        var target = new PhysicalSchemaTarget(subject, payload.Provider, payload.ProviderDefinitions ?? []);
        if (!string.Equals(target.Fingerprint, payload.TargetFingerprint, StringComparison.Ordinal))
            throw new GroundworkSchemaBoundaryException(target.Identity);
        var snapshot = new PhysicalSchemaAppliedSnapshot(
            subject,
            payload.SemanticOperations ?? [],
            payload.ProviderDefinitions ?? []);
        var state = new PhysicalSchemaAppliedState(
            target,
            payload.PlannedAt,
            payload.AppliedAt,
            snapshot,
            payload.AppliedOperations ?? []);
        if (!string.Equals(Serialize(state), json, StringComparison.Ordinal))
            throw new InvalidOperationException("Applied schema state JSON is not in canonical form.");
        return state;
    }

    private sealed class StatePayload
    {
        public StorageUnit? Definition { get; set; }

        public SchemaEvolutionMetadata? Evolution { get; set; }

        public ProviderIdentity? Provider { get; set; }

        public string? TargetFingerprint { get; set; }

        public DateTimeOffset PlannedAt { get; set; }

        public DateTimeOffset AppliedAt { get; set; }

        public PhysicalSchemaAppliedOperation[]? SemanticOperations { get; set; }

        public ProviderPhysicalSchemaDefinition[]? ProviderDefinitions { get; set; }

        public PhysicalSchemaAppliedOperation[]? AppliedOperations { get; set; }
    }

    private sealed class StringReadOnlySetJsonConverter : JsonConverter<IReadOnlySet<string>>
    {
        public override IReadOnlySet<string> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("A set must be an array.");
            var values = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                values.Add(reader.GetString() ?? throw new JsonException("A string set cannot contain null."));
            return values;
        }

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<string> value,
            JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
                writer.WriteStringValue(item);
            writer.WriteEndArray();
        }
    }

    private sealed class AggregationPredicateReadOnlySetJsonConverter
        : JsonConverter<IReadOnlySet<AggregationPredicateOperator>>
    {
        public override IReadOnlySet<AggregationPredicateOperator> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("A set must be an array.");
            var values = new HashSet<AggregationPredicateOperator>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                var name = reader.GetString() ?? throw new JsonException("An aggregation predicate cannot be null.");
                if (!Enum.TryParse<AggregationPredicateOperator>(name, ignoreCase: true, out var value))
                    throw new JsonException($"Unknown aggregation predicate '{name}'.");
                values.Add(value);
            }
            return values;
        }

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<AggregationPredicateOperator> value,
            JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
                writer.WriteStringValue(item.ToString());
            writer.WriteEndArray();
        }
    }

    private sealed class PortableDefaultJsonConverter : JsonConverter<PortableDefault>
    {
        public override PortableDefault Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var kind = root.GetProperty("kind").GetString()
                ?? throw new JsonException("A portable default requires a kind.");
            var value = root.GetProperty("value");
            return new PortableDefault(kind switch
            {
                "null" => null,
                "string" => value.GetString(),
                "boolean" => value.GetBoolean(),
                "int32" => value.GetInt32(),
                "int64" => value.GetInt64(),
                "decimal" => value.GetDecimal(),
                "double" => value.GetDouble(),
                "timestamp" => value.GetDateTimeOffset(),
                "datetime" => value.GetDateTime(),
                "guid" => value.GetGuid(),
                "binary" => value.GetBytesFromBase64(),
                "json" => ReadJson(value),
                _ => throw new JsonException($"Unknown portable default kind '{kind}'.")
            });
        }

        public override void Write(
            Utf8JsonWriter writer,
            PortableDefault value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", Kind(value.Value));
            writer.WritePropertyName("value");
            if (value.Value is null)
                writer.WriteNullValue();
            else if (value.Value is byte[] bytes)
                writer.WriteBase64StringValue(bytes);
            else
                PortableJsonSerializer.WriteClosed(writer, value.Value);
            writer.WriteEndObject();
        }

        private static string Kind(object? value) => value switch
        {
            null => "null",
            string or char => "string",
            bool => "boolean",
            byte or sbyte or short or ushort or int or uint => "int32",
            long or ulong => "int64",
            decimal => "decimal",
            float or double => "double",
            DateTimeOffset => "timestamp",
            DateTime => "datetime",
            Guid => "guid",
            byte[] => "binary",
            System.Collections.IDictionary or System.Collections.IEnumerable => "json",
            _ => throw new JsonException($"Unsupported portable default type '{value.GetType()}'.")
        };

        private static object? ReadJson(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt32(out var int32) => int32,
            JsonValueKind.Number when value.TryGetInt64(out var int64) => int64,
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Array => value.EnumerateArray().Select(ReadJson).ToList(),
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(
                property => property.Name,
                property => ReadJson(property.Value),
                StringComparer.Ordinal),
            _ => throw new JsonException($"Unsupported JSON default token '{value.ValueKind}'.")
        };
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = false,
        UseStringEnumConverter = true,
        GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(StatePayload))]
    private sealed partial class AppliedStateJsonContext : JsonSerializerContext;
}
