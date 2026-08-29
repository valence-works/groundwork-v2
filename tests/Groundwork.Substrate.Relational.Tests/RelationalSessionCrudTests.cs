using Groundwork.Kernel;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Substrate.Relational.Tests;

public sealed class RelationalSessionCrudTests
{
    [Fact]
    public void Preparation_validates_written_values_before_provider_dispatch()
    {
        var adapter = new FakeCrudAdapter();
        var crud = Create(Unit(), adapter);

        Assert.Throws<ArgumentException>(() => crud.PrepareMutation(
            new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = "one",
                ["value"] = "value",
                ["metric"] = double.NaN
            }),
            options: null,
            RelationalCrudKind.Insert));

        Assert.Empty(adapter.Calls);
    }

    [Theory]
    [InlineData((int)RelationalCrudKind.Insert, WriteOutcomeStatus.Inserted)]
    [InlineData((int)RelationalCrudKind.Upsert, WriteOutcomeStatus.Upserted)]
    public async Task Missing_generated_locator_inserts_without_a_point_read(
        int kindValue,
        WriteOutcomeStatus expectedStatus)
    {
        var kind = (RelationalCrudKind)kindValue;
        var adapter = new FakeCrudAdapter();
        var reads = new FakePointReads();
        var unit = SequenceUnit();
        var crud = Create(unit, adapter, reads);
        var operation = crud.PrepareMutation(SequenceValues(), options: null, kind);

        var outcome = await crud.Mutate(operation, RelationalExecution.Synchronous);

        Assert.Equal(expectedStatus, outcome.Status);
        Assert.Equal(0, reads.Calls);
        Assert.Equal(["insert"], adapter.Calls);
        Assert.Equal(expectedStatus, adapter.InsertStatus);
    }

    [Fact]
    public async Task Non_concurrent_update_bypasses_the_point_read()
    {
        var adapter = new FakeCrudAdapter();
        var reads = new FakePointReads();
        var crud = Create(Unit(), adapter, reads);
        var operation = crud.PrepareMutation(Values(), options: null, RelationalCrudKind.Update);

        var outcome = await crud.Mutate(operation, RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.Updated, outcome.Status);
        Assert.Equal(0, reads.Calls);
        Assert.Equal(["update"], adapter.Calls);
    }

    [Fact]
    public async Task Optimistic_insert_and_missing_update_are_classified_before_dispatch()
    {
        var existingAdapter = new FakeCrudAdapter();
        var existingReads = new FakePointReads { Result = Entry(version: 4) };
        var existingCrud = Create(OptimisticUnit(), existingAdapter, existingReads);

        var insert = await existingCrud.Mutate(
            existingCrud.PrepareMutation(Values(), null, RelationalCrudKind.Insert),
            RelationalExecution.Synchronous);

        var missingAdapter = new FakeCrudAdapter();
        var missingCrud = Create(OptimisticUnit(), missingAdapter, new FakePointReads());
        var update = await missingCrud.Mutate(
            missingCrud.PrepareMutation(Values(), null, RelationalCrudKind.Update),
            RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.UniqueViolation, insert.Status);
        Assert.Equal(4, insert.Version);
        Assert.Equal(WriteOutcomeStatus.NotFound, update.Status);
        Assert.Empty(existingAdapter.Calls);
        Assert.Empty(missingAdapter.Calls);
    }

    [Fact]
    public async Task Optimistic_sequence_upsert_with_a_supplied_missing_locator_is_not_an_insert()
    {
        var adapter = new FakeCrudAdapter();
        var crud = Create(OptimisticSequenceUnit(), adapter, new FakePointReads());
        var operation = crud.PrepareMutation(
            new StorageValues(new Dictionary<string, object?> { ["sequence"] = 8L, ["value"] = "value" }),
            options: null,
            RelationalCrudKind.Upsert);

        var outcome = await crud.Mutate(operation, RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.NotFound, outcome.Status);
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task Version_preconditions_are_checked_before_provider_dispatch()
    {
        var adapter = new FakeCrudAdapter();
        var crud = Create(OptimisticUnit(), adapter, new FakePointReads { Result = Entry(version: 3) });
        var operation = crud.PrepareMutation(
            Values(),
            WriteOptions.IfVersion(2),
            RelationalCrudKind.Update);

        var failure = await Assert.ThrowsAsync<RelationalConcurrencyConflictException>(async () =>
            await crud.Mutate(operation, RelationalExecution.Synchronous));

        Assert.Equal(3, failure.Version);
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task Create_only_upsert_refuses_an_existing_row_before_dispatch()
    {
        var adapter = new FakeCrudAdapter();
        var crud = Create(OptimisticUnit(), adapter, new FakePointReads { Result = Entry(version: 5) });
        var operation = crud.PrepareMutation(
            Values(),
            WriteOptions.CreateOnly,
            RelationalCrudKind.Upsert);

        var failure = await Assert.ThrowsAsync<RelationalConcurrencyConflictException>(async () =>
            await crud.Mutate(operation, RelationalExecution.Synchronous));

        Assert.Equal(5, failure.Version);
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public async Task Scoped_physical_key_is_removed_from_the_logical_provider_key()
    {
        var adapter = new FakeCrudAdapter();
        var crud = Create(ScopedUnit(), adapter);
        var operation = crud.PrepareMutation(Values(), null, RelationalCrudKind.Update);

        _ = await crud.Mutate(operation, RelationalExecution.Synchronous);

        Assert.Equal(["id"], adapter.Key!.Values.Keys);
    }

    [Fact]
    public async Task Delete_uses_the_same_concurrency_classification_and_preconditions()
    {
        var key = new StorageKey(new Dictionary<string, object?> { ["id"] = "one" });
        var missingAdapter = new FakeCrudAdapter();
        var missingCrud = Create(OptimisticUnit(), missingAdapter, new FakePointReads());

        var missing = await missingCrud.Delete(
            missingCrud.PrepareDelete(key, options: null),
            RelationalExecution.Synchronous);

        var existingAdapter = new FakeCrudAdapter();
        var existingCrud = Create(OptimisticUnit(), existingAdapter, new FakePointReads { Result = Entry(9) });
        var deleted = await existingCrud.Delete(
            existingCrud.PrepareDelete(key, WriteOptions.IfVersion(9)),
            RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.NotFound, missing.Status);
        Assert.Empty(missingAdapter.Calls);
        Assert.Equal(WriteOutcomeStatus.Deleted, deleted.Status);
        Assert.Equal(["delete"], existingAdapter.Calls);
    }

    private static RelationalSessionCrud Create(
        StorageUnit unit,
        FakeCrudAdapter adapter,
        FakePointReads? reads = null)
    {
        var userColumns = unit.Columns
            .Where(column => column.Name is not ProviderOwnedColumns.Scope and not "__groundwork_version")
            .ToArray();
        return new RelationalSessionCrud(
            unit,
            userColumns,
            userColumns.FirstOrDefault(column => column.Generation == ColumnGeneration.ProviderSequence),
            unit.Columns.FirstOrDefault(column => column.Name == "__groundwork_version"),
            "StubDB",
            (reads ?? new FakePointReads()).Read,
            adapter);
    }

    private static StorageValues Values() => new(new Dictionary<string, object?>
    {
        ["id"] = "one",
        ["value"] = "value"
    });

    private static StorageValues SequenceValues() => new(new Dictionary<string, object?>
    {
        ["value"] = "value"
    });

    private static StoredEntry Entry(long version) => new(Values(), version);

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("crud-unit"),
        Name = "crud_unit",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "value", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "metric", Type = PortableType.Double, IsNullable = true }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit OptimisticUnit() => Unit() with
    {
        Columns =
        [
            .. Unit().Columns,
            new ColumnDefinition { Name = "__groundwork_version", Type = PortableType.Int64, IsNullable = false }
        ],
        Concurrency = ConcurrencyDeclaration.Optimistic("__groundwork_version")
    };

    private static StorageUnit SequenceUnit() => new()
    {
        Id = new StorageUnitId("crud-sequence"),
        Name = "crud_sequence",
        Columns =
        [
            new ColumnDefinition
            {
                Name = "sequence",
                Type = PortableType.Int64,
                IsNullable = false,
                Generation = ColumnGeneration.ProviderSequence
            },
            new ColumnDefinition { Name = "value", Type = PortableType.String, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] }
    };

    private static StorageUnit OptimisticSequenceUnit() => SequenceUnit() with
    {
        Columns =
        [
            .. SequenceUnit().Columns,
            new ColumnDefinition { Name = "__groundwork_version", Type = PortableType.Int64, IsNullable = false }
        ],
        Concurrency = ConcurrencyDeclaration.Optimistic("__groundwork_version")
    };

    private static StorageUnit ScopedUnit() => Unit() with
    {
        Columns =
        [
            .. Unit().Columns,
            new ColumnDefinition { Name = ProviderOwnedColumns.Scope, Type = PortableType.String, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = [ProviderOwnedColumns.Scope, "id"] },
        Scope = ScopePolicy.Scoped
    };

    private sealed class FakePointReads
    {
        internal int Calls { get; private set; }
        internal StoredEntry? Result { get; init; }

        internal ValueTask<StoredEntry?> Read(StorageKey key, RelationalExecution execution)
        {
            Calls++;
            return ValueTask.FromResult(Result);
        }
    }

    private sealed class FakeCrudAdapter : IRelationalCrudAdapter
    {
        internal List<string> Calls { get; } = [];
        internal WriteOutcomeStatus? InsertStatus { get; private set; }
        internal StorageKey? Key { get; private set; }

        public ValueTask<WriteOutcome> Insert(
            StorageValues values,
            WriteOutcomeStatus status,
            RelationalExecution execution)
        {
            Calls.Add("insert");
            InsertStatus = status;
            return ValueTask.FromResult(new WriteOutcome(status));
        }

        public ValueTask<WriteOutcome> Update(
            StorageValues values,
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution)
        {
            Calls.Add("update");
            Key = key;
            return ValueTask.FromResult(new WriteOutcome(WriteOutcomeStatus.Updated, existing?.Version));
        }

        public ValueTask<WriteOutcome> Upsert(
            StorageValues values,
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution)
        {
            Calls.Add("upsert");
            Key = key;
            return ValueTask.FromResult(new WriteOutcome(WriteOutcomeStatus.Upserted, existing?.Version));
        }

        public ValueTask<WriteOutcome> Delete(
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution)
        {
            Calls.Add("delete");
            Key = key;
            return ValueTask.FromResult(new WriteOutcome(WriteOutcomeStatus.Deleted, existing?.Version));
        }
    }
}
