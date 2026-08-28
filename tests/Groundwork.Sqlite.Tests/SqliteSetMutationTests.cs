using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Query.Planning;
using Groundwork.Query.Linq.Execution;
using Groundwork.Store;
using Xunit;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// The provider-neutral admission rules for set-based mutation (#89), proven against a real
/// provider rather than a stub so that every refusal is one a caller would actually meet.
/// </summary>
public sealed class SqliteSetMutationTests
{
    [Fact]
    public void Set_based_mutation_refuses_every_assignment_no_provider_could_apply_faithfully()
    {
        using var connection = Open();
        var unit = Unit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Row("a", "open", "one"));

        var empty = Assert.Throws<ArgumentException>(() =>
            session.UpdateWhere(Status(unit, "open"), new Dictionary<string, object?>()));
        Assert.Contains("GW-SET-003", empty.Message, StringComparison.Ordinal);

        var unknown = Assert.Throws<ArgumentException>(() => session.UpdateWhere(
            Status(unit, "open"), new Dictionary<string, object?> { ["nope"] = 1L }));
        Assert.Contains("GW-SET-002", unknown.Message, StringComparison.Ordinal);

        var providerOwned = Assert.Throws<ArgumentException>(() => session.UpdateWhere(
            Status(unit, "open"), new Dictionary<string, object?> { ["__groundwork_scope"] = "x" }));
        Assert.Contains("GW-SET-002", providerOwned.Message, StringComparison.Ordinal);

        var keyColumn = Assert.Throws<ArgumentException>(() => session.UpdateWhere(
            Status(unit, "open"), new Dictionary<string, object?> { ["id"] = "b" }));
        Assert.Contains("GW-SET-002", keyColumn.Message, StringComparison.Ordinal);

        var json = Assert.Throws<ArgumentException>(() => session.UpdateWhere(
            Status(unit, "open"), new Dictionary<string, object?> { ["document"] = "{}" }));
        Assert.Contains("GW-SET-004", json.Message, StringComparison.Ordinal);

        var wrongType = Assert.Throws<ArgumentException>(() => session.UpdateWhere(
            Status(unit, "open"), new Dictionary<string, object?> { ["label"] = 7L }));
        Assert.Contains("Assignment value for column 'label'", wrongType.Message, StringComparison.Ordinal);

        Assert.Equal("one", Assert.Single(Read(session, unit))["label"]);
    }

    /// <summary>
    /// A relational <c>RenderPredicateFragment</c> does not run the portability validation its full
    /// query renderer runs, so set-based mutation runs it before reaching any provider. The
    /// declared decimal is portable and index-covered; the one in this predicate is not, and
    /// without the validation SQLite renders and executes it.
    /// </summary>
    [Fact]
    public void A_non_portable_predicate_is_refused_before_a_provider_sees_it()
    {
        using var connection = Open();
        var unit = Unit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Row("a", "open", "one"));

        var amount = new ColumnRef(
            new TableId(unit.Name), "amount", QueryType.Decimal, isNullable: true,
            decimalPrecision: 10, decimalScale: 2);
        var refusal = Assert.Throws<QueryRenderException>(() => session.DeleteWhere(
            new Predicate.Equal(amount, QueryConstant.Of(amount, 1m))));
        Assert.Equal("GW-SEM-DECIMAL-001", refusal.Code);
        Assert.Single(Read(session, unit));
    }

    /// <summary>
    /// A folded source column carries a provider-owned search key. Assigning the source through a
    /// set-based update must move the search key with it, or the row stops answering the
    /// case-insensitive query that found it a moment earlier.
    /// </summary>
    [Fact]
    public void Assigning_a_folded_column_moves_its_search_key()
    {
        using var connection = Open();
        var unit = FoldedUnit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "a",
            ["status"] = "open",
            ["name"] = "Alpha"
        }));

        Assert.Equal("a", Assert.Single(ByNamePrefix(session, unit, "alph"))["id"]);
        var aggregate = session.UpdateWhere(
            Status(unit, "open"),
            new Dictionary<string, object?> { ["name"] = "Omega" });
        Assert.Equal(1L, aggregate.MatchedRows);
        Assert.False(aggregate.IsExact);
        Assert.Empty(aggregate.Outcomes);

        Assert.Empty(ByNamePrefix(session, unit, "alph"));
        Assert.Equal("a", Assert.Single(ByNamePrefix(session, unit, "omeg"))["id"]);

        Assert.Equal(1L, session.UpdateWhere(
            NamePrefix(unit, "omeg"),
            new Dictionary<string, object?> { ["name"] = "Final" }).MatchedRows);
        Assert.Empty(ByNamePrefix(session, unit, "omeg"));
        Assert.Equal("a", Assert.Single(ByNamePrefix(session, unit, "fina"))["id"]);
    }

    [Fact]
    public async Task Direct_capability_calls_validate_logical_assignments_before_provider_work()
    {
        using var connection = Open();
        var unit = FoldedUnit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "a",
            ["status"] = "open",
            ["name"] = "Alpha"
        }));
        var native = Assert.IsAssignableFrom<ISetMutationStorageSession>(session);

        var key = Assert.Throws<ArgumentException>(() => native.UpdateWhere(
            Status(unit, "open"),
            new Dictionary<string, object?> { ["id"] = "b" }));
        Assert.Contains("GW-SET-002", key.Message, StringComparison.Ordinal);

        var searchKey = Assert.Throws<ArgumentException>(() => native.UpdateWhere(
            Status(unit, "open"),
            new Dictionary<string, object?> { [SearchKeyProjection.ColumnName("name")] = "forged" }));
        Assert.Contains("GW-SET-002", searchKey.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => native.UpdateWhereAsync(
            Status(unit, "open"),
            new Dictionary<string, object?> { ["id"] = "c" }).AsTask());

        Assert.Equal("Alpha", Assert.Single(Read(session, unit))["name"]);
    }

    [Fact]
    public async Task Direct_capability_cannot_relocate_a_scoped_row_by_assigning_scope()
    {
        using var connection = Open();
        var unit = Unit() with { Scope = ScopePolicy.Scoped };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var first = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var second = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));
        first.Insert(Row("a", "open", "first"));
        second.Insert(Row("a", "open", "second"));
        var native = Assert.IsAssignableFrom<ISetMutationStorageSession>(first);

        var refusal = Assert.Throws<ArgumentException>(() => native.UpdateWhere(
            Status(unit, "open"),
            new Dictionary<string, object?> { [ProviderOwnedColumns.Scope] = "scope-b" }));
        Assert.Contains("GW-SET-002", refusal.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => native.UpdateWhereAsync(
            Status(unit, "open"),
            new Dictionary<string, object?> { [ProviderOwnedColumns.Scope] = "scope-b" }).AsTask());

        Assert.Equal("first", Assert.Single(Read(first, unit))["label"]);
        Assert.Equal("second", Assert.Single(Read(second, unit))["label"]);
    }

    [Fact]
    public void Direct_unit_of_work_capability_refuses_invalid_assignments_before_flush()
    {
        using var connection = Open();
        var unit = Unit();
        Assert.True(connection.Schema.Apply(unit).Applied);

        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, unit))
        {
            var session = Assert.IsAssignableFrom<ISetMutationStorageSession>(work.OpenSession(unit));
            work.Stage(RowWrite.Insert(unit, Row("staged", "open", "one")));

            var refusal = Assert.Throws<ArgumentException>(() => session.UpdateWhere(
                Status(unit, "open"),
                new Dictionary<string, object?> { ["id"] = "moved" }));
            Assert.Contains("GW-SET-002", refusal.Message, StringComparison.Ordinal);
            work.Rollback();
        }

        var reader = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Null(reader.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "staged" })));
    }

    [Fact]
    public void A_privileged_cross_scope_session_cannot_mutate_a_set()
    {
        using var connection = Open();
        var unit = Unit() with { Scope = ScopePolicy.Scoped };
        Assert.True(connection.Schema.Apply(unit).Applied);
        connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("s"))).Insert(Row("a", "open", "one"));
        var privileged = connection.OpenSession(
            unit,
            StorageAccess.PrivilegedAcrossScopes(new StorageAccessAudit("operator", "audit")));

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            privileged.DeleteWhere(Status(unit, "open")));
        Assert.Contains("GW-ACCESS-003", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scoped_key_mutation_only_affects_the_callers_scope()
    {
        using var connection = Open();
        var unit = Unit() with { Scope = ScopePolicy.Scoped };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var first = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var second = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));
        first.Insert(Row("a", "open", "first"));
        second.Insert(Row("a", "open", "second"));

        Assert.Equal(1L, first.DeleteWhere(KeyEquals(unit, "a")).MatchedRows);
        Assert.Null(first.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "a" })));
        Assert.NotNull(second.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "a" })));
    }

    [Fact]
    public void Exact_update_returns_one_write_outcome_per_matched_key()
    {
        using var connection = Open();
        var unit = Unit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Row("a", "open", "one"));
        session.Insert(Row("b", "open", "two"));
        session.Insert(Row("c", "closed", "three"));

        var result = session.UpdateWhere(
            Status(unit, "open"),
            new Dictionary<string, object?> { ["label"] = "updated" },
            new SetMutationOptions { OutcomeMode = SetMutationOutcomeMode.Exact });

        Assert.True(result.IsExact);
        Assert.Equal(2L, result.MatchedRows);
        Assert.Equal(2, result.Outcomes.Count);
        Assert.All(result.Outcomes, outcome => Assert.Equal(WriteOutcomeStatus.Updated, outcome.Outcome.Status));
        Assert.Equal(new[] { "a", "b" }, result.Outcomes.Select(outcome => outcome.Key.Values["id"]).OrderBy(value => value).ToArray());
    }

    [Fact]
    public void Exact_update_keeps_composite_key_outcomes_in_deterministic_logical_order()
    {
        using var connection = Open();
        var unit = CompositeUnit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(CompositeRow("z", "b"));
        session.Insert(CompositeRow("a", "c"));
        session.Insert(CompositeRow("a", "a"));

        var result = session.UpdateWhere(
            Status(unit, "open"),
            new Dictionary<string, object?> { ["label"] = "updated" },
            SetMutationOptions.Exact);

        Assert.Equal(3L, result.MatchedRows);
        Assert.Equal(
            new[] { ("a", "a"), ("a", "c"), ("z", "b") },
            result.Outcomes.Select(outcome =>
                (Assert.IsType<string>(outcome.Key.Values["tenant"]), Assert.IsType<string>(outcome.Key.Values["id"]))).ToArray());
    }

    [Fact]
    public void Exact_update_uses_the_logical_predicate_for_folded_search_keys()
    {
        using var connection = Open();
        var unit = FoldedUnit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(new StorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = "a",
            ["status"] = "open",
            ["name"] = "Alpha"
        }));

        var result = session.UpdateWhere(
            NamePrefix(unit, "alph"),
            new Dictionary<string, object?> { ["name"] = "Omega" },
            SetMutationOptions.Exact);

        Assert.Equal(1L, result.MatchedRows);
        Assert.Equal("a", Assert.Single(result.Outcomes).Key.Values["id"]);
        Assert.Equal("a", Assert.Single(ByNamePrefix(session, unit, "omeg"))["id"]);
    }

    [Fact]
    public void Exact_update_preserves_keyed_optimistic_version_outcomes()
    {
        using var connection = Open();
        var unit = Unit() with { Concurrency = ConcurrencyDeclaration.Optimistic() };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Row("a", "open", "one"));

        var result = session.UpdateWhere(
            Status(unit, "open"),
            new Dictionary<string, object?> { ["label"] = "updated" },
            SetMutationOptions.Exact);

        var outcome = Assert.Single(result.Outcomes).Outcome;
        Assert.Equal(WriteOutcomeStatus.Updated, outcome.Status);
        Assert.Equal(2L, outcome.Version);
        Assert.Equal(2L, session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "a" }))!.Version);
    }

    [Fact]
    public void Exact_scoped_update_orders_logical_keys_without_the_scope_discriminator()
    {
        using var connection = Open();
        var unit = Unit() with { Scope = ScopePolicy.Scoped };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var first = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var second = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));
        first.Insert(Row("a", "open", "first"));
        second.Insert(Row("a", "open", "second"));

        var result = first.UpdateWhere(
            Status(unit, "open"),
            new Dictionary<string, object?> { ["label"] = "updated" },
            SetMutationOptions.Exact);

        Assert.Equal(1L, result.MatchedRows);
        Assert.Equal("a", Assert.Single(result.Outcomes).Key.Values["id"]);
        Assert.Equal("updated", first.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "a" }))!.Values.Values["label"]);
        Assert.Equal("second", second.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "a" }))!.Values.Values["label"]);
    }

    [Fact]
    public async Task Exact_scoped_delete_async_orders_logical_keys_without_the_scope_discriminator()
    {
        using var connection = Open();
        var unit = Unit() with { Scope = ScopePolicy.Scoped };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var first = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var second = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));
        first.Insert(Row("a", "gone", "first"));
        second.Insert(Row("a", "gone", "second"));

        var result = await first.DeleteWhereAsync(Status(unit, "gone"), SetMutationOptions.Exact);

        Assert.Equal(1L, result.MatchedRows);
        Assert.Equal("a", Assert.Single(result.Outcomes).Key.Values["id"]);
        Assert.Null(first.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "a" })));
        Assert.NotNull(second.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "a" })));
    }

    [Fact]
    public async Task Exact_delete_async_returns_deleted_outcomes_and_empty_exact_results_are_distinct()
    {
        using var connection = Open();
        var unit = Unit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Row("a", "gone", "one"));

        var deleted = await session.DeleteWhereAsync(
            Status(unit, "gone"), SetMutationOptions.Exact);
        Assert.True(deleted.IsExact);
        Assert.Equal(1L, deleted.MatchedRows);
        Assert.Equal(WriteOutcomeStatus.Deleted, Assert.Single(deleted.Outcomes).Outcome.Status);

        var empty = await session.DeleteWhereAsync(
            Status(unit, "gone"), SetMutationOptions.Exact);
        Assert.True(empty.IsExact);
        Assert.Equal(0L, empty.MatchedRows);
        Assert.Empty(empty.Outcomes);
    }

    [Fact]
    public async Task Exact_async_cancellation_after_a_keyed_write_poisoned_unit_cannot_commit_partial_updates()
    {
        using var connection = Open();
        var unit = Unit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var seed = connection.OpenSession(unit, StorageAccess.Global);
        seed.Insert(Row("a", "open", "one"));
        seed.Insert(Row("b", "open", "two"));

        using var cancellation = new CancellationTokenSource();
        var observer = new CancelAfterFirstUpdate(cancellation);
        using var work = connection.BeginUnitOfWork(
            StorageAccess.Global,
            BatchWriteOptions.Exact,
            observer,
            unit);
        var session = work.OpenSession(unit);

        await Assert.ThrowsAsync<OperationCanceledException>(() => session.UpdateWhereAsync(
            Status(unit, "open"),
            new Dictionary<string, object?> { ["label"] = "updated" },
            SetMutationOptions.Exact,
            cancellation.Token).AsTask());

        // The first keyed update ran inside the provider transaction. The batch failure marker
        // routes the later commit through the provider's normal rollback path instead of allowing
        // that partial exact mutation to become durable.
        Assert.Throws<InvalidOperationException>(() => work.Commit());

        var reader = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal("one", reader.Read(Key(unit, "a"))!.Values.Values["label"]);
        Assert.Equal("two", reader.Read(Key(unit, "b"))!.Values.Values["label"]);
    }

    [Fact]
    public async Task Exact_async_provider_failure_after_a_keyed_write_poisoned_unit_cannot_commit_partial_updates()
    {
        using var connection = Open();
        var unit = UniqueUnit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        var seed = connection.OpenSession(unit, StorageAccess.Global);
        seed.Insert(Row("a", "open", "one"));
        seed.Insert(Row("b", "open", "two"));

        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        var session = work.OpenSession(unit);

        await Assert.ThrowsAsync<SqliteException>(() => session.UpdateWhereAsync(
            Status(unit, "open"),
            new Dictionary<string, object?> { ["label"] = "same" },
            SetMutationOptions.Exact).AsTask());

        Assert.Throws<InvalidOperationException>(() => work.Commit());

        var reader = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal("one", reader.Read(Key(unit, "a"))!.Values.Values["label"]);
        Assert.Equal("two", reader.Read(Key(unit, "b"))!.Values.Values["label"]);
    }

    [Fact]
    public void Exact_delete_flushes_prior_stage_and_runs_before_a_later_keyed_stage()
    {
        using var connection = Open();
        var unit = Unit();
        Assert.True(connection.Schema.Apply(unit).Applied);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, unit);
        var session = work.OpenSession(unit);

        work.Stage(RowWrite.Insert(unit, Row("staged", "gone", "before")));
        var result = session.DeleteWhere(
            Status(unit, "gone"),
            new SetMutationOptions { OutcomeMode = SetMutationOutcomeMode.Exact });
        Assert.Equal(1L, result.MatchedRows);
        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal("staged", outcome.Key.Values["id"]);
        Assert.Equal(WriteOutcomeStatus.Deleted, outcome.Outcome.Status);

        work.Stage(RowWrite.Upsert(unit, Row("staged", "gone", "after")));
        work.Commit();

        var reader = connection.OpenSession(unit, StorageAccess.Global);
        var row = reader.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "staged" }));
        Assert.NotNull(row);
        Assert.Equal("after", row!.Values.Values["label"]);
    }

    private static IStorageProviderConnection Open() => new SqliteProviderFactory().Create(
        "Data Source=file:groundwork_p43_" + Guid.NewGuid().ToString("N") + "?mode=memory&cache=shared");

    private static Predicate Status(StorageUnit unit, string value)
    {
        var column = new ColumnRef(new TableId(unit.Name), "status", QueryType.String, isNullable: false, maxLength: 32);
        return new Predicate.Equal(column, QueryConstant.Of(column, value));
    }

    private static Predicate KeyEquals(StorageUnit unit, string value)
    {
        var column = new ColumnRef(new TableId(unit.Name), "id", QueryType.String, isNullable: false, maxLength: 64);
        return new Predicate.Equal(column, QueryConstant.Of(column, value));
    }

    private static StorageKey Key(StorageUnit unit, string value) =>
        new(new Dictionary<string, object?> { [unit.Key.Columns[0]] = value });

    /// <summary>
    /// A case-insensitive prefix read, which the provider answers from the search key rather than
    /// from the source column. It is the only way to observe that the search key moved.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ByNamePrefix(
        IStorageSession session,
        StorageUnit unit,
        string prefix)
    {
        return session.Query(new QueryRequest(
            new TableId(unit.Name),
            NamePrefix(unit, prefix),
            [],
            Projection.All,
            Paging.None)).Rows;
    }

    private static Predicate NamePrefix(StorageUnit unit, string prefix)
    {
        var column = new ColumnRef(
            new TableId(unit.Name), "name", QueryType.String, isNullable: false, maxLength: 32,
            stringComparison: QueryStringComparisonPolicy.UnicodeOrdinalIgnoreCase);
        return new Predicate.StartsWith(column, prefix);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Read(
        IStorageSession session,
        StorageUnit unit) =>
        session.Query(new QueryRequest(
            new TableId(unit.Name), Status(unit, "open"), [], Projection.All, Paging.None)).Rows;

    private static StorageValues Row(string id, string status, string label) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["status"] = status,
            ["label"] = label,
            ["document"] = null,
            ["amount"] = null
        });

    private static StorageValues CompositeRow(string tenant, string id) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tenant"] = tenant,
            ["id"] = id,
            ["status"] = "open",
            ["label"] = "before",
            ["document"] = null,
            ["amount"] = null
        });

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("p43_admission"),
        Name = "p43_admission",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
            new() { Name = "status", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
            new() { Name = "label", Type = PortableType.String, IsNullable = true, MaxLength = 64 },
            new() { Name = "document", Type = PortableType.Json, IsNullable = true },
            new() { Name = "amount", Type = PortableType.Decimal, IsNullable = true, Precision = 18, Scale = 4 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition { Name = "by_status", Columns = [new IndexColumn("status")] },
            // Indexed so that the non-portable-predicate proof is refused for being non-portable
            // rather than for being uncovered.
            new IndexDefinition { Name = "by_amount", Columns = [new IndexColumn("amount")] }
        ]
    };

    private static StorageUnit UniqueUnit()
    {
        var unit = Unit();
        return unit with
        {
            Id = new StorageUnitId("p43_exact_failure"),
            Name = "p43_exact_failure",
            Indexes = unit.Indexes.Append(new IndexDefinition
            {
                Name = "unique_label",
                Columns = [new IndexColumn("label")],
                IsUnique = true
            }).ToArray()
        };
    }

    private static StorageUnit CompositeUnit()
    {
        var unit = Unit();
        return unit with
        {
            Id = new StorageUnitId("p43_composite"),
            Name = "p43_composite",
            Columns = unit.Columns.Append(new ColumnDefinition
            {
                Name = "tenant",
                Type = PortableType.String,
                IsNullable = false,
                MaxLength = 64
            }).ToArray(),
            Key = new KeyDefinition { Columns = ["tenant", "id"] }
        };
    }

    private static StorageUnit FoldedUnit() => new()
    {
        Id = new StorageUnitId("p43_folded"),
        Name = "p43_folded",
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 64 },
            new() { Name = "status", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
            new()
            {
                Name = "name",
                Type = PortableType.String,
                IsNullable = false,
                MaxLength = 32,
                Collation = PortableCollation.UnicodeOrdinalIgnoreCase
            }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes =
        [
            new IndexDefinition { Name = "by_status", Columns = [new IndexColumn("status")] },
            new IndexDefinition { Name = "by_name", Columns = [new IndexColumn("name")] }
        ]
    };

    private sealed class CancelAfterFirstUpdate(CancellationTokenSource cancellation) : IProviderCommandObserver
    {
        private int updates;

        public void Observe(ProviderCommandEvent command)
        {
            if (command.Operation == "sqlite.update" && Interlocked.Increment(ref updates) == 1)
                cancellation.Cancel();
        }
    }
}
