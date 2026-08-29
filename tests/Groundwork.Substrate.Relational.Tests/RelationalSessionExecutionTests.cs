using System.Data;
using System.Data.Common;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Substrate.Relational.Tests;

public sealed class RelationalSessionExecutionTests
{
    [Fact]
    public async Task Direct_write_owns_transaction_commit_and_gate_lifecycle()
    {
        var connection = new TrackingConnection();
        var gate = new TrackingGate();
        var execution = Create(connection, gate);

        var result = await execution.ExecuteWrite(
            () => ValueTask.FromResult(execution.Transaction is not null ? 42 : 0),
            RelationalExecution.Synchronous);

        Assert.Equal(42, result);
        Assert.Equal(IsolationLevel.Serializable, connection.LastIsolation);
        Assert.Equal(1, connection.CommitCalls);
        Assert.Equal(0, connection.RollbackCalls);
        Assert.Equal(1, connection.TransactionDisposeCalls);
        Assert.Equal(1, gate.EnterCalls);
        Assert.Equal(1, gate.ExitCalls);
        Assert.Null(execution.Transaction);
    }

    [Fact]
    public async Task Failed_write_rolls_back_and_preserves_primary_failure()
    {
        var connection = new TrackingConnection();
        var execution = Create(connection, new TrackingGate());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await execution.ExecuteWrite<int>(
                () => ValueTask.FromException<int>(new InvalidOperationException("primary")),
                RelationalExecution.Synchronous));

        Assert.Equal("primary", failure.Message);
        Assert.Equal(0, connection.CommitCalls);
        Assert.Equal(1, connection.RollbackCalls);
        Assert.Equal(1, connection.TransactionDisposeCalls);
        Assert.Null(execution.Transaction);
    }

    [Fact]
    public async Task Ambient_transaction_and_batch_fallback_reuse_existing_execution_scope()
    {
        var connection = new TrackingConnection();
        var ambient = connection.BeginTransaction();
        var gate = new TrackingGate();
        var execution = Create(connection, gate, ambient);

        await execution.ExecuteWrite(
            () => ValueTask.FromResult(0),
            RelationalExecution.Synchronous);
        using (execution.EnterBatchFallback())
        {
            await execution.ExecuteWrite(
                () => ValueTask.FromResult(0),
                RelationalExecution.Synchronous);
        }

        Assert.Same(ambient, execution.Transaction);
        Assert.Equal(1, connection.BeginTransactionCalls);
        Assert.Equal(0, gate.EnterCalls);
    }

    [Fact]
    public async Task Adapter_can_serialize_reads_inside_an_ambient_transaction()
    {
        var connection = new TrackingConnection();
        var ambient = connection.BeginTransaction();
        var gate = new TrackingGate();
        var execution = Create(connection, gate, ambient, serializeAmbientReads: true);

        await execution.Execute(
            () => ValueTask.FromResult(0),
            RelationalExecution.Synchronous);

        Assert.Equal(1, gate.EnterCalls);
        Assert.Equal(1, gate.ExitCalls);
    }

    [Fact]
    public async Task Nested_direct_write_is_refused_before_waiting_for_the_gate_again()
    {
        var connection = new TrackingConnection();
        var gate = new TrackingGate();
        var execution = Create(connection, gate);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await execution.ExecuteWrite(
                () => execution.ExecuteWrite(
                    () => ValueTask.FromResult(0),
                    RelationalExecution.Synchronous),
                RelationalExecution.Synchronous));

        Assert.Equal(1, gate.EnterCalls);
        Assert.Equal(1, gate.ExitCalls);
    }

    [Fact]
    public async Task Concurrency_conflict_is_translated_only_for_write_outcomes()
    {
        var execution = Create(new TrackingConnection(), new TrackingGate());

        var result = await execution.Execute(
            () => ValueTask.FromException<WriteOutcome>(new RelationalConcurrencyConflictException(7)),
            RelationalExecution.Synchronous);

        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, result.Status);
        Assert.Equal(7, result.Version);
    }

    [Fact]
    public async Task Gate_wait_honors_the_execution_cancellation_token()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var execution = Create(new TrackingConnection(), new TrackingGate());
        var called = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await execution.Execute(
                () =>
                {
                    called = true;
                    return ValueTask.FromResult(0);
                },
                RelationalExecution.Asynchronous(cancellation.Token)));

        Assert.False(called);
    }

    [Fact]
    public async Task Rollback_runs_after_the_caller_cancels_the_failed_write()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new TrackingConnection();
        var execution = Create(connection, new TrackingGate());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await execution.ExecuteWrite<int>(
                () =>
                {
                    cancellation.Cancel();
                    return ValueTask.FromException<int>(new OperationCanceledException(cancellation.Token));
                },
                RelationalExecution.Asynchronous(cancellation.Token)));

        Assert.Equal(1, connection.RollbackCalls);
        Assert.Equal(1, connection.TransactionDisposeCalls);
    }

    private static RelationalSessionExecution Create(
        TrackingConnection connection,
        TrackingGate gate,
        DbTransaction? ambient = null,
        bool serializeAmbientReads = false) =>
        new(
            StorageAccess.Global,
            ambient,
            ownsConnection: false,
            new TrackingAdapter(connection, gate, serializeAmbientReads),
            objectName: "TestSession");

    private sealed class TrackingAdapter(
        TrackingConnection connection,
        TrackingGate gate,
        bool serializeAmbientReads) : IRelationalSessionExecutionAdapter
    {
        public bool SerializeAmbientReads => serializeAmbientReads;
        public void EnsureUsable() { }
        public ValueTask<IDisposable> EnterGate(RelationalExecution execution)
        {
            execution.CancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IDisposable>(gate.Enter());
        }
        public ValueTask<DbTransaction> BeginWrite(RelationalExecution execution) =>
            execution.BeginTransaction(connection, IsolationLevel.Serializable);
        public ValueTask Rollback(DbTransaction transaction, RelationalExecution execution) =>
            execution.Rollback(transaction);
    }

    private sealed class TrackingGate
    {
        internal int EnterCalls { get; private set; }
        internal int ExitCalls { get; private set; }

        internal IDisposable Enter()
        {
            EnterCalls++;
            return new Scope(this);
        }

        private sealed class Scope(TrackingGate owner) : IDisposable
        {
            public void Dispose() => owner.ExitCalls++;
        }
    }

    private sealed class TrackingConnection : DbConnection
    {
        internal int BeginTransactionCalls { get; private set; }
        internal int CommitCalls { get; private set; }
        internal int RollbackCalls { get; private set; }
        internal int TransactionDisposeCalls { get; private set; }
        internal IsolationLevel? LastIsolation { get; private set; }

#pragma warning disable CS8765
        public override string ConnectionString { get; set; } = string.Empty;
#pragma warning restore CS8765
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            BeginTransactionCalls++;
            LastIsolation = isolationLevel;
            return new TrackingTransaction(this, isolationLevel);
        }

        private sealed class TrackingTransaction(
            TrackingConnection connection,
            IsolationLevel isolationLevel) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => isolationLevel;
            protected override DbConnection DbConnection => connection;
            public override void Commit() => connection.CommitCalls++;
            public override void Rollback() => connection.RollbackCalls++;

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    connection.TransactionDisposeCalls++;
                base.Dispose(disposing);
            }
        }
    }
}
