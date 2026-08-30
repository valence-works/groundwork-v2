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
        refusals.AddRange(protection.DestructiveOperations
            .Where(operation => !operation.IsAuthorizedBy(authorized))
            .Select(operation => new SchemaRefusal(
                "GW-CLI-008",
                $"Destructive operation '{operation.Address ?? operation.Identity}' requires explicit authorization.",
                $"authorization.destructive.{operation.Identity}")));
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
    /// <summary>Reported when a target's schema is applied but an attached data migration is not.</summary>
    public const string DataMigrationPendingOutcome = "data-migration-pending";

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

            if (arguments.Count == 0 || arguments[0] is not ("plan" or "validate" or "status" or "apply" or "adopt"))
                throw new SchemaToolInvocationException("GW-CLI-001", "Command options are invalid. Run '--help'.");
            var command = arguments[0];
            ValidateInvocation(arguments, command, 1);
            var schemaPath = RequiredValue(arguments, "--schema");
            var providerName = RequiredValue(arguments, "--provider");
            var phase = Value(arguments, "--phase") switch
            {
                null or "expand" => SchemaEvolutionPhase.Expand,
                "contract" => SchemaEvolutionPhase.Contract,
                _ => throw new SchemaToolInvocationException(
                    "GW-CLI-001", "Option '--phase' accepts 'expand' or 'contract'.")
            };
            var schemaJson = await File.ReadAllTextAsync(schemaPath, cancellationToken);
            var coveragePath = Value(arguments, "--coverage");
            var verification = SchemaVerifier.Verify(
                schemaJson,
                coveragePath is null ? null : await File.ReadAllTextAsync(coveragePath, cancellationToken),
                validateManifest: command == "validate" && arguments.Contains("--offline", StringComparer.Ordinal));
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

            if (command is "apply" or "adopt")
            {
                var safe = arguments.Contains("--safe", StringComparer.Ordinal);
                var expectedPlans = Values(arguments, "--expected-plan").ToHashSet(StringComparer.Ordinal);
                if (!safe && expectedPlans.Count == 0)
                    throw new SchemaToolInvocationException(
                        "GW-CLI-001",
                        $"{command[0..1].ToUpperInvariant()}{command[1..]} requires --safe or an exact " +
                        "--expected-plan authorization mode.");
                var destructive = Values(arguments, "--allow-destructive")
                    .Concat(Values(arguments, "--authorize-destructive")).ToHashSet(StringComparer.Ordinal);
                var semantic = Values(arguments, "--allow-semantic")
                    .Concat(Values(arguments, "--authorize-semantic")).ToHashSet(StringComparer.Ordinal);
                var dataMigrationCatalog = command == "apply"
                    ? provider.DataMigrationCatalog
                    : null;
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
                    var readiness = AssessContractReadiness(provider, target, inspection, phase);
                    var plan = PhysicalSchemaDiffPlanner.Plan(
                        target, inspection.History, DateTimeOffset.UnixEpoch, phase: phase, readiness: readiness);
                    return (Target: target, Inspection: inspection, Plan: plan, Readiness: readiness,
                        Authorization: Authorize(target, plan));
                }).ToArray();
                if (preflight.Any(item => !item.Inspection.IsAppliedSchemaValid || !item.Plan.IsApplicable))
                {
                    targetReports.AddRange(preflight.Select(item => FromPlan(
                        item.Target,
                        item.Plan,
                        item.Inspection,
                        readiness: item.Readiness)));
                    await WriteAsync(output, json, new SchemaToolReport(
                        command, "blocked", null,
                        provider.Provider.Name, provider.Provider.Version, targetReports, [])
                    { Phase = PhaseName(phase) });
                    return SchemaToolExitCodes.ValidationFailed;
                }
                if (preflight.Any(item => !item.Authorization.IsAuthorized))
                {
                    targetReports.AddRange(preflight.Select(item => FromPlan(
                        item.Target,
                        item.Plan,
                        item.Inspection,
                        item.Authorization.Refusals,
                        item.Readiness)));
                    await WriteAsync(output, json, new SchemaToolReport(
                        command, "authorization-required", null,
                        provider.Provider.Name, provider.Provider.Version, targetReports, [])
                    { Phase = PhaseName(phase) });
                    return SchemaToolExitCodes.AuthorizationRequired;
                }

                if (command == "apply")
                {
                    // Resolve every document-declared transform before the first target mutates.
                    // A later target cannot otherwise reveal a missing host transform after an
                    // earlier target has already published its schema.
                    foreach (var item in preflight)
                    {
                        dataMigrationCatalog!.ResolveDeclared(
                            item.Target.Subject.Evolution.SemanticMigrationId,
                            item.Target.Subject.Id);
                    }
                }

                if (command == "adopt")
                {
                    if (provider.Executor is not IPhysicalSchemaCatalogInspector)
                    {
                        // Named rather than generic: a provider that cannot compare a deployed
                        // catalog to a target cannot prove anything, and adoption is the proof.
                        await WriteErrorAsync(
                            output, error, json, "GW-CLI-013",
                            $"Provider '{provider.Provider.Name}' cannot compare a deployed catalog to a " +
                            "compiled target, so it cannot adopt one.");
                        return SchemaToolExitCodes.ValidationFailed;
                    }
                    foreach (var target in targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        targetReports.Add(FromAdoption(target, PhysicalSchemaAdoption.Adopt(
                            target,
                            provider.Executor,
                            planAuthorization: plan => Authorize(target, plan))));
                    }
                    var adoptionBlocked = targetReports.Any(item => item.Outcome == "blocked");
                    var adoptionUnauthorized = targetReports.Any(item => item.Outcome == "authorization-required");
                    // A run where every target was already recorded reports ready, not adopted:
                    // "adopted" has to mean history was written, or a deploy log cannot tell the
                    // two apart.
                    var adoptedAny = targetReports.Any(item => item.Outcome == "adopted");
                    await WriteAsync(output, json, new SchemaToolReport(
                        command,
                        adoptionUnauthorized ? "authorization-required"
                            : adoptionBlocked ? "blocked"
                            : adoptedAny ? "adopted" : "ready",
                        null, provider.Provider.Name, provider.Provider.Version, targetReports, []));
                    if (adoptionUnauthorized)
                        return SchemaToolExitCodes.AuthorizationRequired;
                    return adoptionBlocked ? SchemaToolExitCodes.ValidationFailed : SchemaToolExitCodes.Success;
                }

                foreach (var target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = PhysicalSchemaApplication.Apply(
                        target,
                        provider.Executor,
                        planAuthorization: plan => Authorize(target, plan),
                        dataMigrations: dataMigrationCatalog,
                        phase: phase,
                        dataMigrationExecutor: provider.DataMigrations);
                    targetReports.Add(FromApplication(target, result) with
                    {
                        DataMigrations = ReadDataMigrations(provider, target),
                        Supersessions = Describe(result.ContractReadiness)
                    });
                }
                var authorizationRequired = targetReports.Any(target => target.Outcome == "authorization-required");
                var blocked = targetReports.Any(target => target.Outcome == "blocked");
                var dataMigrationPending = targetReports.Any(target => target.Outcome == DataMigrationPendingOutcome);
                var outcome = authorizationRequired
                    ? "authorization-required"
                    : blocked ? "blocked" : dataMigrationPending ? DataMigrationPendingOutcome : "applied";
                await WriteAsync(output, json, new SchemaToolReport(
                    command, outcome, null, provider.Provider.Name, provider.Provider.Version, targetReports, [])
                { Phase = PhaseName(phase) });
                if (authorizationRequired)
                    return SchemaToolExitCodes.AuthorizationRequired;
                if (blocked)
                    return SchemaToolExitCodes.ValidationFailed;
                // The schema is applied but the data is not, so this exits as pending work rather
                // than as success: a deploy gate that treats 0 as done must not pass here.
                return dataMigrationPending ? SchemaToolExitCodes.PendingChanges : SchemaToolExitCodes.Success;
            }

            var pending = false;
            var invalid = false;
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inspection = provider.Inspector.InspectHistory(target);
                var readiness = AssessContractReadiness(provider, target, inspection, phase);
                var plan = PhysicalSchemaDiffPlanner.Plan(
                    target, inspection.History, DateTimeOffset.UnixEpoch, phase: phase, readiness: readiness);
                var dataMigrations = ReadDataMigrations(provider, target);
                pending |= plan.Operations.Length != 0;
                // A target whose schema is applied still has pending work when a data migration was
                // interrupted, so its resume is reported as pending rather than as ready.
                pending |= dataMigrations.Any(report => report.State == SchemaToolDataMigrationReport.PendingState);
                invalid |= !inspection.IsAppliedSchemaValid || !plan.IsApplicable;
                targetReports.Add(FromPlan(target, plan, inspection, readiness: readiness) with
                {
                    DataMigrations = dataMigrations
                });
            }
            var reportOutcome = invalid ? "blocked" : command == "validate" ? "ready" : pending ? "pending" : "ready";
            await WriteAsync(output, json, new SchemaToolReport(
                command, reportOutcome, command == "validate" ? "live" : null,
                provider.Provider.Name, provider.Provider.Version, targetReports, [])
            { Phase = PhaseName(phase) });
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
        catch (DataMigrationRefusedException exception)
        {
            await WriteErrorAsync(output, error, json, exception.Code, exception.Message);
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
        IEnumerable<SchemaRefusal>? authorizationRefusals = null,
        ContractReadinessAssessment? readiness = null) => new(
        target.Subject.Id.Value,
        target.Fingerprint,
        authorizationRefusals?.Any() == true
            ? "authorization-required"
            : !inspection.IsAppliedSchemaValid || !plan.IsApplicable
                ? "blocked"
                : plan.Operations.Length == 0 ? "ready" : "pending",
        PlanFingerprint(target, plan),
        inspection.History.AppliedState?.TargetFingerprint,
        plan.Operations.Select(operation => SchemaToolOperationReport.FromPending(operation, PhysicalSchemaPlanProtection.Inspect(plan.Operations))).ToArray(),
        inspection.History.AppliedState?.AppliedOperations.Select(SchemaToolOperationReport.FromApplied).ToArray() ?? [],
        plan.Refusals.Concat(authorizationRefusals ?? [])
            .Select(refusal => new SchemaVerificationError(refusal.Code, refusal.Message, refusal.Path)).ToArray(),
        false)
    {
        References = DescribeReferences(target),
        Supersessions = Describe(readiness),
        Warnings = (inspection.ToleratedDrift.IsDefault ? [] : inspection.ToleratedDrift)
            .Select(refusal => new SchemaVerificationError(refusal.Code, refusal.Message, refusal.Path)).ToArray()
    };

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
            PhysicalSchemaApplicationOutcome.DataMigrationIncomplete => DataMigrationPendingOutcome,
            _ => "applied"
        },
        PlanFingerprint(target, result.Plan),
        result.AppliedState?.TargetFingerprint,
        result.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.DataMigrationIncomplete
            ? []
            : result.Plan.Operations.Select(operation => SchemaToolOperationReport.FromPending(operation, PhysicalSchemaPlanProtection.Inspect(result.Plan.Operations))).ToArray(),
        result.AppliedState?.AppliedOperations.Select(SchemaToolOperationReport.FromApplied).ToArray() ?? [],
        result.Plan.Refusals.Concat(result.AuthorizationRefusals)
            .Select(refusal => new SchemaVerificationError(refusal.Code, refusal.Message, refusal.Path)).ToArray(),
        result.Outcome is PhysicalSchemaApplicationOutcome.Applied or PhysicalSchemaApplicationOutcome.DataMigrationIncomplete)
    {
        References = DescribeReferences(target)
    };

    /// <summary>
    /// One adoption's report. An adopted target reports the operations the plan would have executed
    /// as applied, because that is exactly what its published history now records; a refusal reports
    /// the drift by name so an operator sees which column or index differs.
    /// </summary>
    private static SchemaToolTargetReport FromAdoption(
        PhysicalSchemaTarget target,
        PhysicalSchemaAdoptionResult result) => new(
        target.Subject.Id.Value,
        target.Fingerprint,
        result.Outcome switch
        {
            PhysicalSchemaAdoptionOutcome.Adopted => "adopted",
            PhysicalSchemaAdoptionOutcome.AlreadyAdopted => "ready",
            PhysicalSchemaAdoptionOutcome.AuthorizationRequired => "authorization-required",
            _ => "blocked"
        },
        PlanFingerprint(target, result.Plan),
        result.AppliedState?.TargetFingerprint,
        result.Outcome == PhysicalSchemaAdoptionOutcome.Adopted
            ? []
            : result.Plan.Operations
                .Select(operation => SchemaToolOperationReport.FromPending(
                    operation, PhysicalSchemaPlanProtection.Inspect(result.Plan.Operations)))
                .ToArray(),
        result.AppliedState?.AppliedOperations.Select(SchemaToolOperationReport.FromApplied).ToArray() ?? [],
        result.Refusals.Select(refusal => new SchemaVerificationError(refusal.Code, refusal.Message, refusal.Path)).ToArray(),
        result.Outcome == PhysicalSchemaAdoptionOutcome.Adopted)
    {
        References = DescribeReferences(target),
        Warnings = result.ToleratedDrift
            .Select(refusal => new SchemaVerificationError(refusal.Code, refusal.Message, refusal.Path))
            .ToArray()
    };

    private static IReadOnlyList<SchemaToolReferenceReport> DescribeReferences(PhysicalSchemaTarget target) =>
        target.Subject.References
            .OrderBy(reference => reference.Name, StringComparer.Ordinal)
            .Select(reference => new SchemaToolReferenceReport(
                reference.Name,
                reference.TargetUnitId.Value,
                reference.Columns.ToArray()))
            .ToArray();

    /// <summary>
    /// Reports pending versus applied data migrations for one target from provider-owned state. The
    /// tool cannot see host transforms, so a semantic migration the subject declares but the ledger
    /// has never recorded is reported as not-recorded rather than guessed either way.
    /// </summary>
    private static IReadOnlyList<SchemaToolDataMigrationReport> ReadDataMigrations(
        ISchemaToolProviderSession provider,
        PhysicalSchemaTarget target)
    {
        if (provider.DataMigrations is not { } executor)
            return [];
        var recorded = executor.ReadLedgerEntries(target.Identity)
            .Select(SchemaToolDataMigrationReport.From)
            .ToList();
        var declared = target.Subject.Evolution.SemanticMigrationId;
        if (!string.IsNullOrWhiteSpace(declared) &&
            !recorded.Any(report => string.Equals(report.Identity, declared, StringComparison.Ordinal)))
        {
            recorded.Add(new SchemaToolDataMigrationReport(
                declared,
                SchemaToolDataMigrationReport.NotRecordedState,
                target.Subject.Name,
                0, 0, 0, null, null));
        }
        return recorded
            .OrderBy(report => report.State == SchemaToolDataMigrationReport.AppliedState)
            .ThenBy(report => report.Identity, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// Establishes contract readiness from the same durable state the plan is built from. The
    /// deployment tool never asserts readiness: it calls the kernel rule and passes what it returns.
    /// </summary>
    private static ContractReadinessAssessment? AssessContractReadiness(
        ISchemaToolProviderSession provider,
        PhysicalSchemaTarget target,
        PhysicalSchemaInspectionResult inspection,
        SchemaEvolutionPhase phase) =>
        phase == SchemaEvolutionPhase.Contract
            ? ExpandContractWorkflow.AssessContractReadiness(
                target,
                inspection.History,
                provider.DataMigrations?.ReadLedgerEntries(target.Identity),
                DateTimeOffset.UtcNow)
            : null;

    private static IReadOnlyList<SchemaToolSupersessionReport> Describe(ContractReadinessAssessment? readiness) =>
        readiness is null
            ? []
            : [.. readiness.Supersessions.Select(status => new SchemaToolSupersessionReport(
                status.SupersededColumn,
                status.ReplacementColumn,
                status.State.ToString(),
                status.IsContractable,
                Instant(status.RetainedSince),
                Instant(status.BackfillCompletedAt),
                Instant(status.ContractableAt)))];

    private static string PhaseName(SchemaEvolutionPhase phase) =>
        phase == SchemaEvolutionPhase.Contract ? "contract" : "expand";

    private static string? Instant(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string PlanFingerprint(PhysicalSchemaTarget target, PhysicalSchemaDiffPlan plan)
    {
        // The phase is appended only for a contract plan, so every expand plan keeps the exact
        // fingerprint it had before phases existed and an --expected-plan value does not churn.
        var parts = new[] { target.Fingerprint, plan.ExpectedAppliedTargetFingerprint ?? string.Empty }
            .Concat(plan.Operations.SelectMany(operation => new[] { operation.Identity, operation.Fingerprint }))
            .Concat(plan.Phase == SchemaEvolutionPhase.Expand ? [] : new[] { "phase:" + plan.Phase });
        return PortableHex.Lower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
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
        // Report the spelling that actually authorizes each operation: the readable address where
        // the plan leaves it unambiguous, and the exact identity otherwise.
        var destructive = pending.Where(operation => operation.IsDestructive)
            .Select(operation => operation.Authorization).Distinct(StringComparer.Ordinal).Order().ToArray();
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
            dataMigrations = value.Targets.SelectMany(item => item.DataMigrations)
                .OrderBy(item => item.Identity, StringComparer.Ordinal),
            phase = value.Phase,
            supersessions = value.Targets.SelectMany(item => item.Supersessions)
                .OrderBy(item => item.SupersededColumn, StringComparer.Ordinal),
            pendingOperations = pending,
            appliedOperations = applied.OrderBy(operation => operation.Identity, StringComparer.Ordinal),
            authorization = new
            {
                destructiveRequired = destructive.Length != 0,
                destructiveOperationsRequired = destructive,
                semanticRequired = semantic
            },
            diagnosticRecords = (object?)null,
            diagnostics = value.Diagnostics.Concat(value.Targets.SelectMany(item => item.Diagnostics)).Select(Error)
                .Concat(value.Targets.SelectMany(item => item.Warnings).Select(Warning)),
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

    private static object Warning(SchemaVerificationError warning) => new
    {
        severity = "warning",
        warning.Code,
        warning.Message,
        target = warning.Path
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
            }.Concat(value.Targets.SelectMany(target => target.Supersessions)
                .OrderBy(supersession => supersession.SupersededColumn, StringComparer.Ordinal)
                .Select(supersession =>
                    $"Superseded column {supersession.SupersededColumn} -> {supersession.ReplacementColumn}: " +
                    $"{supersession.State.ToLowerInvariant()}" +
                    (supersession.IsContractable
                        ? ", contractable"
                        : $", not contractable{(supersession.ContractableAt is null ? string.Empty : " until " + supersession.ContractableAt)}")))
            .Concat(value.Targets.SelectMany(target => target.DataMigrations)
                .OrderBy(migration => migration.Identity, StringComparer.Ordinal)
                .Select(migration => $"Data migration {migration.Identity}: {migration.State}" +
                    (migration.State == SchemaToolDataMigrationReport.PendingState
                        ? $" ({migration.RowsScanned} rows scanned, resume at {migration.ResumeCursor})"
                        : string.Empty)))
            .Concat(value.Diagnostics.Concat(value.Targets.SelectMany(target => target.Diagnostics))
                .Select(diagnostic => $"error {diagnostic.Code}: {diagnostic.Message} ({diagnostic.Path})"))
            .Concat(value.Targets.SelectMany(target => target.Warnings)
                .Select(warning => $"warning {warning.Code}: {warning.Message} ({warning.Path})"))),
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
            "apply" or "adopt" => new HashSet<string>(["--safe"], StringComparer.Ordinal),
            _ => new HashSet<string>(StringComparer.Ordinal)
        };
        var values = command switch
        {
            "schema emit" => new HashSet<string>(["--input", "--file", "--output"], StringComparer.Ordinal),
            "apply" or "adopt" => new HashSet<string>([
                "--schema", "--provider", "--connection", "--database", "--provider-assembly",
                "--coverage", "--output", "--phase", "--expected-plan", "--allow-destructive", "--allow-semantic",
                "--authorize-destructive", "--authorize-semantic"
            ], StringComparer.Ordinal),
            _ => new HashSet<string>([
                "--schema", "--provider", "--connection", "--database", "--provider-assembly",
                "--coverage", "--output", "--phase"
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
            if (option == "--phase" && arguments[index + 1] is not ("expand" or "contract"))
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
        Usage: groundwork <plan|validate|status|apply|adopt> --schema <file> --provider <name> [--connection <value>]
               [--database <name>] [--provider-assembly <file>] [--coverage <file>] [--output json|human]
               [--phase expand|contract]
               groundwork schema emit --input <file> --file <file> [--output json|human]

        --phase selects which half of an expand-contract evolution to plan. It defaults to expand, the
        additive half. The contract half removes superseded columns and refuses until its readiness is
        established from the applied schema ledger and the data-migration ledger.

        Apply requires --safe, or exact --expected-plan authorization. Destructive operation identities additionally
        require --allow-destructive <identity>; semantic migrations require --allow-semantic <identity>.
        Runtime admission remains inspect-only unless AutoApplyOnStartup is explicitly enabled by the host.

        Adopt records an existing catalog Groundwork has never applied, under the same authorization as apply. It
        executes no DDL: it proves the deployed catalog is exactly the compiled target and publishes the applied
        state that applying it would have produced. Any difference is refused by name. It never infers a schema,
        and it refuses a target that already has applied history.
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
    IReadOnlyList<SchemaVerificationError> Diagnostics)
{
    /// <summary>Which half of an expand–contract evolution this invocation planned.</summary>
    public string Phase { get; init; } = nameof(SchemaEvolutionPhase.Expand).ToLowerInvariant();
}

/// <summary>One superseded column's place in the expand–contract workflow, as durable state has it.</summary>
public sealed record SchemaToolSupersessionReport(
    string SupersededColumn,
    string ReplacementColumn,
    string State,
    bool IsContractable,
    string? RetainedSince,
    string? BackfillCompletedAt,
    string? ContractableAt);

/// <summary>A logical reference declaration attached to a schema target.</summary>
public sealed record SchemaToolReferenceReport(
    string Name,
    string Target,
    IReadOnlyList<string> Columns);

public sealed record SchemaToolTargetReport(
    string Subject,
    string Fingerprint,
    string Outcome,
    string PlanFingerprint,
    string? AppliedTargetFingerprint,
    IReadOnlyList<SchemaToolOperationReport> PendingOperations,
    IReadOnlyList<SchemaToolOperationReport> AppliedOperations,
    IReadOnlyList<SchemaVerificationError> Diagnostics,
    bool Mutated)
{
    /// <summary>Logical-only references declared by this target, ordered by name.</summary>
    public IReadOnlyList<SchemaToolReferenceReport> References { get; init; } = [];

    /// <summary>Recorded data-migration state for this target, pending first.</summary>
    public IReadOnlyList<SchemaToolDataMigrationReport> DataMigrations { get; init; } = [];

    /// <summary>Contract readiness per superseded column; empty unless the contract phase was planned.</summary>
    public IReadOnlyList<SchemaToolSupersessionReport> Supersessions { get; init; } = [];

    /// <summary>Drift a declaration's opt-in foreign-column policy downgraded to a warning.</summary>
    public IReadOnlyList<SchemaVerificationError> Warnings { get; init; } = [];
}

/// <summary>One semantic migration's recorded data-migration state on one target.</summary>
public sealed record SchemaToolDataMigrationReport(
    string Identity,
    string State,
    string? Unit,
    long RowsScanned,
    long RowsChanged,
    int Batches,
    string? ResumeCursor,
    string? CompletedAt)
{
    /// <summary>The subject declares this semantic migration but nothing has been recorded for it.</summary>
    public const string NotRecordedState = "not-recorded";

    public const string PendingState = "pending";

    public const string AppliedState = "applied";

    internal static SchemaToolDataMigrationReport From(DataMigrationLedgerEntry entry) => new(
        entry.MigrationId,
        entry.IsComplete ? AppliedState : PendingState,
        entry.UnitName,
        entry.RowsScanned,
        entry.RowsChanged,
        entry.Batches,
        entry.Cursor,
        entry.CompletedAt?.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
}

public sealed record SchemaToolOperationReport(
    string Identity,
    string Fingerprint,
    string Kind,
    string? StorageUnit,
    string SubjectIdentity,
    bool IsDestructive,
    string? SemanticMigrationIdentity,
    string? AuthorizationAddress = null)
{
    /// <summary>The exact spelling that authorizes this operation, readable where the plan allows.</summary>
    internal string Authorization => AuthorizationAddress ?? Identity;

    internal static SchemaToolOperationReport FromPending(
        PhysicalSchemaOperation operation,
        PhysicalSchemaPlanProtection protection) => new(
        operation.Identity,
        operation.Fingerprint,
        operation.Kind.ToString(),
        operation.SubjectId?.Value,
        operation.SubjectIdentity,
        operation.RequiresAuthorization,
        operation.SemanticMigrationId,
        protection.DestructiveOperations
            .FirstOrDefault(protected_ => protected_.Identity == operation.Identity)?.Address);

    internal static SchemaToolOperationReport FromApplied(PhysicalSchemaAppliedOperation operation) => new(
        operation.Identity,
        operation.Fingerprint,
        operation.Kind.ToString(),
        operation.SubjectId?.Value,
        operation.SubjectIdentity,
        false,
        null);
}
