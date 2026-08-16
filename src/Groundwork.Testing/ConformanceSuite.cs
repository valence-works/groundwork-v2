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
        ConformanceScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(scenario);

        var checks = new List<ConformanceCheck>();
        try
        {
            using var connection = factory.Create(connectionString);
            if (connection is null)
                throw new InvalidOperationException("The provider factory returned no connection.");

            var global = scenario.Global;
            var scoped = scenario.Scoped;
            RunCheck(checks, "schema apply and provider catalog", () =>
            {
                var first = connection.Schema.Apply(global);
                Require(!first.IsNoOp, "the first schema application must have work");
                var second = connection.Schema.Apply(global);
                Require(second.IsNoOp, "reapplying an unchanged schema must be a no-op");
                Require(connection.Schema.Diff(global).IsEmpty,
                    "the provider reported a non-empty diff for the applied declaration");
                var indexes = connection.Catalog.ReadIndexes(global.Id);
                AssertCatalog(global, indexes);
            });

            RunCheck(checks, "storage-scope isolation", () =>
            {
                connection.Schema.Apply(scoped);
                RequireThrows<InvalidOperationException>(
                    () => connection.OpenSession(scoped, StorageAccess.Global));
                RequireThrows<InvalidOperationException>(
                    () => connection.OpenSession(global, StorageAccess.Scoped(new StorageScope("scope-a"))));

                var first = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("scope-\U00010000")));
                var second = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("scope-\uE000")));
                var firstValues = scenario.Values("same", "a", null);
                var firstOutcome = first.Insert(firstValues);
                Require(firstOutcome.Status == WriteOutcomeStatus.Inserted,
                    "the first scoped insert failed");
                var secondValues = scenario.Values("same", "b", null);
                var secondOutcome = second.Insert(secondValues);
                Require(secondOutcome.Status == WriteOutcomeStatus.Inserted,
                    "the second scoped insert failed");
                Require(first.Read(scenario.Key("same", firstOutcome))?.Values.Values[scenario.ValueColumn] as string == "a",
                    "the first scope could not read its own value");
                Require(second.Read(scenario.Key("same", secondOutcome))?.Values.Values[scenario.ValueColumn] as string == "b",
                    "the second scope could not read its own value");
            });

            RunCheck(checks, "audited privileged cross-scope query", () =>
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

                RequireThrows<InvalidOperationException>(() => session.Query(request));
                RequireThrows<InvalidOperationException>(() => session.Read(scenario.MissingKey("same")));
                RequireThrows<InvalidOperationException>(() => session.Insert(scenario.Values("refused", "write", null)));
                RequireThrows<InvalidOperationException>(() =>
                    connection.BeginUnitOfWork(access, scoped));

                var first = session.QueryAcrossScopes(request);
                Require(first.TotalCount == 2, "the privileged query did not count both scopes");
                Require(first.Rows.Count == 1, "the first privileged page did not contain one row");
                Require(first.NextContinuationToken is not null,
                    "the first privileged page did not return a continuation token");
                var second = session.QueryAcrossScopes(new QueryRequest(
                    table,
                    request.Where,
                    request.Order,
                    request.Projection,
                    Paging.Continuation(first.NextContinuationToken!, 1),
                    request.Result));
                Require(second.Rows.Count == 1, "the second privileged page did not contain one row");
                Require(first.Rows[0].Scope != second.Rows[0].Scope,
                    "the privileged pages did not preserve distinct row scopes");
                Require(first.Rows.Concat(second.Rows).All(row =>
                        row.Values.ContainsKey(scenario.ValueColumn)),
                    "the privileged query did not preserve public row values");
            });

            RunCheck(checks, "cross-scope latest remains partitioned by scope", () =>
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
                first.Insert(Values("a-old", "a-old", earlier));
                first.Insert(Values("a-new", "a-new", later));
                second.Insert(Values("b-old", "b-old", earlier));
                second.Insert(Values("b-new", "b-new", later));

                var table = new TableId(name);
                var group = new ColumnRef(table, "group", QueryType.String, isNullable: false);
                var timestamp = new ColumnRef(table, "observed_at", QueryType.DateTimeOffset, isNullable: false);
                var result = connection.OpenSession(latestUnit, StorageAccess.PrivilegedAcrossScopes(
                        new StorageAccessAudit("conformance-suite", "verify-latest-scope-partition")))
                    .QueryAcrossScopes(new QueryRequest(
                        table,
                        Predicate.AlwaysTrue.Instance,
                        [],
                        Projection.All,
                        Paging.None,
                        new LatestPerKey(group, timestamp)));

                Require(result.Rows.Count == 2,
                    "LatestPerKey collapsed equal logical keys from different scopes");
                Require(result.Rows.Select(row => row.Values["value"] as string)
                        .Order(StringComparer.Ordinal)
                        .SequenceEqual(new[] { "a-new", "b-new" }),
                    "LatestPerKey did not retain the newest row inside each scope");
            });

            RunCheck(checks, "CRUD outcomes and uniqueness", () =>
            {
                var session = connection.OpenSession(global, StorageAccess.Global);
                var firstValues = scenario.Values("one", "first", "unique");
                var firstOutcome = session.Insert(firstValues);
                Require(firstOutcome.Status == WriteOutcomeStatus.Inserted,
                    "insert did not report Inserted");
                Require(session.Insert(scenario.Values("two", "second", "unique")).Status == WriteOutcomeStatus.UniqueViolation,
                    "duplicate unique value did not report UniqueViolation");
                Require(session.Update(scenario.AttachKey(scenario.Values("one", "updated", "unique"), scenario.Key("one", firstOutcome))).Status == WriteOutcomeStatus.Updated,
                    "update did not report Updated");
                var upsertOutcome = session.Upsert(scenario.Values("two", "second", "other"));
                Require(upsertOutcome.Status == WriteOutcomeStatus.Upserted,
                    "upsert did not report Upserted");
                Require(session.Delete(scenario.Key("two", upsertOutcome)).Status == WriteOutcomeStatus.Deleted,
                    "delete did not report Deleted");
                Require(session.Delete(scenario.MissingKey("missing")).Status == WriteOutcomeStatus.NotFound,
                    "missing delete did not report NotFound");
            });

            RunCheck(checks, "declared optimistic concurrency", () =>
            {
                var session = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("scope-c")));
                var inserted = session.Insert(scenario.Values("versioned", "first", null));
                Require(inserted.Version == 1, "optimistic insert must start at version one");
                var versionedKey = scenario.Key("versioned", inserted);
                Require(session.Update(scenario.AttachKey(scenario.Values("versioned", "second", null), versionedKey),
                        WriteOptions.IfVersion(1)).Status == WriteOutcomeStatus.Updated,
                    "matching optimistic update failed");
                Require(session.Update(scenario.AttachKey(scenario.Values("versioned", "stale", null), versionedKey),
                        WriteOptions.IfVersion(1)).Status == WriteOutcomeStatus.ConcurrencyConflict,
                    "stale optimistic update did not report a conflict");

                var globalSession = connection.OpenSession(global, StorageAccess.Global);
                var noVersion = globalSession.Insert(scenario.Values("no-version", "value", null));
                Require(noVersion.Version is null, "a unit without concurrency must not expose versions");
                Require(globalSession.Read(scenario.Key("no-version", noVersion))?.Version is null,
                    "a unit without concurrency must not store versions");
                RequireThrows<InvalidOperationException>(() => globalSession.Update(
                    scenario.AttachKey(scenario.Values("no-version", "changed", null), scenario.Key("no-version", noVersion)),
                    WriteOptions.IfVersion(1)));
            });

            RunCheck(checks, "unit-of-work commit and rollback", () =>
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
                    var report = committed.CommitWithOutcomes();
                    committedOutcome = report.Outcomes.Single(outcome => ReferenceEquals(outcome.Write, committedWrite)).Outcome;
                    Require(report.Succeeded == 1, "staged insert failed");
                }
                Require(session.Read(scenario.Key("committed", committedOutcome)) is not null,
                    "committed value was not visible");

                using (var rolledBack = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, global))
                {
                    rolledBack.Stage(RowWrite.Insert(global, scenario.Values("rolled-back", "no", null)));
                    rolledBack.Rollback();
                }
                Require(session.Read(scenario.MissingKey("rolled-back")) is null,
                    "rolled-back value was visible");
            });
        }
        catch (Exception exception)
        {
            checks.Add(new ConformanceCheck("provider factory and connection", false, exception.Message));
        }

        return new ConformanceReport(checks);
    }

    private static void RunCheck(
        ICollection<ConformanceCheck> checks,
        string name,
        Action action)
    {
        try
        {
            action();
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

}
