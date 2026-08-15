using System.Text.Json.Nodes;

namespace Groundwork.Documents.Serialization;

/// <summary>Eagerly validates and applies contiguous JSON upcaster chains.</summary>
public sealed class DocumentJsonUpcasterRegistry
{
    private readonly Dictionary<string, DocumentSchemaVersionPolicy> policies = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Kind, int Version), IDocumentJsonUpcaster> steps = new();

    public DocumentJsonUpcasterRegistry(IEnumerable<DocumentSchemaVersionPolicy> policies, IEnumerable<IDocumentJsonUpcaster> upcasters)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(upcasters);
        foreach (var policy in policies)
        {
            ArgumentNullException.ThrowIfNull(policy);
            if (!this.policies.TryAdd(policy.DocumentKind, policy))
                throw Invalid(DocumentSchemaVersionFailure.InvalidPolicy,
                    $"Multiple schema-version policies are registered for document kind '{policy.DocumentKind}'.",
                    policy.DocumentKind, minimum: policy.MinimumReadableVersion, current: policy.CurrentVersion);
        }
        foreach (var upcaster in upcasters)
        {
            ArgumentNullException.ThrowIfNull(upcaster);
            ArgumentException.ThrowIfNullOrWhiteSpace(upcaster.DocumentKind);
            if (!this.policies.TryGetValue(upcaster.DocumentKind, out var policy))
                throw Invalid(DocumentSchemaVersionFailure.InvalidUpcasterChain,
                    $"Upcaster '{upcaster.GetType().FullName}' targets document kind '{upcaster.DocumentKind}', but no schema-version policy declares that kind.",
                    upcaster.DocumentKind, parsed: upcaster.FromVersion);
            if (upcaster.FromVersion < policy.MinimumReadableVersion || upcaster.FromVersion >= policy.CurrentVersion)
                throw InvalidChain(policy, upcaster.FromVersion,
                    $"Upcaster for document kind '{policy.DocumentKind}' must start within versions {policy.MinimumReadableVersion} through {policy.CurrentVersion - 1}.");
            if (!steps.TryAdd((upcaster.DocumentKind, upcaster.FromVersion), upcaster))
                throw InvalidChain(policy, upcaster.FromVersion,
                    $"Multiple upcasters are registered for document kind '{policy.DocumentKind}' from version {upcaster.FromVersion} to {upcaster.FromVersion + 1}.");
        }
        foreach (var policy in this.policies.Values)
            for (var version = policy.MinimumReadableVersion; version < policy.CurrentVersion; version++)
                if (!steps.ContainsKey((policy.DocumentKind, version)))
                    throw InvalidChain(policy, version,
                        $"Document kind '{policy.DocumentKind}' has no upcaster from version {version} to {version + 1}; every supported version must reach current version {policy.CurrentVersion}.");
    }

    public IEnumerable<DocumentSchemaVersionPolicy> Policies => policies.Values;

    public DocumentSchemaVersionPolicy GetPolicy(string documentKind) =>
        policies.TryGetValue(documentKind, out var policy)
            ? policy
            : throw Invalid(DocumentSchemaVersionFailure.UnknownDocumentKind,
                $"No schema-version policy is registered for document kind '{documentKind}'.", documentKind);

    public JsonObject UpcastToCurrent(string documentKind, int fromVersion, JsonObject content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var policy = GetPolicy(documentKind);
        if (fromVersion < policy.MinimumReadableVersion)
            throw Invalid(DocumentSchemaVersionFailure.TooOld,
                $"Cannot upcast document kind '{documentKind}' from version {fromVersion}; its minimum readable version is {policy.MinimumReadableVersion}.",
                documentKind, parsed: fromVersion, minimum: policy.MinimumReadableVersion, current: policy.CurrentVersion);
        if (fromVersion > policy.CurrentVersion)
            throw Invalid(DocumentSchemaVersionFailure.Future,
                $"Cannot upcast document kind '{documentKind}' from future version {fromVersion}; current version is {policy.CurrentVersion}.",
                documentKind, parsed: fromVersion, minimum: policy.MinimumReadableVersion, current: policy.CurrentVersion);
        var current = content;
        for (var version = fromVersion; version < policy.CurrentVersion; version++)
        {
            var step = steps[(documentKind, version)];
            try { current = step.Upcast(current) ?? throw new InvalidOperationException("The upcaster returned null."); }
            catch (DocumentSchemaVersionException) { throw; }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                throw Invalid(DocumentSchemaVersionFailure.UpcastFailed,
                    $"Upcaster '{step.GetType().FullName}' failed for document kind '{documentKind}' at version {version}.",
                    documentKind, parsed: version, minimum: policy.MinimumReadableVersion,
                    current: policy.CurrentVersion, inner: exception);
            }
        }
        return current;
    }

    private static DocumentSchemaVersionException InvalidChain(DocumentSchemaVersionPolicy policy, int version, string message) =>
        Invalid(DocumentSchemaVersionFailure.InvalidUpcasterChain, message, policy.DocumentKind, version, policy.MinimumReadableVersion, policy.CurrentVersion);

    private static DocumentSchemaVersionException Invalid(
        DocumentSchemaVersionFailure failure,
        string message,
        string? kind = null,
        int? parsed = null,
        int? minimum = null,
        int? current = null,
        Exception? inner = null) =>
        new(failure, message, kind, parsedVersion: parsed, minimumReadableVersion: minimum, currentVersion: current, innerException: inner);
}
