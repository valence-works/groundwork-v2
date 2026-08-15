using Groundwork.Kernel;

namespace Groundwork.Testing;

/// <summary>Runs the provider-neutral behavioral contract against one provider factory.</summary>
public static class ConformanceSuite
{
    public static ConformanceReport Run(
        IStorageProviderFactory factory,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        var checks = new List<ConformanceCheck>();
        try
        {
            using var connection = factory.Create(connectionString);
            if (connection is null)
                throw new InvalidOperationException("The provider factory returned no connection.");

            var global = ProbeModel.Global;
            var scoped = ProbeModel.Scoped;
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

                var first = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("scope-a")));
                var second = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("scope-b")));
                Require(first.Insert(ProbeModel.Values("same", "a")).Status == WriteOutcomeStatus.Inserted,
                    "the first scoped insert failed");
                Require(second.Insert(ProbeModel.Values("same", "b")).Status == WriteOutcomeStatus.Inserted,
                    "the second scoped insert failed");
                Require(first.Read(ProbeModel.Key("same"))?.Values.Values["value"] as string == "a",
                    "the first scope could not read its own value");
                Require(second.Read(ProbeModel.Key("same"))?.Values.Values["value"] as string == "b",
                    "the second scope could not read its own value");
            });

            RunCheck(checks, "CRUD outcomes and uniqueness", () =>
            {
                var session = connection.OpenSession(global, StorageAccess.Global);
                Require(session.Insert(ProbeModel.Values("one", "first", "unique")).Status == WriteOutcomeStatus.Inserted,
                    "insert did not report Inserted");
                Require(session.Insert(ProbeModel.Values("two", "second", "unique")).Status == WriteOutcomeStatus.UniqueViolation,
                    "duplicate unique value did not report UniqueViolation");
                Require(session.Update(ProbeModel.Values("one", "updated", "unique")).Status == WriteOutcomeStatus.Updated,
                    "update did not report Updated");
                Require(session.Upsert(ProbeModel.Values("two", "second", "other")).Status == WriteOutcomeStatus.Upserted,
                    "upsert did not report Upserted");
                Require(session.Delete(ProbeModel.Key("two")).Status == WriteOutcomeStatus.Deleted,
                    "delete did not report Deleted");
                Require(session.Delete(ProbeModel.Key("missing")).Status == WriteOutcomeStatus.NotFound,
                    "missing delete did not report NotFound");
            });

            RunCheck(checks, "declared optimistic concurrency", () =>
            {
                var session = connection.OpenSession(scoped, StorageAccess.Scoped(new StorageScope("scope-c")));
                var inserted = session.Insert(ProbeModel.Values("versioned", "first"));
                Require(inserted.Version == 1, "optimistic insert must start at version one");
                Require(session.Update(ProbeModel.Values("versioned", "second"),
                        WriteOptions.IfVersion(1)).Status == WriteOutcomeStatus.Updated,
                    "matching optimistic update failed");
                Require(session.Update(ProbeModel.Values("versioned", "stale"),
                        WriteOptions.IfVersion(1)).Status == WriteOutcomeStatus.ConcurrencyConflict,
                    "stale optimistic update did not report a conflict");

                var globalSession = connection.OpenSession(global, StorageAccess.Global);
                var noVersion = globalSession.Insert(ProbeModel.Values("no-version", "value"));
                Require(noVersion.Version is null, "a unit without concurrency must not expose versions");
                Require(globalSession.Read(ProbeModel.Key("no-version"))?.Version is null,
                    "a unit without concurrency must not store versions");
                RequireThrows<InvalidOperationException>(() => globalSession.Update(
                    ProbeModel.Values("no-version", "changed"), WriteOptions.IfVersion(1)));
            });

            RunCheck(checks, "unit-of-work commit and rollback", () =>
            {
                var session = connection.OpenSession(global, StorageAccess.Global);
                using (var committed = connection.BeginUnitOfWork(StorageAccess.Global, global))
                {
                    Require(committed.OpenSession(global).Insert(ProbeModel.Values("committed", "yes")).Succeeded,
                        "staged insert failed");
                    committed.Commit();
                }
                Require(session.Read(ProbeModel.Key("committed")) is not null,
                    "committed value was not visible");

                using (var rolledBack = connection.BeginUnitOfWork(StorageAccess.Global, global))
                {
                    Require(rolledBack.OpenSession(global).Insert(ProbeModel.Values("rolled-back", "no")).Succeeded,
                        "rollback insert failed");
                    rolledBack.Rollback();
                }
                Require(session.Read(ProbeModel.Key("rolled-back")) is null,
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

    private static class ProbeModel
    {
        internal static readonly StorageUnit Global = Create("conformance-global", ScopePolicy.Global,
            ConcurrencyDeclaration.None);
        internal static readonly StorageUnit Scoped = Create("conformance-scoped", ScopePolicy.Scoped,
            ConcurrencyDeclaration.Optimistic());

        internal static StorageValues Values(string id, string value, string? unique = null) =>
            new(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["value"] = value,
                ["uniqueValue"] = unique ?? id
            });

        internal static StorageKey Key(string id) =>
            new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id });

        private static StorageUnit Create(
            string id,
            ScopePolicy scope,
            ConcurrencyDeclaration concurrency) => new()
        {
            Id = new StorageUnitId(id),
            Name = id,
            Columns =
            [
                // Keep the provider-neutral probe's variable-length primary key bounded so
                // providers can validate native key widths from the declaration alone.
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 256 },
                new ColumnDefinition { Name = "uniqueValue", Type = PortableType.String, MaxLength = 256 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Scope = scope,
            Concurrency = concurrency,
            Indexes =
            [
                new IndexDefinition { Name = "by-value", Columns = [new IndexColumn("value")] },
                new IndexDefinition
                {
                    Name = "unique-value",
                    Columns = [new IndexColumn("uniqueValue")],
                    IsUnique = true,
                    MissingValues = MissingValueBehavior.Excluded
                }
            ]
        };
    }
}
