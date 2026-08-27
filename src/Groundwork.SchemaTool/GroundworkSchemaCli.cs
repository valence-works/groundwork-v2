using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Schema;

namespace Groundwork.SchemaTool;

public static class SchemaToolExitCodes
{
    public const int Success = 0;
    public const int PendingChanges = 2;
    public const int ValidationFailed = 3;
    public const int AuthorizationRequired = 4;
    public const int InvalidInvocation = 5;
    public const int ExecutionFailed = 10;
    public const int Cancelled = 130;
}

public static class SchemaToolAuthorization
{
    public static PhysicalSchemaPlanAuthorization Evaluate(
        PhysicalSchemaDiffPlan plan,
        bool safeAuthorized,
        IReadOnlySet<string>? destructiveOperationAuthorizations = null,
        IReadOnlySet<string>? semanticMigrationAuthorizations = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
        var authorized = destructiveOperationAuthorizations ?? new HashSet<string>(StringComparer.Ordinal);
        var semantic = semanticMigrationAuthorizations ?? new HashSet<string>(StringComparer.Ordinal);
        var refusals = new List<SchemaRefusal>();
        if (!safeAuthorized && plan.Operations.Length != 0)
            refusals.Add(new("GW-CLI-007", "Schema changes require explicit --safe authorization.", "authorization.safe"));
        refusals.AddRange(protection.DestructiveOperationIdentities
            .Where(identity => !authorized.Contains(identity))
            .Select(identity => new SchemaRefusal(
                "GW-CLI-008",
                $"Destructive operation '{identity}' requires explicit authorization.",
                $"authorization.destructive.{identity}")));
        refusals.AddRange(protection.SemanticMigrationIdentities
            .Where(identity => !semantic.Contains(identity))
            .Select(identity => new SchemaRefusal(
                "GW-CLI-012",
                $"Semantic migration '{identity}' requires explicit authorization.",
                $"authorization.semantic.{identity}")));
        return refusals.Count == 0
            ? PhysicalSchemaPlanAuthorization.Allow
            : PhysicalSchemaPlanAuthorization.Deny(refusals);
    }
}

public static class GroundworkSchemaCli
{
    public static Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default) =>
        RunAsync(arguments, output, error, provider => DiscoverProvider(arguments, provider, cancellationToken), cancellationToken);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        TextWriter error,
        Func<string, ISchemaToolProviderSession?> providerResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(providerResolver);
        if (arguments.Count == 1 && arguments[0] is "--help" or "-h")
        {
            await output.WriteAsync(HelpText);
            return SchemaToolExitCodes.Success;
        }
        if (arguments.Count == 1 && arguments[0] == "--version")
        {
            var version = typeof(GroundworkSchemaCli).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion.Split('+', 2)[0] ?? "unknown";
            await output.WriteLineAsync($"Groundwork.Tool {version}");
            return SchemaToolExitCodes.Success;
        }

        var json = arguments.Contains("--output", StringComparer.Ordinal) &&
                   Value(arguments, "--output")?.Equals("json", StringComparison.OrdinalIgnoreCase) == true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments.Count >= 2 && arguments[0] == "schema" && arguments[1] == "emit")
                return await EmitAsync(arguments, output, json, cancellationToken);

            if (arguments.Count == 0 || arguments[0] is not ("plan" or "validate" or "status" or "apply"))
                throw new SchemaToolInvocationException("GW-CLI-001", "Command options are invalid. Run '--help'.");
            var command = arguments[0];
            ValidateInvocation(arguments, command, 1);
            var schemaPath = RequiredValue(arguments, "--schema");
            var providerName = RequiredValue(arguments, "--provider");
            var schemaJson = await File.ReadAllTextAsync(schemaPath, cancellationToken);
            var coveragePath = Value(arguments, "--coverage");
            var verification = SchemaVerifier.Verify(
                schemaJson,
                coveragePath is null ? null : await File.ReadAllTextAsync(coveragePath, cancellationToken));
            if (!verification.Succeeded)
            {
                await WriteAsync(output, json, new SchemaToolReport(
                    command, "blocked", command == "validate"
                        ? arguments.Contains("--offline", StringComparer.Ordinal) ? "offline" : "live"
                        : null,
                    providerName, null, [], verification.Errors));
                return SchemaToolExitCodes.ValidationFailed;
            }

            if (command == "validate" && arguments.Contains("--offline", StringComparer.Ordinal))
            {
                await WriteAsync(output, json, new SchemaToolReport(
                    command, "ready", "offline", providerName, null, [], []));
                return SchemaToolExitCodes.Success;
            }

            using var provider = providerResolver(providerName)
                ?? throw new SchemaToolInvocationException(
                    "GW-CLI-006",
                    $"No provider plug-in is registered for '{providerName}'.");
            var schema = GroundworkSchemaCanonical.Read(schemaJson);
            IReadOnlyList<PhysicalSchemaTarget> targets;
            try
            {
                targets = SchemaCompilation.CompileTargets(schema, provider.Targets);
            }
            catch (InvalidOperationException exception)
            {
                // A provider raises its physicalization refusals as InvalidOperationException;
                // they name a code and path, so they belong with the validation failures.
                await WriteErrorAsync(output, error, json, "GW-CLI-005", exception.Message);
                return SchemaToolExitCodes.ValidationFailed;
            }
            var targetReports = new List<SchemaToolTargetReport>();

            if (command == "apply")
            {
                var safe = arguments.Contains("--safe", StringComparer.Ordinal);
                var expectedPlans = Values(arguments, "--expected-plan").ToHashSet(StringComparer.Ordinal);
                if (!safe && expectedPlans.Count == 0)
                    throw new SchemaToolInvocationException(
                        "GW-CLI-001",
                        "Apply requires --safe or an exact --expected-plan authorization mode.");
                var destructive = Values(arguments, "--allow-destructive")
                    .Concat(Values(arguments, "--authorize-destructive")).ToHashSet(StringComparer.Ordinal);
                var semantic = Values(arguments, "--allow-semantic")
                    .Concat(Values(arguments, "--authorize-semantic")).ToHashSet(StringComparer.Ordinal);
                PhysicalSchemaPlanAuthorization Authorize(
                    PhysicalSchemaTarget target,
                    PhysicalSchemaDiffPlan plan)
                {
                    var fingerprint = PlanFingerprint(target, plan);
                    var exact = expectedPlans.Contains(fingerprint);
                    var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
                    if (!protection.IsSafe && !exact)
                    {
                        return PhysicalSchemaPlanAuthorization.Deny([
                            new SchemaRefusal(
                                "GW-CLI-011",
                                "Destructive and semantic authorization requires the exact current plan fingerprint.",
                                "authorization.plan")
                        ]);
                    }
                    return SchemaToolAuthorization.Evaluate(
                        plan,
                        safe || exact,
                        exact ? destructive : null,
                        exact ? semantic : null);
                }

                var preflight = targets.Select(target =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var inspection = provider.Inspector.InspectHistory(target);
                    var plan = PhysicalSchemaDiffPlanner.Plan(target, inspection.History, DateTimeOffset.UnixEpoch);
                    return (Target: target, Inspection: inspection, Plan: plan, Authorization: Authorize(target, plan));
                }).ToArray();
                if (preflight.Any(item => !item.Inspection.IsAppliedSchemaValid || !item.Plan.IsApplicable))
                {
                    targetReports.AddRange(preflight.Select(item => FromPlan(
                        item.Target,
                        item.Plan,
                        item.Inspection)));
                    await WriteAsync(output, json, new SchemaToolReport(
                        command, "blocked", null,
                        provider.Provider.Name, provider.Provider.Version, targetReports, []));
                    return SchemaToolExitCodes.ValidationFailed;
                }
                if (preflight.Any(item => !item.Authorization.IsAuthorized))
                {
                    targetReports.AddRange(preflight.Select(item => FromPlan(
                        item.Target,
                        item.Plan,
                        item.Inspection,
                        item.Authorization.Refusals)));
                    await WriteAsync(output, json, new SchemaToolReport(
                        command, "authorization-required", null,
                        provider.Provider.Name, provider.Provider.Version, targetReports, []));
                    return SchemaToolExitCodes.AuthorizationRequired;
                }

                foreach (var target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = PhysicalSchemaApplication.Apply(
                        target,
                        provider.Executor,
                        planAuthorization: plan => Authorize(target, plan));
                    targetReports.Add(FromApplication(target, result));
                }
                var authorizationRequired = targetReports.Any(target => target.Outcome == "authorization-required");
                var blocked = targetReports.Any(target => target.Outcome == "blocked");
                var outcome = authorizationRequired ? "authorization-required" : blocked ? "blocked" : "applied";
                await WriteAsync(output, json, new SchemaToolReport(
                    command, outcome, null, provider.Provider.Name, provider.Provider.Version, targetReports, []));
                return authorizationRequired
                    ? SchemaToolExitCodes.AuthorizationRequired
                    : blocked ? SchemaToolExitCodes.ValidationFailed : SchemaToolExitCodes.Success;
            }

            var pending = false;
            var invalid = false;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inspection = provider.Inspector.InspectHistory(target);
                var plan = PhysicalSchemaDiffPlanner.Plan(target, inspection.History, DateTimeOffset.UnixEpoch);
                pending |= plan.Operations.Length != 0;
                invalid |= !inspection.IsAppliedSchemaValid || !plan.IsApplicable;
                targetReports.Add(FromPlan(target, plan, inspection));
            }
            var reportOutcome = invalid ? "blocked" : command == "validate" ? "ready" : pending ? "pending" : "ready";
            await WriteAsync(output, json, new SchemaToolReport(
                command, reportOutcome, command == "validate" ? "live" : null,
                provider.Provider.Name, provider.Provider.Version, targetReports, []));
            if (invalid)
                return SchemaToolExitCodes.ValidationFailed;
            return command != "validate" && pending
                ? SchemaToolExitCodes.PendingChanges
                : SchemaToolExitCodes.Success;
        }
        catch (SchemaToolInvocationException exception)
        {
            await WriteErrorAsync(output, error, json, exception.Code, exception.Message);
            return SchemaToolExitCodes.InvalidInvocation;
        }
        catch (SchemaToolProviderInvocationException exception)
        {
            await WriteErrorAsync(output, error, json, "GW-CLI-001", exception.Message);
            return SchemaToolExitCodes.InvalidInvocation;
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(output, error, json, "GW-CLI-009", "The operation was cancelled.");
            return SchemaToolExitCodes.Cancelled;
        }
        catch (GroundworkSchemaBoundaryException exception)
        {
            await WriteErrorAsync(output, error, json, GroundworkSchemaBoundaryException.Code, exception.Message);
            return SchemaToolExitCodes.ValidationFailed;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            await WriteErrorAsync(output, error, json, "GW-CLI-005", exception.Message);
            return SchemaToolExitCodes.ValidationFailed;
        }
        catch (SchemaToolProviderException exception)
        {
            await WriteErrorAsync(output, error, json, "GW-CLI-010", $"Schema tool execution failed: {exception.Message}");
            return SchemaToolExitCodes.ExecutionFailed;
        }
        catch (Exception)
        {
            await WriteErrorAsync(output, error, json, "GW-CLI-010", "Schema tool execution failed.");
            return SchemaToolExitCodes.ExecutionFailed;
        }
    }

    private static async Task<int> EmitAsync(
        IReadOnlyList<string> arguments,
        TextWriter output,
        bool json,
        CancellationToken cancellationToken)
    {
        ValidateInvocation(arguments, "schema emit", 2);
        var input = RequiredValue(arguments, "--input");
        var destination = RequiredValue(arguments, "--file");
        var schema = GroundworkSchemaCanonical.Read(await File.ReadAllTextAsync(input, cancellationToken));
        var canonical = GroundworkSchemaCanonical.Emit(schema);
        await File.WriteAllTextAsync(destination, canonical, cancellationToken);
        var fingerprint = GroundworkSchemaCanonical.Fingerprint(schema);
        await WriteAsync(output, json, new
        {
            command = "schema emit",
            outcome = "written",
            file = destination,
            fingerprint
        });
        return SchemaToolExitCodes.Success;
    }

    private static SchemaToolTargetReport FromPlan(
        PhysicalSchemaTarget target,
        PhysicalSchemaDiffPlan plan,
        PhysicalSchemaInspectionResult inspection,
        IEnumerable<SchemaRefusal>? authorizationRefusals = null) => new(
        target.Subject.Id.Value,
        target.Fingerprint,
        authorizationRefusals?.Any() == true
            ? "authorization-required"
            : !inspection.IsAppliedSchemaValid || !plan.IsApplicable
                ? "blocked"
                : plan.Operations.Length == 0 ? "ready" : "pending",
        PlanFingerprint(target, plan),
        inspection.History.AppliedState?.TargetFingerprint,
        plan.Operations.Select(SchemaToolOperationReport.FromPending).ToArray(),
        inspection.History.AppliedState?.AppliedOperations.Select(SchemaToolOperationReport.FromApplied).ToArray() ?? [],
        plan.Refusals.Concat(authorizationRefusals ?? [])
            .Select(refusal => new SchemaVerificationError(refusal.Code, refusal.Message, refusal.Path)).ToArray(),
        false);

    private static SchemaToolTargetReport FromApplication(
        PhysicalSchemaTarget target,
        PhysicalSchemaApplicationResult result) => new(
        target.Subject.Id.Value,
        target.Fingerprint,
        result.Outcome switch
        {
            PhysicalSchemaApplicationOutcome.Rejected => "blocked",
            PhysicalSchemaApplicationOutcome.AuthorizationRequired => "authorization-required",
            PhysicalSchemaApplicationOutcome.NoChanges => "ready",
            _ => "applied"
        },
        PlanFingerprint(target, result.Plan),
        result.AppliedState?.TargetFingerprint,
        result.Outcome == PhysicalSchemaApplicationOutcome.Applied
            ? []
            : result.Plan.Operations.Select(SchemaToolOperationReport.FromPending).ToArray(),
        result.AppliedState?.AppliedOperations.Select(SchemaToolOperationReport.FromApplied).ToArray() ?? [],
        result.Plan.Refusals.Concat(result.AuthorizationRefusals)
            .Select(refusal => new SchemaVerificationError(refusal.Code, refusal.Message, refusal.Path)).ToArray(),
        result.Outcome == PhysicalSchemaApplicationOutcome.Applied);

    private static string PlanFingerprint(PhysicalSchemaTarget target, PhysicalSchemaDiffPlan plan)
    {
        var parts = new[] { target.Fingerprint, plan.ExpectedAppliedTargetFingerprint ?? string.Empty }
            .Concat(plan.Operations.SelectMany(operation => new[] { operation.Identity, operation.Fingerprint }));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
    }

    private static async Task WriteErrorAsync(
        TextWriter output,
        TextWriter error,
        bool json,
        string code,
        string message)
    {
        if (json)
            await WriteAsync(output, true, new SchemaToolReport("unknown", "invalid", null, null, null, [],
                [new SchemaVerificationError(code, message, "invocation")]));
        else
            await error.WriteLineAsync($"{code}: {message}");
    }

    private static Task WriteAsync(TextWriter output, bool json, object report) => json
        ? output.WriteLineAsync(JsonSerializer.Serialize(Json(report), new JsonSerializerOptions
          {
              PropertyNamingPolicy = JsonNamingPolicy.CamelCase
          }))
        : output.WriteLineAsync(Human(report));

    private static object Json(object report)
    {
        if (report is not SchemaToolReport value)
            return report;
        var pending = value.Targets.SelectMany(target => target.PendingOperations).ToArray();
        var applied = value.Targets.SelectMany(target => target.AppliedOperations).ToArray();
        var destructive = pending.Where(operation => operation.IsDestructive)
            .Select(operation => operation.Identity).Distinct(StringComparer.Ordinal).Order().ToArray();
        var semantic = pending.Select(operation => operation.SemanticMigrationIdentity)
            .Where(identity => identity is not null).Cast<string>()
            .Distinct(StringComparer.Ordinal).Order().ToArray();
        object? target = value.Targets.Count == 1
            ? new { subject = value.Targets[0].Subject, fingerprint = value.Targets[0].Fingerprint }
            : null;
        return new
        {
            schemaVersion = "1",
            value.Command,
            value.Outcome,
            inspectionMode = value.InspectionMode,
            provider = new { name = value.Provider, version = value.ProviderVersion },
            target,
            planFingerprint = value.Targets.Count == 1 ? value.Targets[0].PlanFingerprint : null,
            appliedTargetFingerprint = value.Targets.Count == 1 ? value.Targets[0].AppliedTargetFingerprint : null,
            targets = value.Targets,
            resolvedNames = Array.Empty<object>(),
            pendingOperations = pending,
            appliedOperations = applied.OrderBy(operation => operation.Identity, StringComparer.Ordinal),
            authorization = new
            {
                destructiveRequired = destructive.Length != 0,
                destructiveOperationsRequired = destructive,
                semanticRequired = semantic
            },
            diagnosticRecords = (object?)null,
            diagnostics = value.Diagnostics.Concat(value.Targets.SelectMany(item => item.Diagnostics)).Select(Error),
            targetMutated = value.Targets.Any(item => item.Mutated)
        };
    }

    private static object Error(SchemaVerificationError error) => new
    {
        severity = "error",
        error.Code,
        error.Message,
        target = error.Path
    };

    private static string Human(object report) => report switch
    {
        SchemaToolReport value => string.Join(Environment.NewLine,
            new[]
            {
                $"Groundwork schema {value.Command}: {value.Outcome}",
                $"Provider: {value.Provider ?? "unresolved"}{(value.ProviderVersion is null ? string.Empty : "@" + value.ProviderVersion)}",
                $"Targets: {value.Targets.Count}",
                $"Pending operations: {value.Targets.Sum(target => target.PendingOperations.Count)}",
                $"Applied operations: {value.Targets.Sum(target => target.AppliedOperations.Count)}"
            }.Concat(value.Diagnostics.Select(diagnostic =>
                $"error {diagnostic.Code}: {diagnostic.Message} ({diagnostic.Path})"))),
        _ => "Groundwork schema emit: written"
    };

    private static string RequiredValue(IReadOnlyList<string> arguments, string option) =>
        Value(arguments, option) ?? throw new SchemaToolInvocationException(
            "GW-CLI-001", $"Option '{option}' requires a value.");

    private static string? Value(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
            if (arguments[index] == option && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                return arguments[index + 1];
        return null;
    }

    private static IEnumerable<string> Values(IReadOnlyList<string> arguments, string option)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
            if (arguments[index] == option && !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                yield return arguments[index + 1];
    }

    private static void ValidateInvocation(
        IReadOnlyList<string> arguments,
        string command,
        int optionStart)
    {
        var flags = command switch
        {
            "validate" => new HashSet<string>(["--offline"], StringComparer.Ordinal),
            "apply" => new HashSet<string>(["--safe"], StringComparer.Ordinal),
            _ => new HashSet<string>(StringComparer.Ordinal)
        };
        var values = command switch
        {
            "schema emit" => new HashSet<string>(["--input", "--file", "--output"], StringComparer.Ordinal),
            "apply" => new HashSet<string>([
                "--schema", "--provider", "--connection", "--database", "--provider-assembly",
                "--coverage", "--output", "--expected-plan", "--allow-destructive", "--allow-semantic",
                "--authorize-destructive", "--authorize-semantic"
            ], StringComparer.Ordinal),
            _ => new HashSet<string>([
                "--schema", "--provider", "--connection", "--database", "--provider-assembly",
                "--coverage", "--output"
            ], StringComparer.Ordinal)
        };
        var repeatable = new HashSet<string>([
            "--provider-assembly", "--expected-plan", "--allow-destructive", "--allow-semantic",
            "--authorize-destructive", "--authorize-semantic"
        ], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = optionStart; index < arguments.Count; index++)
        {
            var option = arguments[index];
            if (flags.Contains(option))
            {
                if (!seen.Add(option))
                    throw InvalidOptions();
                continue;
            }
            if (!values.Contains(option) || index + 1 >= arguments.Count ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal) ||
                (!repeatable.Contains(option) && !seen.Add(option)))
                throw InvalidOptions();
            if (option == "--output" && arguments[index + 1] is not ("json" or "human"))
                throw InvalidOptions();
            index++;
        }
    }

    private static SchemaToolInvocationException InvalidOptions() => new(
        "GW-CLI-001",
        "Command options are invalid. Run '--help'.");

    private static ISchemaToolProviderSession? DiscoverProvider(
        IReadOnlyList<string> arguments,
        string provider,
        CancellationToken cancellationToken)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
        foreach (var path in Values(arguments, "--provider-assembly"))
        {
            var fullPath = Path.GetFullPath(path);
            if (!assemblies.Any(assembly => string.Equals(assembly.Location, fullPath, StringComparison.Ordinal)))
                assemblies.Add(Assembly.LoadFrom(fullPath));
        }

        var factoryType = typeof(ISchemaToolProviderSessionFactory);
        var factory = assemblies.SelectMany(LoadableTypes)
            .Where(type => !type.IsAbstract && !type.IsInterface && factoryType.IsAssignableFrom(type))
            .Select(type => Activator.CreateInstance(type) as ISchemaToolProviderSessionFactory)
            .FirstOrDefault(candidate => candidate is not null &&
                                         string.Equals(candidate.Alias, provider, StringComparison.OrdinalIgnoreCase));
        return factory?.Open(new SchemaToolProviderOptions(
            provider,
            Value(arguments, "--connection"),
            Value(arguments, "--database"),
            arguments.Count != 0 && arguments[0] == "apply",
            cancellationToken));
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }

    private const string HelpText = """
        Usage: groundwork <plan|validate|status|apply> --schema <file> --provider <name> [--connection <value>]
               [--database <name>] [--provider-assembly <file>] [--coverage <file>] [--output json|human]
               groundwork schema emit --input <file> --file <file> [--output json|human]

        Apply requires --safe, or exact --expected-plan authorization. Destructive operation identities additionally
        require --allow-destructive <identity>; semantic migrations require --allow-semantic <identity>.
        Runtime admission remains inspect-only unless AutoApplyOnStartup is explicitly enabled by the host.
        """;

    private sealed class SchemaToolInvocationException(string code, string message) : Exception(message)
    {
        internal string Code { get; } = code;
    }
}

public sealed record SchemaToolReport(
    string Command,
    string Outcome,
    string? InspectionMode,
    string? Provider,
    string? ProviderVersion,
    IReadOnlyList<SchemaToolTargetReport> Targets,
    IReadOnlyList<SchemaVerificationError> Diagnostics);

public sealed record SchemaToolTargetReport(
    string Subject,
    string Fingerprint,
    string Outcome,
    string PlanFingerprint,
    string? AppliedTargetFingerprint,
    IReadOnlyList<SchemaToolOperationReport> PendingOperations,
    IReadOnlyList<SchemaToolOperationReport> AppliedOperations,
    IReadOnlyList<SchemaVerificationError> Diagnostics,
    bool Mutated);

public sealed record SchemaToolOperationReport(
    string Identity,
    string Fingerprint,
    string Kind,
    string? StorageUnit,
    string SubjectIdentity,
    bool IsDestructive,
    string? SemanticMigrationIdentity)
{
    internal static SchemaToolOperationReport FromPending(PhysicalSchemaOperation operation) => new(
        operation.Identity,
        operation.Fingerprint,
        operation.Kind.ToString(),
        operation.SubjectId?.Value,
        operation.SubjectIdentity,
        operation.RequiresAuthorization,
        operation.SemanticMigrationId);

    internal static SchemaToolOperationReport FromApplied(PhysicalSchemaAppliedOperation operation) => new(
        operation.Identity,
        operation.Fingerprint,
        operation.Kind.ToString(),
        operation.SubjectId?.Value,
        operation.SubjectIdentity,
        false,
        null);
}
