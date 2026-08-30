using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Groundwork.Substrate.Relational;

Console.WriteLine(new ExternalProviderDialect().ProviderName);

// This project intentionally is not listed in Groundwork.slnx. It compiles the complete public
// provider boundary without InternalsVisibleTo. The methods marked DriverWork are the native driver
// work a real provider must supply; optional capability interfaces are deliberately absent until the
// connected deployment can honor them.
internal sealed class ExternalProviderFactory : IStorageProviderFactory
{
    public IStorageProviderConnection Create(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new ExternalProviderConnection();
    }

    // RelationalSchemaExecutor, RelationalRuntimeAdmission, and RelationalSchemaToolSession are the
    // reusable public schema/runtime-admission seam. A real provider passes its DbConnection factory.
    public static RelationalSchemaExecutor CreateSchemaExecutor(Func<DbConnection> createConnection) =>
        new(createConnection, new ExternalProviderDialect());
}

internal sealed class ExternalProviderConnection : IStorageProviderConnection
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Lazy<DbConnection> sharedConnection = new(DriverWork<DbConnection>);

    public IProviderCatalog Catalog { get; } = new ExternalProviderCatalog();

    public ISchemaCoordinator Schema { get; } = new ExternalSchemaCoordinator();

    // An empty list is honest for this compile-only skeleton. Add descriptors only with matching
    // behavior and optional session interfaces.
    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; } = [];

    public IStorageSession OpenSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer = null)
    {
        var connection = sharedConnection.Value;
        return new ExternalStorageSession(
            unit,
            access,
            connection,
            transaction: null,
            gate,
            new ExternalSessionLifetime(),
            ownsConnection: false,
            observer);
    }

    public IOwnedStorageSession OpenOwnedSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer = null)
    {
        var connection = DriverWork<DbConnection>();
        return new ExternalOwnedStorageSession(unit, access, connection, observer);
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units) =>
        BeginUnitOfWork(access, BatchWriteOptions.Default, observer: null, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        params StorageUnit[] units) =>
        BeginUnitOfWork(access, options, observer: null, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        IProviderCommandObserver? observer,
        params StorageUnit[] units)
    {
        var connection = DriverWork<DbConnection>();
        var transaction = DriverWork<DbTransaction>();
        return CreateUnitOfWork(
            access,
            options,
            units,
            connection,
            transaction,
            new SemaphoreSlim(1, 1),
            observer);
    }

    private static IUnitOfWork CreateUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        IReadOnlyList<StorageUnit> units,
        DbConnection connection,
        DbTransaction transaction,
        SemaphoreSlim gate,
        IProviderCommandObserver? observer)
    {
        var lifetime = new RelationalUnitOfWorkLifetime(
            connection,
            transaction,
            supportsAsync: true,
            disposeTransaction: true);
        return new RelationalUnitOfWork(
            units,
            options,
            unit =>
            {
                var session = new ExternalUnitOfWorkSession(
                    unit, access, connection, transaction, gate, observer);
                return new RelationalUnitOfWorkSession(session, session.Close);
            },
            lifetime);
    }

    public void Dispose()
    {
        if (sharedConnection.IsValueCreated)
            sharedConnection.Value.Dispose();
        gate.Dispose();
    }

    private static T DriverWork<T>() => throw new NotImplementedException(
        "Open the provider's native connection or transaction, then construct the shared runtime.");
}

internal sealed class ExternalUnitOfWorkSession(
    StorageUnit unit,
    StorageAccess access,
    DbConnection connection,
    DbTransaction transaction,
    SemaphoreSlim gate,
    IProviderCommandObserver? observer)
    : ExternalStorageSession(
        unit,
        access,
        connection,
        transaction,
        gate,
        new ExternalSessionLifetime(),
        ownsConnection: false,
        observer);

internal class ExternalStorageSession : RelationalStorageSessionBase
{
    internal ExternalStorageSession(
        StorageUnit unit,
        StorageAccess access,
        DbConnection connection,
        DbTransaction? transaction,
        SemaphoreSlim gate,
        ExternalSessionLifetime lifetime,
        bool ownsConnection = false,
        IProviderCommandObserver? observer = null)
        : this(
            unit,
            access,
            transaction,
            ownsConnection,
            observer,
            new ExternalSessionRuntime(connection, gate, lifetime)) { }

    private ExternalStorageSession(
        StorageUnit unit,
        StorageAccess access,
        DbTransaction? transaction,
        bool ownsConnection,
        IProviderCommandObserver? observer,
        ExternalSessionRuntime runtime)
        : base(
            unit,
            access,
            runtime.Commands,
            runtime.Appends,
            runtime.Retention,
            runtime.Commands.Connection,
            transaction,
            ownsConnection,
            observer,
            "external-stub") { }
}

internal sealed class ExternalSessionRuntime
{
    internal ExternalSessionRuntime(
        DbConnection connection,
        SemaphoreSlim gate,
        ExternalSessionLifetime lifetime)
    {
        Commands = new ExternalSessionAdapter(connection, new ExternalProviderDialect(), gate, lifetime);
        Appends = new ExternalAppendAdapter(Commands);
        Retention = new ExternalRetentionAdapter(Commands);
    }

    internal ExternalSessionAdapter Commands { get; }
    internal ExternalAppendAdapter Appends { get; }
    internal ExternalRetentionAdapter Retention { get; }
}

internal sealed class ExternalOwnedStorageSession : ExternalStorageSession, IOwnedStorageSession
{
    private readonly DbConnection connection;
    private readonly ExternalSessionLifetime lifetime;

    internal ExternalOwnedStorageSession(
        StorageUnit unit,
        StorageAccess access,
        DbConnection connection,
        IProviderCommandObserver? observer = null)
        : this(unit, access, connection, new ExternalSessionLifetime(), observer) { }

    private ExternalOwnedStorageSession(
        StorageUnit unit,
        StorageAccess access,
        DbConnection connection,
        ExternalSessionLifetime lifetime,
        IProviderCommandObserver? observer)
        : base(unit, access, connection, transaction: null, new SemaphoreSlim(1, 1), lifetime,
            ownsConnection: true, observer)
    {
        this.connection = connection;
        this.lifetime = lifetime;
    }

    public bool IsReleased => lifetime.IsReleased;

    public void Dispose()
    {
        if (!lifetime.Release()) return;
        Close();
        connection.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (!lifetime.Release()) return;
        Close();
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class ExternalSessionAdapter(
    DbConnection connection,
    RelationalDialect dialect,
    SemaphoreSlim gate,
    ExternalSessionLifetime lifetime) : RelationalStorageSessionAdapter(connection, dialect, gate)
{
    public override void EnsureUsable() => lifetime.ThrowIfReleased();

    protected override void BindParameter(
        DbCommand command,
        string parameter,
        object? value,
        ColumnDefinition column) => DriverWork();

    protected override ValueTask<WriteOutcome> Insert(
        StorageValues values,
        WriteOutcomeStatus status,
        RelationalExecution execution) => DriverWork<ValueTask<WriteOutcome>>();

    protected override ValueTask<WriteOutcome> Update(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution) => DriverWork<ValueTask<WriteOutcome>>();

    protected override ValueTask<WriteOutcome> Upsert(
        StorageValues values,
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution) => DriverWork<ValueTask<WriteOutcome>>();

    protected override ValueTask<WriteOutcome> Delete(
        StorageKey key,
        StoredEntry? existing,
        WriteOptions? options,
        RelationalExecution execution) => DriverWork<ValueTask<WriteOutcome>>();

    private static void DriverWork() => DriverWork<object>();

    private static T DriverWork<T>() => throw new NotImplementedException(
        "Replace the compile-only stub with provider-native parameter and mutation commands.");
}

internal sealed class ExternalAppendAdapter(ExternalSessionAdapter commands) : RelationalAppendAdapter
{
    protected override ValueTask<DateTimeOffset> PrepareLedger(
        RelationalAppendCommand operation,
        RelationalExecution execution) => DriverWork<DateTimeOffset>();

    protected override ValueTask ReclaimExpired(
        RelationalAppendCommand operation,
        DateTimeOffset cutoff,
        RelationalExecution execution) => DriverWork();

    protected override ValueTask<RelationalAppendLedgerState?> ReadLedger(
        RelationalAppendCommand operation,
        RelationalExecution execution) => DriverWork<RelationalAppendLedgerState?>();

    protected override ValueTask DeleteLedger(
        RelationalAppendCommand operation,
        RelationalAppendLedgerState existing,
        RelationalExecution execution) => DriverWork();

    protected override ValueTask<bool> TryClaimLedger(
        RelationalAppendCommand operation,
        DateTimeOffset providerNow,
        RelationalExecution execution) => DriverWork<bool>();

    protected override ValueTask<RelationalAppendReplayState?> ReadClaimWinner(
        RelationalAppendCommand operation,
        RelationalExecution execution) => DriverWork<RelationalAppendReplayState?>();

    protected override ValueTask<IReadOnlyList<RowWriteOutcome>> InsertPayload(
        RelationalAppendCommand operation,
        RelationalExecution execution) => DriverWork<IReadOnlyList<RowWriteOutcome>>();

    protected override ValueTask<bool> CompleteLedger(
        RelationalAppendCommand operation,
        string serializedOutcomes,
        RelationalExecution execution) => DriverWork<bool>();

    private static async ValueTask DriverWork() => await DriverWork<object>();

    private static async ValueTask<T> DriverWork<T>()
    {
        await Task.Yield();
        throw new NotImplementedException(
            "Replace the compile-only stub with provider-native append ledger and payload commands.");
    }

    // Real overrides create every command through this transaction-bound seam.
    private DbCommand Command(string sql) => commands.CreateCommand(sql);
}

internal sealed class ExternalRetentionAdapter(ExternalSessionAdapter commands) : RelationalRetentionAdapter
{
    protected override ValueTask<int> DeleteBatch(
        RelationalRetentionCommand operation,
        RelationalExecution execution) => DriverWork<int>();

    protected override ValueTask<DateTimeOffset> PrepareLedger(
        RelationalExactRetentionCommand operation,
        RelationalExecution execution) => DriverWork<DateTimeOffset>();

    protected override ValueTask ReclaimExpired(
        RelationalExactRetentionCommand operation,
        DateTimeOffset cutoff,
        RelationalExecution execution) => DriverWork();

    protected override ValueTask<RelationalRetentionLedgerState?> ReadLedger(
        RelationalExactRetentionCommand operation,
        RelationalExecution execution) => DriverWork<RelationalRetentionLedgerState?>();

    protected override ValueTask DeleteLedger(
        RelationalExactRetentionCommand operation,
        RelationalRetentionLedgerState existing,
        RelationalExecution execution) => DriverWork();

    protected override ValueTask<bool> TryClaimLedger(
        RelationalExactRetentionCommand operation,
        DateTimeOffset providerNow,
        RelationalExecution execution) => DriverWork<bool>();

    protected override ValueTask<RelationalRetentionReplayState?> ReadClaimWinner(
        RelationalExactRetentionCommand operation,
        RelationalExecution execution) => DriverWork<RelationalRetentionReplayState?>();

    protected override ValueTask<bool> CompleteLedger(
        RelationalExactRetentionCommand operation,
        string serializedResult,
        RelationalExecution execution) => DriverWork<bool>();

    private static async ValueTask DriverWork() => await DriverWork<object>();

    private static async ValueTask<T> DriverWork<T>()
    {
        await Task.Yield();
        throw new NotImplementedException(
            "Replace the compile-only stub with provider-native retention and ledger commands.");
    }

    private DbCommand Command(string sql) => commands.CreateCommand(sql);
}

internal sealed class ExternalSessionLifetime
{
    private int released;

    public bool IsReleased => Volatile.Read(ref released) != 0;

    public bool Release() => Interlocked.Exchange(ref released, 1) == 0;

    public void ThrowIfReleased()
    {
        if (IsReleased)
            throw new ObjectDisposedException(nameof(ExternalOwnedStorageSession));
    }
}

internal sealed class ExternalProviderCatalog : IProviderCatalog
{
    public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId) => [];
}

internal sealed class ExternalSchemaCoordinator : ISchemaCoordinator
{
    public GroundworkRuntimeSchemaAdmissionResult InspectRuntimeAdmission(
        StorageUnit desired,
        GroundworkRuntimeSchemaAdmissionOptions? options = null) =>
        DriverWork<GroundworkRuntimeSchemaAdmissionResult>();

    public SchemaDiff Diff(StorageUnit desired) => DriverWork<SchemaDiff>();

    public SchemaApplyResult Apply(StorageUnit desired) => DriverWork<SchemaApplyResult>();

    private static T DriverWork<T>() => throw new NotImplementedException(
        "Wrap PhysicalSchemaApplication and RelationalSchemaExecutor for the native provider.");
}

public sealed class ExternalSchemaToolSessionFactory : ISchemaToolProviderSessionFactory
{
    public string Alias => "external-stub";

    public ISchemaToolProviderSession Open(SchemaToolProviderOptions options) =>
        throw new NotImplementedException(
            "Open the native schema connection and return RelationalSchemaToolSession.");
}

internal sealed class ExternalProviderDialect : RelationalDialect
{
    public override string ProviderName => "external-stub";
    public override RelationalQueryRenderer CreateQueryRenderer() => new ExternalQueryRenderer(this);
    public override string QuoteIdentifier(string identifier) => $"[{identifier}]";
    public override string MapType(ColumnDefinition definition) => definition.Type.ToString();
    public override string? MapCollation(ColumnDefinition definition) => null;
    public override string? MapDefault(ColumnDefinition definition) => null;
    public override string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey) => "CREATE TABLE";
    public override string AddColumnSql(string table, string column, string definition) => "ADD COLUMN";
    public override string FinalizeColumnSql(string table, string column, ColumnDefinition definition) => "FINALIZE COLUMN";
    public override string CreateIndexSql(string table, IndexDefinition index, string? filter) => "CREATE INDEX";
    public override string DropIndexSql(string table, string index) => "DROP INDEX";
    public override string ConditionalUpsertSql(RelationalWriteShape shape) => "UPSERT";
    public override string BatchInsertSql(RelationalWriteShape shape, int batchSize) => "BATCH";
    public override object? ConvertValue(object? value, ColumnDefinition definition) => value;
    public override void Validate(ColumnDefinition definition) { }
    public override bool TryMapUniqueViolation(DbException exception, out string indexName)
    {
        indexName = string.Empty;
        return false;
    }
    public override void AcquireApplicationLock(DbConnection connection, string resource) { }
    public override void ReleaseApplicationLock(DbConnection connection, string resource) { }
    public override bool VerifyApplicationLock(DbConnection connection, string resource) => true;
    public override long ReadServerSessionId(DbConnection connection) => 1;
    public override long AcquireFence(DbConnection connection, PhysicalSchemaTargetIdentity target, string owner) => 1;
    public override void AssertFence(DbConnection connection, DbTransaction transaction, PhysicalSchemaTargetIdentity target, string owner, long fence) { }
    public override void EnsureInfrastructure(DbConnection connection) { }
    public override PhysicalSchemaHistoryState ReadHistory(DbConnection connection, PhysicalSchemaTargetIdentity target) => PhysicalSchemaHistoryState.Empty;
    public override void PublishHistory(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        string owner,
        long fence) { }
    public override bool TableExists(DbConnection connection, DbTransaction? transaction, string table) => true;
    public override IReadOnlyDictionary<string, RelationalColumnMetadata> ReadColumns(DbConnection connection, DbTransaction? transaction, string table) => new Dictionary<string, RelationalColumnMetadata>();
    public override RelationalIndexMetadata? ReadIndex(DbConnection connection, DbTransaction? transaction, string table, string index) => null;
}

internal sealed class ExternalQueryRenderer : RelationalQueryRenderer
{
    internal ExternalQueryRenderer(RelationalDialect dialect)
        : base(dialect, dialect.ParameterBudget, supportsIndexHints: false)
    {
        ProviderName = dialect.ProviderName;
    }

    protected override string ProviderName { get; }
}
