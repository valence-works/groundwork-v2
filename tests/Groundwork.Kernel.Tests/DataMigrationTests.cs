using System.Collections.Immutable;
using System.Reflection;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Xunit;

namespace Groundwork.Kernel.Tests;

public sealed class DataMigrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly PhysicalSchemaTargetIdentity Target =
        new(new StorageUnitId("orders"), "test-provider");

    private static StorageUnit Unit => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 64 },
            new ColumnDefinition { Name = "slug", Type = PortableType.String, MaxLength = 64 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static DataMigration Migration(IDataMigrationTransform? transform = null) =>
        new("2026-08-slugify", new StorageUnitId("orders"), transform ?? new SlugTransform());

    // ---------------------------------------------------------------- ledger invariants

    [Fact]
    public void A_completed_ledger_entry_cannot_carry_a_resume_cursor()
    {
        var refusal = Assert.Throws<DataMigrationRefusedException>(() => new DataMigrationLedgerEntry(
            Target, "2026-08-slugify", "orders", "fingerprint",
            DataMigrationRunState.Completed, cursor: "3:sab;", 3, 3, 1, Now, Now, Now));

        Assert.Equal("GW-MIGRATION-005", refusal.Code);
        Assert.Equal(
            "GW-MIGRATION-005: the ledger entry for data migration '2026-08-slugify' is marked completed " +
            "while still carrying a resume cursor.",
            refusal.Message);
    }

    [Fact]
    public void A_completed_ledger_entry_cannot_omit_its_completion_instant()
    {
        var refusal = Assert.Throws<DataMigrationRefusedException>(() => new DataMigrationLedgerEntry(
            Target, "2026-08-slugify", "orders", "fingerprint",
            DataMigrationRunState.Completed, cursor: null, 3, 3, 1, Now, Now, completedAt: null));

        Assert.Equal("GW-MIGRATION-005", refusal.Code);
        Assert.Contains("is marked completed without a completion instant.", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_ledger_entry_cannot_carry_a_completion_instant()
    {
        var refusal = Assert.Throws<DataMigrationRefusedException>(() => new DataMigrationLedgerEntry(
            Target, "2026-08-slugify", "orders", "fingerprint",
            DataMigrationRunState.Running, cursor: "3:sab;", 3, 3, 1, Now, Now, completedAt: Now));

        Assert.Equal("GW-MIGRATION-005", refusal.Code);
        Assert.Contains("is marked running while carrying a completion instant.", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Completion_cannot_be_fabricated_without_exhaustion_evidence()
    {
        var entry = DataMigrationLedgerEntry.Start(Target, Migration(), Unit, Now);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => entry.Complete(default, Now));

        Assert.Equal("GW-MIGRATION-007", refusal.Code);
        Assert.Equal(
            "GW-MIGRATION-007: data migration '2026-08-slugify' cannot be completed without evidence " +
            "that its source was exhausted.",
            refusal.Message);
    }

    [Fact]
    public void Exhaustion_evidence_belongs_to_one_migration_and_cannot_be_constructed_by_a_provider()
    {
        var mine = DataMigrationLedgerEntry.Start(Target, Migration(), Unit, Now);
        var other = DataMigrationLedgerEntry.Start(
            Target, new DataMigration("other", new StorageUnitId("orders"), new SlugTransform()), Unit, Now);
        var foreignEvidence = DataMigrationChunkOutcome.Exhausted(other);

        var refusal = Assert.Throws<DataMigrationRefusedException>(
            () => mine.Complete(Evidence(foreignEvidence), Now));
        Assert.Equal(
            "GW-MIGRATION-007: data migration '2026-08-slugify' cannot be completed with exhaustion " +
            "evidence from 'other'.",
            refusal.Message);

        // The only way to obtain evidence is a chunk the provider reported exhausted: the type has
        // no public constructor, so a provider assembly cannot manufacture one.
        Assert.Empty(typeof(DataMigrationExhaustion).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void An_advanced_entry_stays_running_and_completion_clears_the_cursor()
    {
        var entry = DataMigrationLedgerEntry.Start(Target, Migration(), Unit, Now)
            .Advance(DataMigrationCursor.After(Unit, Row(7)), 4, 3, Now.AddSeconds(1));

        Assert.Equal(DataMigrationRunState.Running, entry.State);
        Assert.Equal("2:i7;", entry.Cursor);
        Assert.Equal(4, entry.RowsScanned);
        Assert.Equal(3, entry.RowsChanged);
        Assert.Equal(1, entry.Batches);
        Assert.Null(entry.CompletedAt);

        var completed = entry.Complete(Evidence(DataMigrationChunkOutcome.Exhausted(entry)), Now.AddSeconds(2));

        Assert.Equal(DataMigrationRunState.Completed, completed.State);
        Assert.Null(completed.Cursor);
        Assert.Equal(Now.AddSeconds(2), completed.CompletedAt);
        Assert.Equal(4, completed.RowsScanned);
    }

    // ---------------------------------------------------------------- cursor

    [Fact]
    public void A_cursor_encodes_and_decodes_its_key_values()
    {
        var cursor = DataMigrationCursor.After(Unit, Row(42));

        Assert.Equal("3:i42;", cursor.Canonical);
        Assert.True(DataMigrationCursor.TryDecode(Unit, cursor.Canonical, out var decoded));
        Assert.Equal(42, Assert.Single(decoded.Values));
    }

    [Fact]
    public void A_composite_cursor_encodes_every_key_column_in_declared_order()
    {
        var unit = Unit with
        {
            Columns =
            [
                new ColumnDefinition { Name = "tenant", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "name", Type = PortableType.String },
                new ColumnDefinition { Name = "slug", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["tenant", "id"] }
        };

        var cursor = DataMigrationCursor.After(unit, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tenant"] = "acme",
            ["id"] = 9
        });

        Assert.Equal("5:sacme;2:i9;", cursor.Canonical);
        Assert.True(DataMigrationCursor.TryDecode(unit, cursor.Canonical, out var decoded));
        Assert.Equal(new object?[] { "acme", 9 }, decoded.Values.ToArray());
        // A cursor written for a different key shape does not decode, so a resume cannot silently
        // read it as a position in this unit.
        Assert.False(DataMigrationCursor.TryDecode(Unit, cursor.Canonical, out _));
    }

    [Fact]
    public void A_cursor_refuses_a_null_key_value()
    {
        var refusal = Assert.Throws<DataMigrationRefusedException>(() => DataMigrationCursor.After(
            Unit, new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = null }));

        Assert.Equal("GW-MIGRATION-004", refusal.Code);
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void A_migration_projects_the_key_first_then_its_declared_sources()
    {
        Assert.Equal(new[] { "id", "name" }, Migration().ValidateAgainst(Unit).ToArray());
    }

    [Fact]
    public void A_migration_refuses_to_write_the_key_it_resumes_on()
    {
        var migration = new DataMigration("m", new StorageUnitId("orders"), new KeyWritingTransform());

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => migration.ValidateAgainst(Unit));

        Assert.Equal("GW-MIGRATION-004", refusal.Code);
        Assert.Equal(
            "GW-MIGRATION-004: migration 'm' writes key column 'id' of 'orders'; " +
            "a data migration cannot move the key it resumes on.",
            refusal.Message);
    }

    [Fact]
    public void A_transform_that_produces_an_undeclared_column_is_refused()
    {
        var migration = new DataMigration("m", new StorageUnitId("orders"), new StrayTransform());
        var request = new DataMigrationChunkRequest(
            migration, Unit, DataMigrationLedgerEntry.Start(Target, migration, Unit, Now), null, ["id", "name"], 10);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => request.Apply(Row(1)));

        Assert.Equal("GW-MIGRATION-006", refusal.Code);
        Assert.Equal(
            "GW-MIGRATION-006: data migration 'm' produced column 'name', which its transform does not " +
            "declare as a target.",
            refusal.Message);
    }

    [Fact]
    public void A_transform_cannot_produce_null_for_a_required_target()
    {
        var unit = Unit with
        {
            Columns = Unit.Columns.Select(column =>
                column.Name == "slug" ? column with { IsNullable = false } : column).ToArray()
        };
        var migration = new DataMigration("m", unit.Id, new ConstantTransform("slug", null));
        var request = new DataMigrationChunkRequest(
            migration, unit, DataMigrationLedgerEntry.Start(Target, migration, unit, Now), null, ["id"], 10);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => request.Apply(Row(1)));

        Assert.Equal(DataMigrationCodes.NotApplicable, refusal.Code);
        Assert.Contains("produced null for non-nullable target column 'slug'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transform_cannot_produce_a_value_outside_the_targets_portable_type()
    {
        var migration = new DataMigration("m", Unit.Id, new ConstantTransform("slug", 42));
        var request = new DataMigrationChunkRequest(
            migration, Unit, DataMigrationLedgerEntry.Start(Target, migration, Unit, Now), null, ["id"], 10);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => request.Apply(Row(1)));

        Assert.Equal(DataMigrationCodes.NotApplicable, refusal.Code);
        Assert.Contains(
            "produced CLR type 'Int32' for target column 'slug', which declares portable type 'String'",
            refusal.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_transform_cannot_produce_text_longer_than_the_target_allows()
    {
        var migration = new DataMigration("m", Unit.Id, new ConstantTransform("slug", new string('x', 65)));
        var request = new DataMigrationChunkRequest(
            migration, Unit, DataMigrationLedgerEntry.Start(Target, migration, Unit, Now), null, ["id"], 10);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => request.Apply(Row(1)));

        Assert.Equal(DataMigrationCodes.NotApplicable, refusal.Code);
        Assert.Contains("target column 'slug' exceeds its declared length of 64", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transform_cannot_produce_binary_longer_than_the_target_allows()
    {
        var unit = Unit with
        {
            Columns =
            [
                .. Unit.Columns,
                new ColumnDefinition { Name = "payload", Type = PortableType.Binary, MaxLength = 4 }
            ]
        };
        var migration = new DataMigration("m", unit.Id, new ConstantTransform("payload", new byte[5]));
        var request = new DataMigrationChunkRequest(
            migration, unit, DataMigrationLedgerEntry.Start(Target, migration, unit, Now), null, ["id"], 10);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => request.Apply(Row(1)));

        Assert.Equal(DataMigrationCodes.NotApplicable, refusal.Code);
        Assert.Contains("target column 'payload' exceeds its declared length of 4", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transform_cannot_produce_a_decimal_outside_the_targets_precision()
    {
        var unit = Unit with
        {
            Columns =
            [
                .. Unit.Columns,
                new ColumnDefinition
                {
                    Name = "amount", Type = PortableType.Decimal, Precision = 5, Scale = 2
                }
            ]
        };
        var migration = new DataMigration("m", unit.Id, new ConstantTransform("amount", 1234.56m));
        var request = new DataMigrationChunkRequest(
            migration, unit, DataMigrationLedgerEntry.Start(Target, migration, unit, Now), null, ["id"], 10);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => request.Apply(Row(1)));

        Assert.Equal(DataMigrationCodes.NotApplicable, refusal.Code);
        Assert.Contains(
            "target column 'amount' does not fit its declared decimal(5,2)",
            refusal.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Decimal_output_must_fit_scale_without_rounding_but_ignores_insignificant_zeroes()
    {
        var unit = Unit with
        {
            Columns =
            [
                .. Unit.Columns,
                new ColumnDefinition
                {
                    Name = "amount", Type = PortableType.Decimal, Precision = 5, Scale = 2
                }
            ]
        };

        DataMigrationChunkRequest Request(decimal value)
        {
            var migration = new DataMigration("m", unit.Id, new ConstantTransform("amount", value));
            return new DataMigrationChunkRequest(
                migration, unit, DataMigrationLedgerEntry.Start(Target, migration, unit, Now), null, ["id"], 10);
        }

        Assert.Equal(1.2300m, Assert.Single(Request(1.2300m).Apply(Row(1))!).Value);
        var refusal = Assert.Throws<DataMigrationRefusedException>(() => Request(1.234m).Apply(Row(1)));
        Assert.Contains("does not fit its declared decimal(5,2)", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-0.0d)]
    public void Double_output_must_be_in_the_portable_storage_domain(double value)
    {
        var unit = Unit with
        {
            Columns =
            [
                .. Unit.Columns,
                new ColumnDefinition { Name = "measurement", Type = PortableType.Double }
            ]
        };
        var migration = new DataMigration("m", unit.Id, new ConstantTransform("measurement", value));
        var request = new DataMigrationChunkRequest(
            migration, unit, DataMigrationLedgerEntry.Start(Target, migration, unit, Now), null, ["id"], 10);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => request.Apply(Row(1)));

        Assert.Equal(DataMigrationCodes.NotApplicable, refusal.Code);
        Assert.Contains("portable Double storage domain", refusal.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- runner

    [Fact]
    public void A_provider_missing_a_capability_is_refused_by_name()
    {
        var executor = new FakeExecutor(Unit) { Capabilities = DataMigrationCapabilities.KeysetScan };

        var refusal = Assert.Throws<DataMigrationRefusedException>(() =>
            DataMigrationRunner.Run(executor, Target, Unit, Migration()));

        Assert.Equal("GW-MIGRATION-001", refusal.Code);
        Assert.Equal(
            "GW-MIGRATION-001: this provider does not advertise data-migration capability " +
            "AtomicChunkProgress, AppliedLedger, ExclusiveRunLease; it cannot move data under the facility's " +
            "interruption guarantees.",
            refusal.Message);
        Assert.Empty(executor.Ledger);
    }

    [Fact]
    public void A_budgeted_pass_stops_with_a_resume_cursor_and_the_next_pass_finishes_it()
    {
        var executor = new FakeExecutor(Unit, Seed(5));

        var first = DataMigrationRunner.Run(
            executor, Target, Unit, Migration(), new DataMigrationBudget { MaxRowsPerBatch = 2, MaxBatches = 1 }, Now);

        Assert.Equal(DataMigrationStatus.Interrupted, first.Status);
        Assert.False(first.IsComplete);
        Assert.Equal(2, first.RowsScanned);
        Assert.Equal(1, first.Batches);
        Assert.Equal("2:i2;", first.ResumeCursor);
        var interrupted = Assert.Single(executor.Ledger.Values);
        Assert.Equal(DataMigrationRunState.Running, interrupted.State);
        Assert.Null(interrupted.CompletedAt);
        Assert.Equal(["a-slug", "b-slug", null, null, null], executor.Rows.Select(row => row["slug"]));

        var second = DataMigrationRunner.Run(
            executor, Target, Unit, Migration(), new DataMigrationBudget { MaxRowsPerBatch = 2 }, Now);

        Assert.Equal(DataMigrationStatus.Completed, second.Status);
        Assert.Equal(5, second.RowsScanned);
        Assert.Equal(5, second.RowsChanged);
        Assert.Equal(3, second.Batches);
        Assert.Null(second.ResumeCursor);
        Assert.Equal(["a-slug", "b-slug", "c-slug", "d-slug", "e-slug"], executor.Rows.Select(row => row["slug"]));
        Assert.Equal(DataMigrationRunState.Completed, Assert.Single(executor.Ledger.Values).State);
    }

    [Fact]
    public void An_exactly_full_final_chunk_is_finished_by_a_further_empty_chunk_not_by_the_cursor()
    {
        // Four rows and a batch of two: the second chunk fills exactly, so nothing about the cursor
        // says the source ran out. Completion is only recorded once a chunk observes it.
        var executor = new FakeExecutor(Unit, Seed(4));

        var result = DataMigrationRunner.Run(
            executor, Target, Unit, Migration(), new DataMigrationBudget { MaxRowsPerBatch = 2 }, Now);

        Assert.Equal(DataMigrationStatus.Completed, result.Status);
        Assert.Equal(4, result.RowsScanned);
        Assert.Equal(3, executor.ChunkCalls);
        Assert.Equal(2, result.Batches);
    }

    [Fact]
    public void A_pass_interrupted_at_its_last_chunk_is_not_reported_as_finished()
    {
        var executor = new FakeExecutor(Unit, Seed(4));

        var stopped = DataMigrationRunner.Run(
            executor, Target, Unit, Migration(), new DataMigrationBudget { MaxRowsPerBatch = 2, MaxBatches = 2 }, Now);

        // Every row is migrated, yet the pass is not complete: no chunk ever saw the source
        // exhausted, so the ledger still records a running migration with a resume position.
        Assert.Equal(DataMigrationStatus.Interrupted, stopped.Status);
        Assert.Equal(4, stopped.RowsScanned);
        Assert.Equal("2:i4;", stopped.ResumeCursor);
        var entry = Assert.Single(executor.Ledger.Values);
        Assert.Equal(DataMigrationRunState.Running, entry.State);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => stopped.EnsureComplete());
        Assert.Equal("GW-MIGRATION-007", refusal.Code);

        Assert.Equal(DataMigrationStatus.Completed,
            DataMigrationRunner.Run(executor, Target, Unit, Migration(), null, Now).Status);
    }

    [Fact]
    public void A_replay_of_a_completed_migration_touches_no_row()
    {
        var executor = new FakeExecutor(Unit, Seed(3));
        Assert.Equal(DataMigrationStatus.Completed,
            DataMigrationRunner.Run(executor, Target, Unit, Migration(), null, Now).Status);
        var chunksAfterFirstPass = executor.ChunkCalls;

        var replay = DataMigrationRunner.Run(executor, Target, Unit, Migration(), null, Now);

        Assert.Equal(DataMigrationStatus.Replayed, replay.Status);
        Assert.True(replay.IsComplete);
        Assert.Equal(3, replay.RowsScanned);
        Assert.Equal(chunksAfterFirstPass, executor.ChunkCalls);
    }

    [Fact]
    public async Task Concurrent_direct_runs_share_one_exclusive_target_lease()
    {
        var executor = new FakeExecutor(Unit, Seed(1));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var transform = new BlockingSlugTransform(entered, release);
        var migration = new DataMigration("concurrent", Unit.Id, transform);

        var first = Task.Run(() => DataMigrationRunner.Run(executor, Target, Unit, migration, null, Now));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var second = Task.Run(() => DataMigrationRunner.Run(executor, Target, Unit, migration, null, Now));

        await Task.Delay(100);
        Assert.Equal(1, transform.Calls);
        Assert.False(second.IsCompleted);

        release.Set();
        var outcomes = await Task.WhenAll(first, second);
        Assert.Contains(outcomes, outcome => outcome.Status == DataMigrationStatus.Completed);
        Assert.Contains(outcomes, outcome => outcome.Status == DataMigrationStatus.Replayed);
        Assert.Equal(1, transform.Calls);
    }

    [Fact]
    public void A_completed_migration_replays_after_contract_retires_its_source_column()
    {
        var executor = new FakeExecutor(Unit, Seed(3));
        Assert.Equal(DataMigrationStatus.Completed,
            DataMigrationRunner.Run(executor, Target, Unit, Migration(), null, Now).Status);
        var contracted = Unit with
        {
            Columns = Unit.Columns.Where(column => column.Name != "name").ToArray()
        };
        var chunksAfterFirstPass = executor.ChunkCalls;

        var replay = DataMigrationRunner.Run(executor, Target, contracted, Migration(), null, Now);

        Assert.Equal(DataMigrationStatus.Replayed, replay.Status);
        Assert.Equal(chunksAfterFirstPass, executor.ChunkCalls);
    }

    [Fact]
    public void Reusing_a_migration_identity_for_a_changed_transform_is_refused()
    {
        var executor = new FakeExecutor(Unit, Seed(2));
        DataMigrationRunner.Run(executor, Target, Unit, Migration(), null, Now);

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => DataMigrationRunner.Run(
            executor, Target, Unit, Migration(new UpperSlugTransform()), null, Now));

        Assert.Equal("GW-MIGRATION-002", refusal.Code);
        Assert.Contains("Use a new semantic migration identity for a changed transform.", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transform_version_changes_the_request_fingerprint_even_when_its_identity_is_unchanged()
    {
        Assert.NotEqual(
            Migration(new SlugTransform()).RequestFingerprint(Unit),
            Migration(new UpperSlugTransform()).RequestFingerprint(Unit));
    }

    [Fact]
    public void Transform_fingerprints_are_delimiter_safe_and_require_an_explicit_version()
    {
        Assert.NotEqual(
            DataMigrationTransformFingerprint.Create("a:b", "c"),
            DataMigrationTransformFingerprint.Create("a", "b:c"));
        Assert.Throws<ArgumentException>(() => DataMigrationTransformFingerprint.Create("slug", " "));
        Assert.Throws<ArgumentException>(() => DataMigrationTransformFingerprint.Create("slug\ud800", "v1"));
        Assert.Throws<ArgumentException>(() => DataMigrationTransformFingerprint.Create("slug", "v1\udc00"));
    }

    [Fact]
    public void A_provider_that_advances_without_scanning_is_refused_rather_than_looping()
    {
        // The spin starts after one genuine chunk, so the entry already carries a resume cursor.
        // That matters: on the very first chunk the cursor is still null, and the neighbouring
        // "advanced without recording a resume cursor" guard would catch it — this test would pass
        // while proving nothing about the loop. With a cursor in hand, the scanned-nothing check is
        // the only thing standing between the runner and an endless run of chunks claiming
        // progress, which is what the name claims it proves.
        var executor = new FakeExecutor(Unit, Seed(6)) { SpinAfterChunks = 1 };

        var refusal = Assert.Throws<DataMigrationRefusedException>(() => DataMigrationRunner.Run(
            executor, Target, Unit, Migration(), new DataMigrationBudget { MaxRowsPerBatch = 2 }, Now));

        Assert.Equal("GW-MIGRATION-005", refusal.Code);
        Assert.Contains("without scanning a row and without reporting its source exhausted", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(2, executor.ChunkCalls);
    }

    [Fact]
    public void A_provider_that_advances_without_a_resume_cursor_is_refused()
    {
        // Unreachable through Advance, which always records a cursor — so this is the backstop for
        // a provider that builds a ledger entry itself. It has to report a scanned row, or the
        // scanned-nothing check above would catch it first and this would prove that guard twice.
        var executor = new FakeExecutor(Unit, Seed(2)) { AdvanceWithoutCursor = true };

        var refusal = Assert.Throws<DataMigrationRefusedException>(() =>
            DataMigrationRunner.Run(executor, Target, Unit, Migration(), null, Now));

        Assert.Equal("GW-MIGRATION-005", refusal.Code);
        Assert.Contains("without recording a resume cursor", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_surfaces_run_the_same_migration_to_the_same_evidence()
    {
        var synchronous = new FakeExecutor(Unit, Seed(5));
        var asynchronous = new FakeExecutor(Unit, Seed(5));
        var budget = new DataMigrationBudget { MaxRowsPerBatch = 2 };

        var fromSync = DataMigrationRunner.Run(synchronous, Target, Unit, Migration(), budget, Now);
        var fromAsync = await DataMigrationRunner.RunAsync(asynchronous, Target, Unit, Migration(), budget, Now);

        Assert.Equal(fromSync, fromAsync);
        Assert.Equal(synchronous.Rows.Select(row => row["slug"]), asynchronous.Rows.Select(row => row["slug"]));
        Assert.True(asynchronous.SawAsync);
        Assert.False(synchronous.SawAsync);
    }

    [Fact]
    public async Task An_already_cancelled_pass_moves_no_row()
    {
        var executor = new FakeExecutor(Unit, Seed(3));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await DataMigrationRunner.RunAsync(
            executor, Target, Unit, Migration(), null, Now, null, cancellation.Token));

        Assert.Equal(0, executor.ChunkCalls);
        Assert.Empty(executor.Ledger);
        Assert.All(executor.Rows, row => Assert.Null(row["slug"]));
    }

    [Fact]
    public void Progress_is_reported_once_per_committed_chunk()
    {
        var executor = new FakeExecutor(Unit, Seed(5));
        var reported = new List<DataMigrationProgress>();

        DataMigrationRunner.Run(
            executor, Target, Unit, Migration(),
            new DataMigrationBudget { MaxRowsPerBatch = 2 }, Now,
            new SynchronousProgress(reported.Add));

        Assert.Equal([2L, 4L, 5L], reported.Select(entry => entry.RowsScanned));
        Assert.Equal(["2:i2;", "2:i4;", "2:i5;"], reported.Select(entry => entry.Cursor));
    }

    // ---------------------------------------------------------------- derived-column transform

    [Fact]
    public void The_derived_column_transform_produces_the_portable_search_key()
    {
        var unit = Unit with
        {
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition
                {
                    Name = "name",
                    Type = PortableType.String,
                    MaxLength = 64,
                    LogicalCollation = PortableCollation.OrdinalIgnoreCase
                },
                new ColumnDefinition { Name = "__groundwork_search_name", Type = PortableType.String, MaxLength = 320 }
            ]
        };
        var derived = new DerivedColumnDefinition
        {
            Name = "__groundwork_search_name",
            SourceColumn = "name",
            Projection = PortableProjection.BoundarySearchKey,
            AlgorithmId = SearchKeyProjection.AlgorithmId(PortableCollation.OrdinalIgnoreCase)
        };
        var transform = new DerivedColumnTransform(unit, [derived]);

        var produced = transform.Transform(new DataMigrationRow(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "Ada"
        }));

        Assert.Equal(new[] { "name" }, transform.SourceColumns.ToArray());
        Assert.Equal(new[] { "__groundwork_search_name" }, transform.TargetColumns.ToArray());
        Assert.Equal(
            PortableStringComparison.CreateSearchKey("Ada", PortableStringComparisonPolicy.AsciiIgnoreCase),
            Assert.Single(produced.Values!).Value);
    }

    [Fact]
    public void The_derived_column_transform_uses_the_same_locale_sort_key_as_writes()
    {
        var logical = Unit with
        {
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition
                {
                    Name = "name",
                    Type = PortableType.String,
                    MaxLength = 64,
                    LocaleSortKey = new LocaleSortKeyDefinition
                    {
                        CultureName = "sv-SE",
                        MaximumExpansionFactor = 12
                    }
                }
            ]
        };
        var physical = SearchKeyProjection.Expand(logical);
        var derived = Assert.Single(physical.DerivedColumns,
            column => column.Projection == PortableProjection.LocaleSortKey);
        var transform = new DerivedColumnTransform(physical, [derived]);

        var produced = transform.Transform(new DataMigrationRow(
            new Dictionary<string, object?> { ["name"] = "Åke" }));
        var written = SearchKeyProjection.Populate(
            physical,
            new Dictionary<string, object?> { ["name"] = "Åke" });

        Assert.Equal(written[derived.Name], Assert.Single(produced.Values!).Value);
        Assert.IsType<string>(written[derived.Name]);
    }

    [Fact]
    public void The_derived_column_transform_identity_changes_with_its_algorithm()
    {
        var unit = Unit with
        {
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 64 },
                new ColumnDefinition { Name = "__groundwork_search_name", Type = PortableType.String, MaxLength = 320 }
            ]
        };
        DerivedColumnDefinition Derived(PortableCollation collation) => new()
        {
            Name = "__groundwork_search_name",
            SourceColumn = "name",
            Projection = PortableProjection.BoundarySearchKey,
            AlgorithmId = SearchKeyProjection.AlgorithmId(collation)
        };

        Assert.NotEqual(
            new DerivedColumnTransform(unit, [Derived(PortableCollation.OrdinalIgnoreCase)]).Version,
            new DerivedColumnTransform(unit, [Derived(PortableCollation.UnicodeOrdinalIgnoreCase)]).Version);
    }

    // ---------------------------------------------------------------- helpers

    private static DataMigrationExhaustion Evidence(DataMigrationChunkOutcome outcome) =>
        (DataMigrationExhaustion)typeof(DataMigrationChunkOutcome)
            .GetProperty("Evidence", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(outcome)!;

    private static Dictionary<string, object?> Row(int id) => new(StringComparer.Ordinal)
    {
        ["id"] = id,
        ["name"] = ((char)('a' + id - 1)).ToString(),
        ["slug"] = null
    };

    private static List<Dictionary<string, object?>> Seed(int count) =>
        Enumerable.Range(1, count).Select(Row).ToList();

    private sealed class SynchronousProgress(Action<DataMigrationProgress> report) : IProgress<DataMigrationProgress>
    {
        public void Report(DataMigrationProgress value) => report(value);
    }

    private sealed class SlugTransform : IDataMigrationTransform
    {
        public string Identity => "slug";
        public string Version => "v1";
        public ImmutableArray<string> SourceColumns => ["name"];
        public ImmutableArray<string> TargetColumns => ["slug"];
        public DataMigrationValues Transform(DataMigrationRow row) =>
            DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = row["name"] is string name ? name + "-slug" : null
            });
    }

    private sealed class UpperSlugTransform : IDataMigrationTransform
    {
        public string Identity => "slug";
        public string Version => "v2";
        public ImmutableArray<string> SourceColumns => ["name"];
        public ImmutableArray<string> TargetColumns => ["slug"];
        public DataMigrationValues Transform(DataMigrationRow row) =>
            DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = (row["name"] as string)?.ToUpperInvariant()
            });
    }

    private sealed class BlockingSlugTransform(
        ManualResetEventSlim entered,
        ManualResetEventSlim release) : IDataMigrationTransform
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);
        public string Identity => "blocking-slug";
        public string Version => "v1";
        public ImmutableArray<string> SourceColumns => ["name"];
        public ImmutableArray<string> TargetColumns => ["slug"];

        public DataMigrationValues Transform(DataMigrationRow row)
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The concurrent migration test did not release its first transform.");
            return DataMigrationValues.Set(new Dictionary<string, object?>
            {
                ["slug"] = row["name"] + "-slug"
            });
        }
    }

    private sealed class KeyWritingTransform : IDataMigrationTransform
    {
        public string Identity => "renumber";
        public string Version => "v1";
        public ImmutableArray<string> SourceColumns => ["name"];
        public ImmutableArray<string> TargetColumns => ["id"];
        public DataMigrationValues Transform(DataMigrationRow row) => DataMigrationValues.Unchanged;
    }

    private sealed class StrayTransform : IDataMigrationTransform
    {
        public string Identity => "stray";
        public string Version => "v1";
        public ImmutableArray<string> SourceColumns => ["name"];
        public ImmutableArray<string> TargetColumns => ["slug"];
        public DataMigrationValues Transform(DataMigrationRow row) =>
            DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = "ok",
                ["name"] = "not mine"
            });
    }

    private sealed class ConstantTransform(string target, object? value) : IDataMigrationTransform
    {
        public string Identity => "constant";
        public string Version => "v1";
        public ImmutableArray<string> SourceColumns => [];
        public ImmutableArray<string> TargetColumns => [target];
        public DataMigrationValues Transform(DataMigrationRow row) =>
            DataMigrationValues.Set(new Dictionary<string, object?> { [target] = value });
    }

    /// <summary>
    /// An in-process executor that keeps the facility's contract honestly: it scans strictly after
    /// the cursor in key order, applies the caller's transform, and records the advanced entry with
    /// the rows it wrote.
    /// </summary>
    private sealed class FakeExecutor(StorageUnit unit, List<Dictionary<string, object?>>? rows = null)
        : IDataMigrationExecutor
    {
        private readonly SemaphoreSlim migrationGate = new(1, 1);
        public List<Dictionary<string, object?>> Rows { get; } = rows ?? [];

        public Dictionary<string, DataMigrationLedgerEntry> Ledger { get; } = new(StringComparer.Ordinal);

        public DataMigrationCapabilities Capabilities { get; set; } = DataMigrationRunner.Required;

        public IPhysicalSchemaApplicationLock AcquireMigrationLock(PhysicalSchemaTargetIdentity target)
        {
            migrationGate.Wait();
            return new MigrationLock(target, () => migrationGate.Release());
        }

        /// <summary>
        /// A ceiling on chunks this source will serve. No budget can bound a runner that spins —
        /// a chunk claiming progress without scanning advances neither the row nor the batch count,
        /// so MaxRows and MaxBatches never trip. That is exactly why the runner needs its own
        /// scanned-nothing guard, and why this fake refuses rather than letting a regression there
        /// hang the suite instead of failing it.
        /// </summary>
        public int MaxChunkCalls { get; set; } = 64;

        /// <summary>Return the entry unchanged from this chunk onward; -1 never does.</summary>
        public int SpinAfterChunks { get; set; } = -1;

        /// <summary>Report a scanned row but hand back an entry carrying no resume cursor.</summary>
        public bool AdvanceWithoutCursor { get; set; }

        public int ChunkCalls { get; private set; }

        public bool SawAsync { get; private set; }

        public DataMigrationLedgerEntry? ReadLedgerEntry(PhysicalSchemaTargetIdentity target, string migrationId) =>
            Ledger.GetValueOrDefault(migrationId);

        public ValueTask<DataMigrationLedgerEntry?> ReadLedgerEntryAsync(
            PhysicalSchemaTargetIdentity target, string migrationId, CancellationToken cancellationToken = default)
        {
            SawAsync = true;
            cancellationToken.ThrowIfCancellationRequested();
            return new(ReadLedgerEntry(target, migrationId));
        }

        public IReadOnlyList<DataMigrationLedgerEntry> ReadLedgerEntries(PhysicalSchemaTargetIdentity target) =>
            Ledger.Values.ToArray();

        public ValueTask<IReadOnlyList<DataMigrationLedgerEntry>> ReadLedgerEntriesAsync(
            PhysicalSchemaTargetIdentity target, CancellationToken cancellationToken = default) =>
            new(ReadLedgerEntries(target));

        public void WriteLedgerEntry(DataMigrationLedgerEntry entry) => Ledger[entry.MigrationId] = entry;

        public ValueTask WriteLedgerEntryAsync(DataMigrationLedgerEntry entry, CancellationToken cancellationToken = default)
        {
            SawAsync = true;
            cancellationToken.ThrowIfCancellationRequested();
            WriteLedgerEntry(entry);
            return default;
        }

        public DataMigrationChunkOutcome ExecuteChunk(DataMigrationChunkRequest request)
        {
            ChunkCalls++;
            if (ChunkCalls > MaxChunkCalls)
            {
                throw new InvalidOperationException(
                    $"The runner requested {ChunkCalls} chunks from a source of {Rows.Count} rows; " +
                    "a guard that bounds the loop is missing.");
            }
            if (SpinAfterChunks >= 0 && ChunkCalls > SpinAfterChunks)
                return DataMigrationChunkOutcome.Advanced(request.Entry);

            if (AdvanceWithoutCursor)
            {
                var current = request.Entry;
                return DataMigrationChunkOutcome.Advanced(new DataMigrationLedgerEntry(
                    current.Target, current.MigrationId, current.UnitName, current.RequestFingerprint,
                    DataMigrationRunState.Running, cursor: null,
                    current.RowsScanned + 1, current.RowsChanged, current.Batches + 1,
                    current.StartedAt, Now, completedAt: null));
            }

            var after = request.Cursor is null ? int.MinValue : (int)request.Cursor.Values[0]!;
            var chunk = Rows.Where(row => (int)row["id"]! > after)
                .OrderBy(row => (int)row["id"]!)
                .Take(request.MaxRows)
                .ToArray();
            if (chunk.Length == 0)
                return DataMigrationChunkOutcome.Exhausted(request.Entry);

            var changed = 0;
            foreach (var row in chunk)
            {
                if (request.Apply(row) is not { } produced)
                    continue;
                foreach (var pair in produced)
                    row[pair.Key] = pair.Value;
                changed++;
            }

            var entry = request.Entry.Advance(
                DataMigrationCursor.After(unit, chunk[^1]), chunk.Length, changed, Now);
            WriteLedgerEntry(entry);
            return chunk.Length < request.MaxRows
                ? DataMigrationChunkOutcome.Exhausted(entry)
                : DataMigrationChunkOutcome.Advanced(entry);
        }

        public ValueTask<DataMigrationChunkOutcome> ExecuteChunkAsync(
            DataMigrationChunkRequest request, CancellationToken cancellationToken = default)
        {
            SawAsync = true;
            cancellationToken.ThrowIfCancellationRequested();
            return new(ExecuteChunk(request));
        }

        private sealed class MigrationLock(
            PhysicalSchemaTargetIdentity target,
            Action release) : IPhysicalSchemaApplicationLock
        {
            private int disposed;
            public PhysicalSchemaTargetIdentity Target { get; } = target;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                    release();
            }
        }
    }
}
