using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Groundwork.SqlServer;
using Groundwork.Substrate.Relational;
using Groundwork.Sqlite;
using Groundwork.Query.Linq.Execution;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// The four-provider acceptance proof for set-based mutation (P4.3, #89).
///
/// Every assertion here is pinned to a literal. Nothing is recomputed from the code under test, so
/// a provider that agrees with the others by making the same mistake still fails.
/// </summary>
[Collection(NativeProviderDifferentialCollection.Name)]
public sealed class SetMutationDifferentialTests
{
    private const string Scan = "GW-SCAN-0089";

    /// <summary>
    /// The rendered artifact, for each relational dialect. The assignment parameters are numbered
    /// in their own namespace: the predicate fragment numbers its own values from <c>p0</c>, so
    /// numbering assignments the same way would collide on the very first one.
    /// </summary>
    [Fact]
    public void Set_mutation_renders_assignments_and_the_predicate_in_disjoint_parameter_namespaces()
    {
        AssertRendered(new SqliteQueryRenderer(), "\"label\"", "\"__groundwork_version\"");
        AssertRendered(new PostgreSqlQueryRenderer(), "\"label\"", "\"__groundwork_version\"");
        AssertRendered(new SqlServerQueryRenderer(), "[label]", "[__groundwork_version]");

        static void AssertRendered(RelationalQueryRenderer renderer, string label, string version)
        {
            var unit = CreateUnit("p43_render");
            var update = renderer.RenderUpdateWhere(
                unit.Name, Status(unit, "old"), ["label"], "__groundwork_version");
            Assert.Equal(new[] { "s0" }, update.AssignmentParameters.ToArray());
            Assert.Equal(new[] { "p0" }, update.Parameters.Select(parameter => parameter.Name).ToArray());
            Assert.Contains(label + " = @s0", update.CommandText, StringComparison.Ordinal);
            Assert.Contains(version + " = " + version + " + 1", update.CommandText, StringComparison.Ordinal);
            Assert.Contains("@p0", update.CommandText, StringComparison.Ordinal);
            Assert.StartsWith("UPDATE ", update.CommandText, StringComparison.Ordinal);

            var delete = renderer.RenderDeleteWhere(unit.Name, Status(unit, "old"));
            Assert.StartsWith("DELETE FROM ", delete.CommandText, StringComparison.Ordinal);
            Assert.Empty(delete.AssignmentParameters);
            Assert.Equal(new[] { "p0" }, delete.Parameters.Select(parameter => parameter.Name).ToArray());

            // No token column, no increment: a unit that declares no optimistic concurrency has
            // nothing to bump, and inventing one would write a column that does not exist.
            var untracked = renderer.RenderUpdateWhere(unit.Name, Status(unit, "old"), ["label"]);
            Assert.DoesNotContain(" + 1", untracked.CommandText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SQLite_set_based_mutation_matches_the_portable_contract() =>
        AssertProvider("SQLite", () => new SqliteProviderFactory().Create(
            "Data Source=file:groundwork_p43_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared"));

    [SkippableFact]
    public void PostgreSQL_set_based_mutation_matches_the_portable_contract() =>
        AssertProvider("PostgreSQL", () => new PostgreSqlProviderFactory().Create(
            Required("GROUNDWORK_POSTGRES_CONNECTION")));

    [SkippableFact]
    public void SQLServer_set_based_mutation_matches_the_portable_contract() =>
        AssertProvider("SQL Server", () => new SqlServerProviderFactory().Create(LiveSqlServer.Required()));

    [SkippableFact]
    public void MongoDB_set_based_mutation_matches_the_portable_contract() =>
        AssertProvider("MongoDB", () => new MongoProviderFactory().Create(LiveMongo.Required()));

    private static void AssertProvider(string provider, Func<IStorageProviderConnection> open)
    {
        using var connection = open();
        Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.SetMutation);

        AssertMatchedCounts(provider, connection);
        AssertUncoveredPredicateIsRefused(provider, connection);
        AssertOptimisticTokenIsBumped(provider, connection);
        AssertScopeIsNotCrossed(provider, connection);
        AssertUnitOfWorkFlushesBeforeMutating(provider, connection);
    }

    /// <summary>
    /// The count contract. The second update assigns values every matched row already holds:
    /// MongoDB's <c>modifiedCount</c> is 0 for it while its <c>matchedCount</c> is 3, and the
    /// relational providers report 3. Pinning 3 for both runs is what proves the reported number is
    /// matched rows on every provider rather than "rows whose bytes changed" on one of them.
    /// </summary>
    private static void AssertMatchedCounts(string provider, IStorageProviderConnection connection)
    {
        var unit = CreateUnit("p43_counts_" + Suffix());
        Assert.True(connection.Schema.Apply(unit).Applied, provider);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var row in Rows())
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(new StorageValues(row)).Status);

        Assert.Equal(3L, session.UpdateWhere(
            Status(unit, "old"),
            new Dictionary<string, object?> { ["label"] = "archived" }).MatchedRows);
        Assert.Equal(3L, session.UpdateWhere(
            Status(unit, "old"),
            new Dictionary<string, object?> { ["label"] = "archived" }).MatchedRows);
        Assert.Equal(
            new string?[] { "archived", "archived", "archived" },
            Labels(session, unit, "old"));

        Assert.Equal(2L, session.DeleteWhere(Status(unit, "gone")).MatchedRows);
        Assert.Equal(0L, session.DeleteWhere(Status(unit, "gone")).MatchedRows);
        Assert.Equal(new[] { "a1", "a2", "o1", "o2", "o3" }, Ids(session, unit));

        // Assignments are values, never expressions, so replaying an acknowledgement-loss retry
        // lands on the same rows with the same result.
        Assert.Equal(2L, session.UpdateWhere(
            Status(unit, "active"),
            new Dictionary<string, object?> { ["amount"] = 41L, ["label"] = null }).MatchedRows);
        Assert.Equal(new long?[] { 41L, 41L }, Amounts(session, unit, "active"));
    }

    /// <summary>
    /// An unfiltered set-based mutation is refused by the rule that already refuses an unfiltered
    /// read — same checker, same code, same acceptance escape hatch.
    /// </summary>
    private static void AssertUncoveredPredicateIsRefused(string provider, IStorageProviderConnection connection)
    {
        var unit = CreateUnit("p43_cover_" + Suffix());
        Assert.True(connection.Schema.Apply(unit).Applied, provider);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var row in Rows())
            session.Insert(new StorageValues(row));

        var refusedDelete = Assert.Throws<QueryCoverageException>(() =>
            session.DeleteWhere(Predicate.AlwaysTrue.Instance));
        Assert.Equal("GW-COVER-005", refusedDelete.Code);

        var label = new ColumnRef(new TableId(unit.Name), "label", QueryType.String, isNullable: true, maxLength: 64);
        var refusedUpdate = Assert.Throws<QueryCoverageException>(() => session.UpdateWhere(
            new Predicate.Equal(label, QueryConstant.Of(label, "keep")),
            new Dictionary<string, object?> { ["amount"] = 1L }));
        Assert.Equal("GW-COVER-006", refusedUpdate.Code);
        Assert.Equal(7, Ids(session, unit).Count);

        var accepted = new SetMutationOptions
        {
            AcceptedScan = ScanAcceptance.Allow(Scan, "P4.3 acceptance proof", "groundwork", new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero))
        };
        Assert.Equal(7L, session.DeleteWhere(Predicate.AlwaysTrue.Instance, accepted).MatchedRows);
        Assert.Empty(Ids(session, unit));

        var expired = new SetMutationOptions
        {
            AcceptedScan = ScanAcceptance.Allow(Scan, "P4.3 acceptance proof", "groundwork", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero))
        };
        var stale = Assert.Throws<QueryCoverageException>(() =>
            session.DeleteWhere(Predicate.AlwaysTrue.Instance, expired));
        Assert.Equal("GW-COVER-903", stale.Code);
    }

    /// <summary>
    /// A set-based update on a unit with an optimistic token moves every matched row's version in
    /// the same statement, exactly as a keyed update does. The keyed IfVersion(1) write afterwards
    /// is the proof that the move is visible to the concurrency contract rather than only to a read.
    /// </summary>
    private static void AssertOptimisticTokenIsBumped(string provider, IStorageProviderConnection connection)
    {
        var unit = CreateUnit("p43_version_" + Suffix()) with
        {
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        Assert.True(connection.Schema.Apply(unit).Applied, provider);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var row in Rows())
            Assert.Equal(1L, session.Insert(new StorageValues(row)).Version);

        Assert.Equal(3L, session.UpdateWhere(
            Status(unit, "old"),
            new Dictionary<string, object?> { ["label"] = "archived" }).MatchedRows);

        Assert.Equal(2L, session.Read(Key("o1"))!.Version);
        Assert.Equal(1L, session.Read(Key("a1"))!.Version);

        // Set mutation does not use the append idempotency ledger. Repeating an update stores the
        // same application value but advances the optimistic token again, so callers must resolve
        // an unknown acknowledgement before retrying when token stability matters.
        Assert.Equal(3L, session.UpdateWhere(
            Status(unit, "old"),
            new Dictionary<string, object?> { ["label"] = "archived" }).MatchedRows);
        Assert.Equal(3L, session.Read(Key("o1"))!.Version);

        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, session.Update(
            Row("o1", "old", "stale", 1L),
            WriteOptions.IfVersion(1)).Status);
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, session.Update(
            Row("o1", "old", "stale", 1L),
            WriteOptions.IfVersion(2)).Status);
        Assert.Equal(WriteOutcomeStatus.Updated, session.Update(
            Row("o1", "old", "fresh", 1L),
            WriteOptions.IfVersion(3)).Status);

        var systemOwned = Assert.Throws<InvalidOperationException>(() => session.UpdateWhere(
            Status(unit, "old"),
            new Dictionary<string, object?> { ["version"] = 9L }));
        Assert.Contains("GW-WRITE-CONCURRENCY-003", systemOwned.Message, StringComparison.Ordinal);
    }

    private static void AssertScopeIsNotCrossed(string provider, IStorageProviderConnection connection)
    {
        var unit = CreateUnit("p43_scope_" + Suffix()) with { Scope = ScopePolicy.Scoped };
        Assert.True(connection.Schema.Apply(unit).Applied, provider);
        var first = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var second = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));
        foreach (var row in Rows())
        {
            first.Insert(new StorageValues(row));
            second.Insert(new StorageValues(row));
        }

        Assert.Equal(2L, first.DeleteWhere(Status(unit, "gone")).MatchedRows);
        Assert.Equal(3L, first.UpdateWhere(
            Status(unit, "old"),
            new Dictionary<string, object?> { ["label"] = "scoped" }).MatchedRows);

        Assert.Equal(new[] { "a1", "a2", "o1", "o2", "o3" }, Ids(first, unit));
        Assert.Equal(new[] { "a1", "a2", "g1", "g2", "o1", "o2", "o3" }, Ids(second, unit));
        Assert.Equal(new string?[] { "scoped", "scoped", "scoped" }, Labels(first, unit, "old"));
        Assert.Equal(new string?[] { "keep", "keep", "keep" }, Labels(second, unit, "old"));

        // A privileged cross-scope session sees every scope at once and so has no scope to write
        // to. The provider session refuses it, which is what a caller who reaches the capability
        // interface directly — bypassing the admitted entry point — actually meets.
        var privileged = (ISetMutationStorageSession)connection.OpenSession(
            unit,
            StorageAccess.PrivilegedAcrossScopes(new StorageAccessAudit("operator", "P4.3 acceptance proof")));
        var refusal = Assert.Throws<InvalidOperationException>(() => privileged.DeleteWhere(Status(unit, "old")));
        Assert.Contains("GW-ACCESS-003", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "a1", "a2", "o1", "o2", "o3" }, Ids(first, unit));
    }

    /// <summary>
    /// Set-based mutation is not a second write path. Inside a unit of work it takes the same
    /// whole-unit flush barrier a staged read takes, so it sees rows staged but not yet flushed.
    /// </summary>
    private static void AssertUnitOfWorkFlushesBeforeMutating(string provider, IStorageProviderConnection connection)
    {
        if (!connection.Capabilities.Any(capability => capability.Id == WellKnownCapabilities.AtomicCommit))
            return;
        var unit = CreateUnit("p43_uow_" + Suffix());
        Assert.True(connection.Schema.Apply(unit).Applied, provider);
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, unit))
        {
            var session = work.OpenSession(unit);
            foreach (var row in Rows())
                work.Stage(RowWrite.Insert(unit, new StorageValues(row)));
            Assert.Equal(2L, session.DeleteWhere(Status(unit, "gone")).MatchedRows);
            Assert.Equal(3L, session.UpdateWhere(
                Status(unit, "old"),
                new Dictionary<string, object?> { ["label"] = "staged" }).MatchedRows);
            work.Commit();
        }

        var reader = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(new[] { "a1", "a2", "o1", "o2", "o3" }, Ids(reader, unit));
        Assert.Equal(new string?[] { "staged", "staged", "staged" }, Labels(reader, unit, "old"));
    }

    private static Predicate Status(StorageUnit unit, string value)
    {
        var column = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, isNullable: false, maxLength: 32);
        return new Predicate.Equal(column, QueryConstant.Of(column, value));
    }

    private static IReadOnlyList<string> Ids(IStorageSession session, StorageUnit unit) =>
        Read(session, unit, Predicate.AlwaysTrue.Instance)
            .Select(row => Assert.IsType<string>(row["id"]))
            .ToArray();

    private static IReadOnlyList<string?> Labels(IStorageSession session, StorageUnit unit, string status) =>
        Read(session, unit, Status(unit, status))
            .Select(row => row["label"] as string)
            .ToArray();

    private static IReadOnlyList<long?> Amounts(IStorageSession session, StorageUnit unit, string status) =>
        Read(session, unit, Status(unit, status))
            .Select(row => row["amount"] as long?)
            .ToArray();

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Read(
        IStorageSession session,
        StorageUnit unit,
        Predicate where)
    {
        var table = new TableId(unit.Name);
        var id = new ColumnRef(table, "id", QueryType.String, isNullable: false, maxLength: 64);
        return session.Query(new QueryRequest(
            table,
            where,
            [new OrderTerm(id, OrderDirection.Ascending, NullOrder.First)],
            Projection.All,
            Paging.None)).Rows;
    }

    private static StorageKey Key(string id) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id });

    private static string Suffix() => Guid.NewGuid().ToString("N")[..12];

    private static StorageUnit CreateUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
            new() { Name = "status", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
            new() { Name = "label", Type = PortableType.String, IsNullable = true, MaxLength = 64 },
            new() { Name = "amount", Type = PortableType.Int64, IsNullable = true }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition { Name = "by_status", Columns = [new IndexColumn("status")] }
        ]
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows() =>
    [
        Row("o1", "old", "keep", 1L).Values,
        Row("o2", "old", "keep", 2L).Values,
        Row("o3", "old", "keep", 3L).Values,
        Row("a1", "active", "keep", 4L).Values,
        Row("a2", "active", "keep", 5L).Values,
        Row("g1", "gone", "keep", 6L).Values,
        Row("g2", "gone", "keep", 7L).Values
    ];

    private static StorageValues Row(string id, string status, string? label, long? amount) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["status"] = status,
            ["label"] = label,
            ["amount"] = amount
        });

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Skip.If(true, $"Set {name} to run the live set-based mutation proof.");
        return value!;
    }
}
