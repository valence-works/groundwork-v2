using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Testing;

using Groundwork.Store;

/// <summary>Runs the provider-neutral behavioral contract against one provider factory.</summary>
public static class ConformanceSuite
{
    public static ConformanceReport Run(
        IStorageProviderFactory factory,
        string connectionString)
        => Run(factory, connectionString, ConformanceScenario.Default);

    /// <summary>
    /// Runs the shipped contract against an externally supplied storage family. The default
    /// overload remains source-compatible for provider authors while family packages can supply
    /// their own declarations and generated-key mapping.
    /// </summary>
    public static ConformanceReport Run(
        IStorageProviderFactory factory,
        string connectionString,
        ConformanceScenario scenario) =>
        Run(factory, connectionString, scenario, ConformanceExecution.Synchronous)
            .GetAwaiter().GetResult();

    /// <summary>
    /// Proves the same contract against the asynchronous session surface. Every check the
    /// synchronous run performs is repeated here through the asynchronous members, and the
    /// asynchronous run additionally proves that an already-cancelled token is refused before any
    /// provider work is issued. Each surface scopes its own storage unit names, so both runs can
    /// prove the whole contract independently against one database.
    /// </summary>
    public static ValueTask<ConformanceReport> RunAsync(
        IStorageProviderFactory factory,
        string connectionString,
        CancellationToken cancellationToken = default) =>
        RunAsync(factory, connectionString, ConformanceScenario.Default, cancellationToken);

    public static ValueTask<ConformanceReport> RunAsync(
        IStorageProviderFactory factory,
        string connectionString,
        ConformanceScenario scenario,
        CancellationToken cancellationToken = default) =>
        Run(factory, connectionString, scenario, ConformanceExecution.Asynchronous(cancellationToken));

    private static async ValueTask<ConformanceReport> Run(
        IStorageProviderFactory factory,
        string connectionString,
        ConformanceScenario scenario,
        ConformanceExecution surface)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scenario);
        scenario = scenario.WithUnitNameSuffix(surface.IsAsync ? "_async" : "_sync");

        var checks = new List<ConformanceCheck>();
        try
        {
            using var connection = factory.Create(connectionString);
            if (connection is null)
                throw new InvalidOperationException("The provider factory returned no connection.");

            var global = scenario.Global;
            var scoped = scenario.Scoped;
            await RunCheck(checks, "schema apply and provider catalog", () =>
            {
                var first = connection.Schema.Apply(global);
                Require(!first.IsNoOp, "the first schema application must have work");
                var second = connection.Schema.Apply(global);
                Require(second.IsNoOp, "reapplying an unchanged schema must be a no-op");
                Require(connection.Schema.Diff(global).IsEmpty,
                    "the provider reported a non-empty diff for the applied declaration");
                var indexes = connection.Catalog.ReadIndexes(global.Id);
                AssertCatalog(global, indexes);
                return default;
            }).ConfigureAwait(false);

            await RunCheck(checks, "storage-scope isolation", async () =>
            {
                connection.Schema.Apply(scoped);
                RequireThrows<InvalidOperationException>(
                    () => connection.OpenSession(scoped, StorageAccess.Global));
                RequireThrows<InvalidOperationException>(
                    () => connection.OpenSession(global, StorageAccess.Scoped(new StorageScope("scope-a"))));

                var first = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("scope-\U00010000")));
                var second = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("scope-\uE000")));
                var firstValues = scenario.Values("same", "a", null);
                var firstOutcome = await surface.Insert(first, firstValues).ConfigureAwait(false);
                Require(firstOutcome.Status == WriteOutcomeStatus.Inserted,
                    "the first scoped insert failed");
                var secondValues = scenario.Values("same", "b", null);
                var secondOutcome = await surface.Insert(second, secondValues).ConfigureAwait(false);
                Require(secondOutcome.Status == WriteOutcomeStatus.Inserted,
                    "the second scoped insert failed");
                Require((await surface.Read(first, scenario.Key("same", firstOutcome)).ConfigureAwait(false))
                        ?.Values.Values[scenario.ValueColumn] as string == "a",
                    "the first scope could not read its own value");
                Require((await surface.Read(second, scenario.Key("same", secondOutcome)).ConfigureAwait(false))
                        ?.Values.Values[scenario.ValueColumn] as string == "b",
                    "the second scope could not read its own value");
            }).ConfigureAwait(false);

            await RunCheck(checks, "audited privileged cross-scope query", async () =>
            {
                var access = StorageAccess.PrivilegedAcrossScopes(
                    new StorageAccessAudit("conformance-suite", "verify-cross-scope-recovery"));
                var session = connection.OpenSession(scoped, access);
                var table = new TableId(scoped.Name);
                var request = new QueryRequest(
                    table,
                    Predicate.AlwaysTrue.Instance,
                    [],
                    Projection.All,
                    Paging.Keyset(1),
                    ResultShape.TotalCount.Instance);

                await RequireThrows<InvalidOperationException>(
                    async () => await surface.Query(session, request).ConfigureAwait(false)).ConfigureAwait(false);
                await RequireThrows<InvalidOperationException>(
                    async () => await surface.Read(session, scenario.MissingKey("same")).ConfigureAwait(false)).ConfigureAwait(false);
                await RequireThrows<InvalidOperationException>(
                    async () => await surface.Insert(session, scenario.Values("refused", "write", null)).ConfigureAwait(false)).ConfigureAwait(false);
                RequireThrows<InvalidOperationException>(() =>
                    connection.BeginUnitOfWork(access, scoped));

                var first = await surface.QueryAcrossScopes(session, request).ConfigureAwait(false);
                Require(first.TotalCount == 2, "the privileged query did not count both scopes");
                Require(first.Rows.Count == 1, "the first privileged page did not contain one row");
                Require(first.NextContinuationToken is not null,
                    "the first privileged page did not return a continuation token");
                var second = await surface.QueryAcrossScopes(session, new QueryRequest(
                    table,
                    request.Where,
                    request.Order,
                    request.Projection,
                    Paging.Continuation(first.NextContinuationToken!, 1),
                    request.Result)).ConfigureAwait(false);
                Require(second.Rows.Count == 1, "the second privileged page did not contain one row");
                Require(first.Rows[0].Scope != second.Rows[0].Scope,
                    "the privileged pages did not preserve distinct row scopes");
                Require(first.Rows.Concat(second.Rows).All(row =>
                        row.Values.ContainsKey(scenario.ValueColumn)),
                    "the privileged query did not preserve public row values");
            }).ConfigureAwait(false);

            await RunCheck(checks, "cross-scope latest remains partitioned by scope", async () =>
            {
                var name = "conformance_cross_scope_latest_" + Guid.NewGuid().ToString("N");
                var latestUnit = new StorageUnit
                {
                    Id = new StorageUnitId(name),
                    Name = name,
                    Scope = ScopePolicy.Scoped,
                    Columns =
                    [
                        new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
                        new ColumnDefinition { Name = "group", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
                        new ColumnDefinition { Name = "observed_at", Type = PortableType.DateTimeOffset, IsNullable = false },
                        new ColumnDefinition { Name = "value", Type = PortableType.String, IsNullable = false, MaxLength = 64 }
                    ],
                    Key = new KeyDefinition { Columns = ["id"] }
                };
                connection.Schema.Apply(latestUnit);
                var first = connection.OpenSession(latestUnit,
                    StorageAccess.Scoped(new StorageScope("latest-a")));
                var second = connection.OpenSession(latestUnit,
                    StorageAccess.Scoped(new StorageScope("latest-b")));
                var earlier = DateTimeOffset.Parse("2026-01-01T00:00:00Z", null,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                var later = earlier.AddMinutes(1);
                StorageValues Values(string id, string value, DateTimeOffset observedAt) => new(
                    new Dictionary<string, object?>
                    {
                        ["id"] = id,
                        ["group"] = "shared",
                        ["observed_at"] = observedAt,
                        ["value"] = value
                    });
                await surface.Insert(first, Values("a-old", "a-old", earlier)).ConfigureAwait(false);
                await surface.Insert(first, Values("a-new", "a-new", later)).ConfigureAwait(false);
                await surface.Insert(second, Values("b-old", "b-old", earlier)).ConfigureAwait(false);
                await surface.Insert(second, Values("b-new", "b-new", later)).ConfigureAwait(false);

                var table = new TableId(name);
                var group = new ColumnRef(table, "group", QueryType.String, isNullable: false);
                var timestamp = new ColumnRef(table, "observed_at", QueryType.DateTimeOffset, isNullable: false);
                var result = await surface.QueryAcrossScopes(
                    connection.OpenSession(latestUnit, StorageAccess.PrivilegedAcrossScopes(
                        new StorageAccessAudit("conformance-suite", "verify-latest-scope-partition"))),
                    new QueryRequest(
                        table,
                        Predicate.AlwaysTrue.Instance,
                        [],
                        Projection.All,
                        Paging.None,
                        new LatestPerKey(group, timestamp))).ConfigureAwait(false);

                Require(result.Rows.Count == 2,
                    "LatestPerKey collapsed equal logical keys from different scopes");
                Require(result.Rows.Select(row => row.Values["value"] as string)
                        .Order(StringComparer.Ordinal)
                        .SequenceEqual(new[] { "a-new", "b-new" }),
                    "LatestPerKey did not retain the newest row inside each scope");
            }).ConfigureAwait(false);

            await RunCheck(checks, "CRUD outcomes and uniqueness", async () =>
            {
                var session = connection.OpenSession(global, StorageAccess.Global);
                var firstValues = scenario.Values("one", "first", "unique");
                var firstOutcome = await surface.Insert(session, firstValues).ConfigureAwait(false);
                Require(firstOutcome.Status == WriteOutcomeStatus.Inserted,
                    "insert did not report Inserted");
                Require((await surface.Insert(session, scenario.Values("two", "second", "unique")).ConfigureAwait(false))
                        .Status == WriteOutcomeStatus.UniqueViolation,
                    "duplicate unique value did not report UniqueViolation");
                Require((await surface.Update(session,
                        scenario.AttachKey(scenario.Values("one", "updated", "unique"), scenario.Key("one", firstOutcome)))
                        .ConfigureAwait(false)).Status == WriteOutcomeStatus.Updated,
                    "update did not report Updated");
                var upsertOutcome = await surface.Upsert(session, scenario.Values("two", "second", "other")).ConfigureAwait(false);
                Require(upsertOutcome.Status == WriteOutcomeStatus.Upserted,
                    "upsert did not report Upserted");
                Require((await surface.Delete(session, scenario.Key("two", upsertOutcome)).ConfigureAwait(false))
                        .Status == WriteOutcomeStatus.Deleted,
                    "delete did not report Deleted");
                Require((await surface.Delete(session, scenario.MissingKey("missing")).ConfigureAwait(false))
                        .Status == WriteOutcomeStatus.NotFound,
                    "missing delete did not report NotFound");
            }).ConfigureAwait(false);

            await RunCheck(checks, "declared optimistic concurrency", async () =>
            {
                var session = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("scope-c")));
                var inserted = await surface.Insert(session, scenario.Values("versioned", "first", null)).ConfigureAwait(false);
                Require(inserted.Version == 1, "optimistic insert must start at version one");
                var versionedKey = scenario.Key("versioned", inserted);
                Require((await surface.Update(session,
                        scenario.AttachKey(scenario.Values("versioned", "second", null), versionedKey),
                        WriteOptions.IfVersion(1)).ConfigureAwait(false)).Status == WriteOutcomeStatus.Updated,
                    "matching optimistic update failed");
                Require((await surface.Update(session,
                        scenario.AttachKey(scenario.Values("versioned", "stale", null), versionedKey),
                        WriteOptions.IfVersion(1)).ConfigureAwait(false)).Status == WriteOutcomeStatus.ConcurrencyConflict,
                    "stale optimistic update did not report a conflict");

                var globalSession = connection.OpenSession(global, StorageAccess.Global);
                var noVersion = await surface.Insert(globalSession, scenario.Values("no-version", "value", null)).ConfigureAwait(false);
                Require(noVersion.Version is null, "a unit without concurrency must not expose versions");
                Require((await surface.Read(globalSession, scenario.Key("no-version", noVersion)).ConfigureAwait(false))?.Version is null,
                    "a unit without concurrency must not store versions");
                await RequireThrows<InvalidOperationException>(async () => await surface.Update(
                    globalSession,
                    scenario.AttachKey(scenario.Values("no-version", "changed", null), scenario.Key("no-version", noVersion)),
                    WriteOptions.IfVersion(1)).ConfigureAwait(false)).ConfigureAwait(false);
            }).ConfigureAwait(false);

            await RunCheck(checks, "unit-of-work commit and rollback", async () =>
            {
                Require(connection.Capabilities.Any(capability =>
                        capability.Id == WellKnownCapabilities.AtomicCommit),
                    "the provider did not advertise its cross-unit atomic commit transaction");
                var session = connection.OpenSession(global, StorageAccess.Global);
                var committedValues = scenario.Values("committed", "yes", null);
                var committedWrite = RowWrite.Insert(global, committedValues);
                WriteOutcome committedOutcome;
                using (var committed = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, global))
                {
                    committed.Stage(committedWrite);
                    var report = await surface.CommitWithOutcomes(committed).ConfigureAwait(false);
                    committedOutcome = report.Outcomes.Single(outcome => ReferenceEquals(outcome.Write, committedWrite)).Outcome;
                    Require(report.Succeeded == 1, "staged insert failed");
                }
                Require(await surface.Read(session, scenario.Key("committed", committedOutcome)).ConfigureAwait(false) is not null,
                    "committed value was not visible");

                using (var rolledBack = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, global))
                {
                    rolledBack.Stage(RowWrite.Insert(global, scenario.Values("rolled-back", "no", null)));
                    rolledBack.Rollback();
                }
                Require(await surface.Read(session, scenario.MissingKey("rolled-back")).ConfigureAwait(false) is null,
                    "rolled-back value was visible");
            }).ConfigureAwait(false);

            if (surface.IsAsync)
            {
                await RunCheck(checks, "cancellation is refused before provider work", async () =>
                {
                    var session = connection.OpenSession(global, StorageAccess.Global);
                    var cancelled = ConformanceExecution.Asynchronous(new CancellationToken(canceled: true));
                    var page = new QueryRequest(
                        new TableId(global.Name),
                        Predicate.AlwaysTrue.Instance,
                        [],
                        Projection.All,
                        Paging.Keyset(1));
                    await RequireThrows<OperationCanceledException>(
                        async () => await cancelled.Read(session, scenario.MissingKey("cancelled")).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    // A query is proven too: a provider that renders it as a server-side pipeline
                    // must issue that pipeline on a surface that carries the token.
                    await RequireThrows<OperationCanceledException>(
                        async () => await cancelled.Query(session, page).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    await RequireThrows<OperationCanceledException>(
                        async () => await cancelled.Insert(session, scenario.Values("cancelled", "no", null)).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    Require(await surface.Read(session, scenario.MissingKey("cancelled")).ConfigureAwait(false) is null,
                        "a cancelled write reached the provider");
                }).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            checks.Add(new ConformanceCheck("provider factory and connection", false, exception.Message));
        }

        return new ConformanceReport(checks);
    }

    private static async ValueTask RunCheck(
        ICollection<ConformanceCheck> checks,
        string name,
        Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            checks.Add(new ConformanceCheck(name, true));
        }
        catch (Exception exception)
        {
            checks.Add(new ConformanceCheck(name, false, exception.Message));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new ConformanceFailureException("contract", message);
    }

    private static void RequireThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new ConformanceFailureException("contract", $"Expected {typeof(TException).Name}.");
    }

    private static async ValueTask RequireThrows<TException>(Func<ValueTask> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new ConformanceFailureException("contract", $"Expected {typeof(TException).Name}.");
    }

    private static void AssertCatalog(StorageUnit declaration, IReadOnlyList<ProviderIndex> actual)
    {
        Require(actual.Count == declaration.Indexes.Count,
            "the provider catalog returned a different number of indexes");

        foreach (var expected in declaration.Indexes)
        {
            var found = actual.SingleOrDefault(index => index.Name == expected.Name);
            Require(found is not null, $"the provider catalog did not report index '{expected.Name}'");
            Require(found!.IsUnique == expected.IsUnique,
                $"catalog uniqueness differs for index '{expected.Name}'");
            Require(found.MissingValues == expected.MissingValues,
                $"catalog missing-value behavior differs for index '{expected.Name}'");
            Require(found.SchemaVersion == expected.SchemaVersion,
                $"catalog schema version differs for index '{expected.Name}'");
            Require(found.Columns.Count == expected.Columns.Count,
                $"catalog column count differs for index '{expected.Name}'");
            for (var i = 0; i < expected.Columns.Count; i++)
            {
                Require(found.Columns[i].Column == expected.Columns[i].Column &&
                        found.Columns[i].Direction == expected.Columns[i].Direction,
                    $"catalog columns differ for index '{expected.Name}'");
            }
        }
    }

    /// <summary>
    /// Dispatches one storage operation to the surface under proof, so every conformance check is
    /// written once and executed twice.
    /// </summary>
    private readonly struct ConformanceExecution
    {
        private ConformanceExecution(bool isAsync, CancellationToken cancellationToken)
        {
            IsAsync = isAsync;
            CancellationToken = cancellationToken;
        }

        internal static ConformanceExecution Synchronous { get; } = new(false, CancellationToken.None);

        internal static ConformanceExecution Asynchronous(CancellationToken cancellationToken) =>
            new(true, cancellationToken);

        internal bool IsAsync { get; }

        private CancellationToken CancellationToken { get; }

        internal ValueTask<StoredEntry?> Read(IStorageSession session, StorageKey key) => IsAsync
            ? session.ReadAsync(key, CancellationToken)
            : new(session.Read(key));

        internal ValueTask<QueryMaterializedResult> Query(IStorageSession session, QueryRequest request) => IsAsync
            ? session.QueryAsync(request, cancellationToken: CancellationToken)
            : new(session.Query(request));

        internal ValueTask<CrossScopeQueryResult> QueryAcrossScopes(IStorageSession session, QueryRequest request) => IsAsync
            ? session.QueryAcrossScopesAsync(request, cancellationToken: CancellationToken)
            : new(session.QueryAcrossScopes(request));

        internal ValueTask<WriteOutcome> Insert(IStorageSession session, StorageValues values) => IsAsync
            ? session.InsertAsync(values, cancellationToken: CancellationToken)
            : new(session.Insert(values));

        internal ValueTask<WriteOutcome> Update(
            IStorageSession session,
            StorageValues values,
            WriteOptions? options = null) => IsAsync
            ? session.UpdateAsync(values, options, CancellationToken)
            : new(session.Update(values, options));

        internal ValueTask<WriteOutcome> Upsert(IStorageSession session, StorageValues values) => IsAsync
            ? session.UpsertAsync(values, cancellationToken: CancellationToken)
            : new(session.Upsert(values));

        internal ValueTask<WriteOutcome> Delete(IStorageSession session, StorageKey key) => IsAsync
            ? session.DeleteAsync(key, cancellationToken: CancellationToken)
            : new(session.Delete(key));

        internal ValueTask<BatchWriteReport> CommitWithOutcomes(IUnitOfWork unitOfWork) => IsAsync
            ? unitOfWork.CommitWithOutcomesAsync(CancellationToken)
            : new(unitOfWork.CommitWithOutcomes());
    }
}
