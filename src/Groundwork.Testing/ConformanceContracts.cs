namespace Groundwork.Testing;

/// <summary>One named result emitted by the testing conformance harness.</summary>
public sealed record ConformanceCheck(string Name, bool Passed, string? Failure = null);

/// <summary>Aggregate result emitted by the testing conformance harness.</summary>
public sealed class ConformanceReport
{
    public ConformanceReport(IReadOnlyList<ConformanceCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        Checks = Array.AsReadOnly(checks.ToArray());
    }

    public IReadOnlyList<ConformanceCheck> Checks { get; }

    public bool Passed => Checks.All(check => check.Passed);

    public IReadOnlyList<ConformanceCheck> Failures =>
        Array.AsReadOnly(Checks.Where(check => !check.Passed).ToArray());
}

public sealed class ConformanceFailureException : Exception
{
    public ConformanceFailureException(string checkName, string message)
        : base($"Conformance check '{checkName}' failed: {message}")
    {
        CheckName = checkName;
    }

    public string CheckName { get; }
}
