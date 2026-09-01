namespace Groundwork.Extensions.DependencyInjection;

/// <summary>
/// Stable refusal codes raised by the hosting integration. Like every other Groundwork code these
/// are part of the public contract: branch on <see cref="GroundworkHostingException.Code"/>.
/// </summary>
public static class GroundworkHostingDiagnostics
{
    /// <summary>A storage connection is registered with a lifetime other than singleton.</summary>
    public const string ConnectionLifetime = "GW-HOST-001";

    /// <summary>Two connections were registered under the same name.</summary>
    public const string DuplicateConnectionName = "GW-HOST-002";

    /// <summary>A connection name was requested that was never registered.</summary>
    public const string UnknownConnectionName = "GW-HOST-003";

    /// <summary>A registered connection is missing its provider factory or connection string.</summary>
    public const string IncompleteConnection = "GW-HOST-004";

    /// <summary>Startup admission found physical schema work that must be applied before serving.</summary>
    public const string StartupAdmissionBlocked = "GW-HOST-005";

    /// <summary>The deployed database does not advertise a capability the application requires.</summary>
    public const string CapabilityNotAdvertised = "GW-HOST-006";

    /// <summary>Auto-apply was enabled outside the Development host environment.</summary>
    public const string AutoApplyOnStartupNotAllowed = "GW-HOST-007";
}

/// <summary>A hosting refusal carrying a stable <c>GW-HOST-*</c> code and a named corrective action.</summary>
public sealed class GroundworkHostingException : InvalidOperationException
{
    public GroundworkHostingException(string code, string message)
        : base($"{code}: {message}")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
