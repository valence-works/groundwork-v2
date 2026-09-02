using System.Text;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.MySql;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Store;
using Groundwork.Testing;

if (args.Length != 4)
{
    Console.Error.WriteLine("Usage: ProviderMatrix <public-packages.txt> <matrix.json> <provider.md> <packages.md>");
    return 2;
}

var packageListPath = Path.GetFullPath(args[0]);
var packageRows = ReadPackages(packageListPath);

if (packageRows.Length == 0 || packageRows.Select(row => row.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packageRows.Length)
    throw new InvalidOperationException("public-packages.txt must contain a non-empty unique package inventory.");

var providers = new List<ProviderRow>
{
    FromFactory("sqlite", "SQLite", "SQLite provider conformance", new SqliteProviderFactory(), "Data Source=:memory:"),
    FromFactory("postgresql", "PostgreSQL", "PostgreSQL differential lane", new PostgreSqlProviderFactory(), "Host=matrix.invalid;Database=groundwork"),
    FromFactory("sqlserver", "SQL Server", "SQL Server differential lane", new SqlServerProviderFactory(), "Server=matrix.invalid;Database=groundwork"),
    FromFactory("mysql", "MySQL", "MySQL differential lane", new MySqlProviderFactory(), "Server=matrix.invalid;Database=groundwork"),
    FromFactory("inmemory", "InMemory", "Reference provider conformance", new InMemoryProviderFactory(), "memory://provider-matrix"),
    MongoProfile("mongodb-replica-set", "MongoDB replica set", "MongoDB differential lane", transactional: true),
    MongoProfile("mongodb-standalone", "MongoDB standalone", "Capability-refusal lane", transactional: false)
};

var document = new MatrixDocument(packageRows, providers);
var options = new JsonSerializerOptions { WriteIndented = true };
File.WriteAllText(args[1], JsonSerializer.Serialize(document, options) + Environment.NewLine);
File.WriteAllText(args[2], RenderProviderMatrix(document));
File.WriteAllText(args[3], RenderPackageMatrix(document));

static ProviderRow FromFactory(
    string id,
    string displayName,
    string lane,
    IStorageProviderFactory factory,
    string connectionString)
{
    using var connection = factory.Create(connectionString);
    return Snapshot(id, displayName, lane, connection.Capabilities);
}

static PackageRow[] ReadPackages(string path)
{
    return File.ReadLines(path)
        .Select((line, index) => (line, lineNumber: index + 1))
        .Where(entry => !string.IsNullOrWhiteSpace(entry.line) && !entry.line.TrimStart().StartsWith('#'))
        .Select(entry =>
        {
            var parts = entry.line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty))
                throw new InvalidOperationException($"Invalid public package entry at line {entry.lineNumber}: '{entry.line}'.");
            return new PackageRow(parts[0], parts[1]);
        })
        .ToArray();
}

static ProviderRow MongoProfile(string id, string displayName, string lane, bool transactional)
{
    var capabilities = BatchWriteCapabilities.ForProvider(
        "MongoDB",
        nativeBatch: true,
        exactOutcomeCost: "one FindOneAndUpdate per coalesced row",
        batchCost: "uses unordered BulkWrite for aggregate commits",
        exactAppendOutcomes: true,
        durableHighWaterInspection: true,
        exactRetention: true,
        exactRetentionAffectedKeys: true,
        atomicCommit: transactional,
        compareAndDelete: transactional,
        setMutation: "Updates or deletes every document matching an index-covered portable predicate on MongoDB with one updateMany/deleteMany, and reports matchedCount/deletedCount. Unlike the relational providers, a multi-document updateMany/deleteMany is atomic only when it runs inside a transaction: open a unit of work on a transaction-capable deployment when the whole set must apply or none of it.");
    var transactionDependent = new[]
    {
        BatchWriteCapabilities.AppendIdempotency,
        BatchWriteCapabilities.ExactAppendOutcomes,
        BatchWriteCapabilities.DurableHighWaterInspection,
        BatchWriteCapabilities.ExactRetention,
        BatchWriteCapabilities.ExactRetentionAffectedKeys,
        BatchWriteCapabilities.ProviderSequence
    };
    if (!transactional)
        capabilities = capabilities.Where(descriptor => !transactionDependent.Contains(descriptor.Id)).ToArray();
    else
        capabilities = capabilities.Select(descriptor => descriptor.Id == BatchWriteCapabilities.ProviderSequence
            ? MongoCapabilities.ProviderSequenceDescriptor
            : descriptor).ToArray();
    return Snapshot(id, displayName, lane, capabilities);
}

static ProviderRow Snapshot(
    string id,
    string displayName,
    string lane,
    IReadOnlyList<CapabilityDescriptor> descriptors)
{
    var capabilities = descriptors
        .OrderBy(descriptor => descriptor.Id.Value, StringComparer.Ordinal)
        .Select(descriptor => new CapabilityRow(
            descriptor.Id.Value,
            descriptor.DisplayName,
            descriptor.Description,
            descriptor.EvidenceGatedByDefault,
            descriptor.AdditionalProviderCommandsPerWrite))
        .ToArray();
    return new ProviderRow(id, displayName, lane, capabilities);
}

static string RenderPackageMatrix(MatrixDocument document)
{
    var builder = new StringBuilder();
    builder.AppendLine("# Groundwork v2 package matrix");
    builder.AppendLine();
    builder.AppendLine("Generated from `eng/public-packages.txt`; edit that allowlist rather than this file.");
    builder.AppendLine();
    builder.AppendLine("| Package | Project |");
    builder.AppendLine("| --- | --- |");
    foreach (var package in document.Packages)
        builder.AppendLine($"| `{package.Id}` | `{package.Project}` |");
    return builder.ToString();
}

static string RenderProviderMatrix(MatrixDocument document)
{
    var allCapabilities = document.Providers
        .SelectMany(provider => provider.Capabilities)
        .GroupBy(capability => capability.Id, StringComparer.Ordinal)
        .Select(group => DescribeCapability(group.Key))
        .OrderBy(capability => capability.Id.Value, StringComparer.Ordinal)
        .ToArray();
    var columns = new[]
    {
        "groundwork.column.provider-sequence",
        "groundwork.storage.batched-unit-of-work",
        "groundwork.storage.batched-outcomes",
        "groundwork.storage.batched-native",
        "groundwork.storage.append-idempotency",
        "groundwork.storage.exact-append-outcomes",
        "groundwork.storage.durable-high-water-inspection",
        "groundwork.storage.exact-retention",
        "groundwork.storage.exact-retention-affected-keys",
        "groundwork.schema.enforced-constraints",
        "groundwork.operational.atomic-commit",
        "groundwork.storage.compare-and-delete",
        "groundwork.storage.set-mutation"
    };
    var builder = new StringBuilder();
    builder.AppendLine("# Groundwork v2 provider capability matrix");
    builder.AppendLine();
    builder.AppendLine("Generated by `eng/generate-provider-matrices.sh` from public provider capability objects.");
    builder.AppendLine("MongoDB rows model the public capability profiles for transaction-capable and standalone deployments.");
    builder.AppendLine();
    builder.AppendLine("| Provider profile | Lane | " + string.Join(" | ", columns.Select(ShortName)) + " |");
    builder.AppendLine("| --- | --- | " + string.Join(" | ", columns.Select(_ => "---")) + " |");
    foreach (var provider in document.Providers)
    {
        var ids = provider.Capabilities.Select(capability => capability.Id).ToHashSet(StringComparer.Ordinal);
        builder.AppendLine($"| {provider.DisplayName} | {provider.Lane} | " +
                           string.Join(" | ", columns.Select(column => ids.Contains(column) ? "yes" : "no")) + " |");
    }

    builder.AppendLine();
    builder.AppendLine("## Capability semantics");
    builder.AppendLine();
    builder.AppendLine("The two batch columns are intentionally separate: staged unit-of-work and per-row outcomes do not imply a native multi-row command.");
    builder.AppendLine();
    builder.AppendLine("| Capability ID | Meaning | Evidence-gated | Additional provider commands/write |");
    builder.AppendLine("| --- | --- | --- | --- |");
    foreach (var capability in allCapabilities)
        builder.AppendLine($"| `{capability.Id}` | {EscapeTableCell(capability.Description)} | {(capability.EvidenceGatedByDefault ? "yes" : "no")} | {capability.AdditionalProviderCommandsPerWrite} |");
    return builder.ToString();
}

static CapabilityDescriptor DescribeCapability(string id)
{
    if (id == WellKnownCapabilities.EnforcedConstraints.Value)
        return CapabilityRegistry.Default.Get(WellKnownCapabilities.EnforcedConstraints);
    if (id == WellKnownCapabilities.AtomicCommit.Value)
        return BatchWriteCapabilities.AtomicCommitDescriptor;
    if (id == BatchWriteCapabilities.NativeBatch.Value)
        return BatchWriteCapabilities.NativeBatchDescriptor;
    return BatchWriteCapabilities.All.Single(descriptor => descriptor.Id.Value == id);
}

static string ShortName(string capability) => capability switch
{
    "groundwork.column.provider-sequence" => "Sequence",
    "groundwork.storage.batched-unit-of-work" => "Staged UoW",
    "groundwork.storage.batched-outcomes" => "Per-row outcomes",
    "groundwork.storage.batched-native" => "Native batch",
    "groundwork.storage.append-idempotency" => "Append idempotency",
    "groundwork.storage.exact-append-outcomes" => "Exact append",
    "groundwork.storage.durable-high-water-inspection" => "High-water",
    "groundwork.storage.exact-retention" => "Exact retention",
    "groundwork.storage.exact-retention-affected-keys" => "Affected keys",
    "groundwork.schema.enforced-constraints" => "Enforced constraints",
    "groundwork.operational.atomic-commit" => "Atomic commit",
    "groundwork.storage.compare-and-delete" => "Compare/delete",
    "groundwork.storage.set-mutation" => "Set mutation",
    _ => capability
};

static string EscapeTableCell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);

return 0;

public sealed record MatrixDocument(IReadOnlyList<PackageRow> Packages, IReadOnlyList<ProviderRow> Providers);
public sealed record PackageRow(string Id, string Project);
public sealed record ProviderRow(string Id, string DisplayName, string Lane, IReadOnlyList<CapabilityRow> Capabilities);
public sealed record CapabilityRow(string Id, string DisplayName, string Description, bool EvidenceGated, int AdditionalCommandsPerWrite);
