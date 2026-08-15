using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>Canonical JSON persistence for the CAS schema history snapshot.</summary>
public static class PhysicalSchemaAppliedStateSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

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
        return JsonSerializer.Serialize(payload, Options);
    }

    public static PhysicalSchemaAppliedState Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var payload = JsonSerializer.Deserialize<StatePayload>(json, Options)
            ?? throw new ArgumentException("Applied schema state JSON is empty.", nameof(json));
        if (payload.Definition is null || payload.Provider is null || payload.Evolution is null)
            throw new ArgumentException("Applied schema state JSON is missing its subject or provider.", nameof(json));

        var subject = new SchemaSubject(payload.Definition, payload.Evolution);
        var target = new PhysicalSchemaTarget(subject, payload.Provider, payload.ProviderDefinitions ?? []);
        if (!string.Equals(target.Fingerprint, payload.TargetFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Applied schema state target fingerprint does not match its subject snapshot.");
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
}
