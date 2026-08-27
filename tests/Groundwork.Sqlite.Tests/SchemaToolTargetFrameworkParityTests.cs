using Groundwork.SchemaTool;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// The groundwork CLI is multi-targeted, and it writes the applied-state history that startup
/// admission later compares against. A schema document emitted, planned, or applied by the CLI
/// running on one target framework must therefore be identical to one produced on another:
/// otherwise applying schema from a net8.0 deployment host and starting the application on
/// net10.0 would report drift that does not exist.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SqliteSchemaTargetParityTests"/> already proves the CLI and the runtime agree, but it
/// compares two values computed in the same process, so it would agree with itself on each target
/// even if the two targets disagreed with each other. These expectations are literals, and this
/// suite is built and run once per shipped target framework, so a target that produced a different
/// canonical document or a different fingerprint fails here.
/// </para>
/// <para>
/// This reaches the composition that the kernel-level pins in
/// <c>Groundwork.Kernel.Tests.TargetFrameworkParityTests</c> cannot cover on their own: canonical
/// JSON emission, column and index ordering (the declaration deliberately names its indexes in the
/// opposite order to the canonical one), operation identities, and the plan fingerprint that
/// <c>apply</c> authorizes against.
/// </para>
/// </remarks>
public sealed class SchemaToolTargetFrameworkParityTests : IDisposable
{
    [Fact]
    public async Task Emitted_canonical_schema_is_byte_identical_on_every_shipped_target_framework()
    {
        var declared = harness.Temp("parity.json", SchemaToolCliHarness.ParitySchema());
        var emitted = Path.Combine(harness.Root, "emitted.json");

        var emit = await harness.EmitAsync(declared, emitted);

        Assert.True(SchemaToolExitCodes.Success == emit.ExitCode, emit.Reason);
        Assert.Equal(ExpectedCanonicalSchema, File.ReadAllText(emitted));
    }

    [Fact]
    public async Task Planned_and_applied_fingerprints_are_identical_on_every_shipped_target_framework()
    {
        var declared = harness.Temp("parity.json", SchemaToolCliHarness.ParitySchema());
        var emitted = Path.Combine(harness.Root, "emitted.json");
        var database = Path.Combine(harness.Root, "parity.db");
        var connection = $"Data Source={database}";
        Assert.True(SchemaToolExitCodes.Success == (await harness.EmitAsync(declared, emitted)).ExitCode);
        File.Create(database).Dispose();

        var plan = await harness.RunAsync(["plan", "--schema", emitted], connection);
        var target = plan.Report.RootElement.GetProperty("targets")[0];

        Assert.Equal(ExpectedSubjectFingerprint, target.GetProperty("fingerprint").GetString());
        Assert.Equal(ExpectedPlanFingerprint, target.GetProperty("planFingerprint").GetString());

        var apply = await harness.ApplyAuthorizedAsync(emitted, connection);

        Assert.True(SchemaToolExitCodes.Success == apply.ExitCode, apply.Reason);
        Assert.Equal("applied", apply.Report.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            ExpectedSubjectFingerprint,
            apply.Report.RootElement.GetProperty("appliedTargetFingerprint").GetString());
    }

    private const string ExpectedCanonicalSchema =
        """{"tables":[{"name":"parity_orders","columns":[{"name":"id","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"None","generation":"Supplied","default":null},{"name":"customer","type":"String","nullable":false,"length":64,"precision":null,"scale":null,"folding":"AsciiIgnoreCase","generation":"Supplied","default":null},{"name":"status","type":"String","nullable":false,"length":16,"precision":null,"scale":null,"folding":"None","generation":"Supplied","default":{"value":"pending"}}],"key":["id"],"indexes":[{"name":"a_parity_status","columns":[{"name":"status","descending":false}],"includeNulls":true,"unique":false},{"name":"z_parity_customer","columns":[{"name":"customer","descending":false}],"includeNulls":true,"unique":false}],"scope":"Scoped","concurrency":{"token":"version"},"timestamps":"None","retention":null,"appendIdempotency":null,"retentionIdempotency":null,"aggregations":[]}]}""";

    private const string ExpectedSubjectFingerprint =
        "9923bfbc8f4f475209cc8d1b39a51ccf7a7d15c51d20ec1e006f4aa79ddee31e";

    private const string ExpectedPlanFingerprint =
        "a0703693d0fe77dbeb907f0a7b74f8209b573181fb41da8118a1e71fb6c7b388";

    private readonly SchemaToolCliHarness harness = new(
        static (arguments, output, error) => GroundworkSchemaCli.RunAsync(arguments, output, error),
        "sqlite",
        Path.Combine(AppContext.BaseDirectory, "Groundwork.Sqlite.dll"));

    public void Dispose() => harness.Dispose();
}
