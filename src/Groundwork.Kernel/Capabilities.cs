using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Groundwork.Kernel;

/// <summary>Stable, namespaced identifier for a provider capability.</summary>
public readonly partial record struct CapabilityId
{
    private static readonly Regex NamespacedForm = CapabilityIdPattern();

    public CapabilityId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException(
                "Capability id must be a non-empty namespaced value (for example 'vendor.area.name').",
                nameof(value));
        if (!NamespacedForm.IsMatch(value))
        {
            throw new ArgumentException(
                $"Capability id '{value}' must be a dotted, lowercase, namespaced value of the form 'vendor.area.name'.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public string Namespace => Value[..Value.IndexOf('.')];

    public bool Equals(CapabilityId other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static implicit operator string(CapabilityId id) => id.Value;

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityIdPattern();
}

/// <summary>Describes a capability and its default evidence policy.</summary>
public sealed record CapabilityDescriptor(
    CapabilityId Id,
    string DisplayName,
    string Description,
    bool EvidenceGatedByDefault = false,
    string OwningModule = "groundwork")
{
    public bool Equals(CapabilityDescriptor? other) => other is not null && Id.Equals(other.Id);

    public override int GetHashCode() => Id.GetHashCode();
}

public interface ICapabilityRegistry
{
    bool IsRegistered(CapabilityId id);

    bool TryGet(CapabilityId id, out CapabilityDescriptor descriptor);

    CapabilityDescriptor Get(CapabilityId id);

    IReadOnlyCollection<CapabilityDescriptor> Descriptors { get; }
}

public interface ICapabilityRegistryBuilder
{
    ICapabilityRegistryBuilder Add(CapabilityDescriptor descriptor);
}

/// <summary>Registry of built-in and module-contributed capability descriptors.</summary>
public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly IReadOnlyDictionary<CapabilityId, CapabilityDescriptor> descriptors;

    private CapabilityRegistry(IReadOnlyDictionary<CapabilityId, CapabilityDescriptor> descriptors) =>
        this.descriptors = descriptors;

    public static CapabilityRegistry Default { get; } = CreateBuilder().Build();

    public IReadOnlyCollection<CapabilityDescriptor> Descriptors => descriptors.Values.ToImmutableArray();

    public bool IsRegistered(CapabilityId id) => descriptors.ContainsKey(id);

    public bool TryGet(CapabilityId id, out CapabilityDescriptor descriptor) =>
        descriptors.TryGetValue(id, out descriptor!);

    public CapabilityDescriptor Get(CapabilityId id) =>
        descriptors.TryGetValue(id, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Capability '{id}' is not registered.");

    public static Builder CreateBuilder()
    {
        var builder = new Builder();
        foreach (var descriptor in WellKnownCapabilities.All)
            builder.Add(descriptor);
        return builder;
    }

    public sealed class Builder : ICapabilityRegistryBuilder
    {
        private readonly Dictionary<CapabilityId, CapabilityDescriptor> descriptors = [];

        public ICapabilityRegistryBuilder Add(CapabilityDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (descriptors.TryGetValue(descriptor.Id, out var existing))
            {
                if (!existing.Equals(descriptor) ||
                    !string.Equals(existing.OwningModule, descriptor.OwningModule, StringComparison.Ordinal) ||
                    existing.EvidenceGatedByDefault != descriptor.EvidenceGatedByDefault)
                {
                    throw new InvalidOperationException(
                        $"Capability '{descriptor.Id}' is already registered by module '{existing.OwningModule}' with a different definition.");
                }

                return this;
            }

            descriptors.Add(descriptor.Id, descriptor);
            return this;
        }

        public CapabilityRegistry Build() =>
            new(new Dictionary<CapabilityId, CapabilityDescriptor>(descriptors));
    }
}

public interface IGroundworkModule
{
    string Name { get; }

    void RegisterCapabilities(ICapabilityRegistryBuilder builder);
}

/// <summary>Composes module capability contributions into one registry and evidence policy.</summary>
public sealed class GroundworkModuleCatalog
{
    private readonly List<IGroundworkModule> modules = [];

    public GroundworkModuleCatalog Add(IGroundworkModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        modules.Add(module);
        return this;
    }

    public IReadOnlyList<IGroundworkModule> Modules => modules.ToImmutableArray();

    public CapabilityRegistry BuildRegistry()
    {
        var builder = CapabilityRegistry.CreateBuilder();
        foreach (var module in modules)
            module.RegisterCapabilities(builder);
        return builder.Build();
    }

    public (CapabilityRegistry Registry, WorkloadEvidencePolicy EvidencePolicy) Build()
    {
        var registry = BuildRegistry();
        return (registry, WorkloadEvidencePolicy.FromRegistry(registry));
    }
}

public sealed record WorkloadEvidencePolicy
{
    public WorkloadEvidencePolicy(IReadOnlySet<CapabilityId> evidenceGatedCapabilities)
    {
        ArgumentNullException.ThrowIfNull(evidenceGatedCapabilities);
        EvidenceGatedCapabilities = evidenceGatedCapabilities.ToImmutableHashSet();
    }

    public IReadOnlySet<CapabilityId> EvidenceGatedCapabilities { get; }

    public void Deconstruct(out IReadOnlySet<CapabilityId> evidenceGatedCapabilities) =>
        evidenceGatedCapabilities = EvidenceGatedCapabilities;

    public static WorkloadEvidencePolicy Default { get; } = FromRegistry(CapabilityRegistry.Default);

    public static WorkloadEvidencePolicy FromRegistry(ICapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new(registry.Descriptors
            .Where(descriptor => descriptor.EvidenceGatedByDefault)
            .Select(descriptor => descriptor.Id)
            .ToHashSet());
    }
}

public abstract record ProviderFit
{
    private ProviderFit()
    {
    }

    public sealed record Supported : ProviderFit;

    public sealed record RequiresEvidence : ProviderFit
    {
        public RequiresEvidence(IReadOnlyList<string> reasons) => Reasons = Snapshot(reasons);

        public IReadOnlyList<string> Reasons { get; }

        public void Deconstruct(out IReadOnlyList<string> reasons) => reasons = Reasons;
    }

    public sealed record Unsupported : ProviderFit
    {
        public Unsupported(IReadOnlyList<CapabilityId> missingRequirements) =>
            MissingRequirements = Snapshot(missingRequirements);

        public IReadOnlyList<CapabilityId> MissingRequirements { get; }

        public void Deconstruct(out IReadOnlyList<CapabilityId> missingRequirements) =>
            missingRequirements = MissingRequirements;
    }

    private static ImmutableArray<T> Snapshot<T>(IReadOnlyList<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.ToImmutableArray();
    }
}

public sealed record ProviderIdentity(string Name, string Version)
{
    public override string ToString() => $"{Name} {Version}";
}

public sealed record ProviderCapabilityReport
{
    public ProviderCapabilityReport(
        ProviderIdentity provider,
        IReadOnlySet<CapabilityId> supportedCapabilities,
        IReadOnlySet<CapabilityId> evidencedCapabilities,
        IReadOnlyList<string> warnings)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        SupportedCapabilities = Snapshot(supportedCapabilities);
        EvidencedCapabilities = Snapshot(evidencedCapabilities);
        Warnings = Snapshot(warnings);
    }

    public ProviderIdentity Provider { get; init; }
    public IReadOnlySet<CapabilityId> SupportedCapabilities
    {
        get => supportedCapabilities;
        init => supportedCapabilities = Snapshot(value);
    }

    public IReadOnlySet<CapabilityId> EvidencedCapabilities
    {
        get => evidencedCapabilities;
        init => evidencedCapabilities = Snapshot(value);
    }

    public IReadOnlyList<string> Warnings
    {
        get => warnings;
        init => warnings = Snapshot(value);
    }

    private IReadOnlySet<CapabilityId> supportedCapabilities = null!;
    private IReadOnlySet<CapabilityId> evidencedCapabilities = null!;
    private IReadOnlyList<string> warnings = null!;

    public void Deconstruct(
        out ProviderIdentity provider,
        out IReadOnlySet<CapabilityId> supportedCapabilities,
        out IReadOnlySet<CapabilityId> evidencedCapabilities,
        out IReadOnlyList<string> warnings)
    {
        provider = Provider;
        supportedCapabilities = SupportedCapabilities;
        evidencedCapabilities = EvidencedCapabilities;
        warnings = Warnings;
    }

    public ProviderCapabilityReport WithCapabilities(params CapabilityId[] capabilities) =>
        this with
        {
            SupportedCapabilities = SupportedCapabilities.Concat(capabilities).ToImmutableHashSet(),
            EvidencedCapabilities = EvidencedCapabilities.Concat(capabilities).ToImmutableHashSet()
        };

    private static ImmutableHashSet<CapabilityId> Snapshot(IEnumerable<CapabilityId> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.ToImmutableHashSet();
    }

    private static ImmutableArray<string> Snapshot(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.ToImmutableArray();
    }
}

public sealed record CapabilityValidationIssue(string Code, string Message, string Target, bool IsError)
{
    public static CapabilityValidationIssue Error(string code, string message, string target) =>
        new(code, message, target, IsError: true);

    public static CapabilityValidationIssue Warning(string code, string message, string target) =>
        new(code, message, target, IsError: false);
}

public sealed record CapabilityCompatibilityResult
{
    public CapabilityCompatibilityResult(IReadOnlyList<CapabilityValidationIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(issues);
        Issues = issues.ToImmutableArray();
    }

    public IReadOnlyList<CapabilityValidationIssue> Issues { get; }

    public void Deconstruct(out IReadOnlyList<CapabilityValidationIssue> issues) => issues = Issues;

    public bool IsCompatible => Issues.All(issue => !issue.IsError);

    public IReadOnlyList<CapabilityValidationIssue> Errors => Issues.Where(issue => issue.IsError).ToImmutableArray();

    public static CapabilityCompatibilityResult Compatible { get; } = new([]);
}

/// <summary>Evaluates provider fit from typed capability ids, with no manifest dependency.</summary>
public sealed class ProviderCapabilityValidator
{
    private readonly ICapabilityRegistry registry;

    public ProviderCapabilityValidator()
        : this(CapabilityRegistry.Default)
    {
    }

    public ProviderCapabilityValidator(ICapabilityRegistry registry) =>
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public ProviderFit Evaluate(
        IEnumerable<CapabilityId> requirements,
        ProviderCapabilityReport capabilities,
        WorkloadEvidencePolicy? policy = null)
    {
        var required = Snapshot(requirements);
        ArgumentNullException.ThrowIfNull(capabilities);
        policy ??= WorkloadEvidencePolicy.FromRegistry(registry);

        var evaluation = EvaluateRequirements(required, capabilities, policy);
        return evaluation.Missing.Length != 0
            ? new ProviderFit.Unsupported(evaluation.Missing)
            : evaluation.NeedsEvidence.Length == 0
            ? new ProviderFit.Supported()
            : new ProviderFit.RequiresEvidence(evaluation.NeedsEvidence);
    }

    public CapabilityCompatibilityResult Validate(
        IEnumerable<CapabilityId> requirements,
        ProviderCapabilityReport capabilities)
    {
        var required = Snapshot(requirements);
        ArgumentNullException.ThrowIfNull(capabilities);
        var diagnostics = new List<CapabilityValidationIssue>();

        foreach (var warning in capabilities.Warnings)
            diagnostics.Add(CapabilityValidationIssue.Warning("GW-CAP-002", warning, "provider.warnings"));

        foreach (var requirement in required.Where(requirement => !registry.IsRegistered(requirement)))
        {
            diagnostics.Add(CapabilityValidationIssue.Error(
                "GW-CAP-014",
                $"Required capability '{requirement}' is not registered. Register it via an IGroundworkModule before validating.",
                "requirements"));
        }

        var evaluation = EvaluateRequirements(
            required,
            capabilities,
            WorkloadEvidencePolicy.FromRegistry(registry));
        if (evaluation.Missing.Length != 0)
        {
            diagnostics.Add(CapabilityValidationIssue.Error(
                "GW-CAP-004",
                $"Provider does not support required capabilities: {string.Join(", ", evaluation.Missing.Select(requirement => requirement.Value))}.",
                "requirements"));
        }

        if (evaluation.NeedsEvidence.Length != 0)
        {
            diagnostics.Add(CapabilityValidationIssue.Error(
                "GW-CAP-013",
                $"Provider requires evidence before serving the required capabilities: {string.Join(" ", evaluation.NeedsEvidence)}",
                "requirements"));
        }

        return diagnostics.Count == 0
            ? CapabilityCompatibilityResult.Compatible
            : new CapabilityCompatibilityResult(diagnostics);
    }

    public CapabilityCompatibilityResult ValidateRuntimeFit(
        IEnumerable<CapabilityId> requirements,
        ProviderCapabilityReport capabilities) => Validate(requirements, capabilities);

    private static CapabilityId[] Snapshot(IEnumerable<CapabilityId> requirements) =>
        (requirements ?? throw new ArgumentNullException(nameof(requirements)))
            .Distinct()
            .ToArray();

    private static CapabilityEvaluation EvaluateRequirements(
        IReadOnlyList<CapabilityId> required,
        ProviderCapabilityReport capabilities,
        WorkloadEvidencePolicy policy)
    {
        var missing = required
            .Where(requirement => !capabilities.SupportedCapabilities.Contains(requirement))
            .OrderBy(requirement => requirement.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var needsEvidence = required
            .Where(requirement => capabilities.SupportedCapabilities.Contains(requirement) &&
                                  policy.EvidenceGatedCapabilities.Contains(requirement) &&
                                  !capabilities.EvidencedCapabilities.Contains(requirement))
            .OrderBy(requirement => requirement.Value, StringComparer.Ordinal)
            .Select(requirement => EvidenceReason(requirement.Value))
            .ToImmutableArray();
        return new CapabilityEvaluation(missing, needsEvidence);
    }

    private sealed record CapabilityEvaluation(
        ImmutableArray<CapabilityId> Missing,
        ImmutableArray<string> NeedsEvidence);

    private static string EvidenceReason(string requirement) =>
        $"Requirement '{requirement}' is evidence-gated; the provider must supply benchmark or operational evidence before serving it.";
}

public static class WellKnownCapabilities
{
    public static readonly CapabilityId AtomicCommit = new("groundwork.operational.atomic-commit");

    public static IReadOnlyList<CapabilityDescriptor> All { get; } = ImmutableArray.Create(
        new CapabilityDescriptor(
            AtomicCommit,
            "Atomic commit",
            "Cross-unit atomic commit across storage units.",
            EvidenceGatedByDefault: true));
}
