using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>
/// Shared relational schema executor. Providers own only the public <see cref="RelationalDialect"/>
/// contract; lifecycle, fencing, operation dispatch, and connection cleanup remain common.
/// </summary>
public sealed class RelationalSchemaExecutor
    : IPhysicalSchemaExecutor, IPhysicalSchemaHistoryInspector, IPhysicalSchemaCatalogInspector, IDataMigrationExecutor
{
    private readonly Func<DbConnection> createConnection;
    private readonly RelationalDialect dialect;
    private RelationalApplicationLock? activeLease;

    public RelationalSchemaExecutor(
        Func<DbConnection> createConnection,
        RelationalDialect dialect)
    {
        this.createConnection = createConnection ?? throw new ArgumentNullException(nameof(createConnection));
        this.dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
    }

    public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var connection = createConnection()
            ?? throw new InvalidOperationException("The relational connection factory returned null.");
        try
        {
            if (connection.State != ConnectionState.Open)
                connection.Open();
            dialect.EnsureInfrastructure(connection);
            var resource = $"groundwork:schema:{target}";
            dialect.AcquireApplicationLock(connection, resource);
            try
            {
                var owner = Guid.NewGuid().ToString("N");
                var fence = dialect.AcquireFence(connection, target, owner);
                var sessionId = dialect.ReadServerSessionId(connection);
                // The lease is remembered so a data migration executed under this apply runs on the
                // same connection and fence, rather than racing the apply from a second connection.
                var lease = new RelationalApplicationLock(
                    connection, dialect, target, resource, owner, fence, sessionId,
                    released => Interlocked.CompareExchange(ref activeLease, null, released));
                Volatile.Write(ref activeLease, lease);
                return lease;
            }
            catch
            {
                dialect.ReleaseApplicationLock(connection, resource);
                throw;
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public PhysicalSchemaHistoryState ReadHistory(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        var lease = RequireLock(target, applicationLock);
        lease.Verify();
        return dialect.ReadHistory(lease.Connection, target);
    }

    public PhysicalSchemaOperationAcknowledgement ApplyOperation(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var lease = RequireLock(target, applicationLock);
        lease.Verify();
        return ApplyOperationBatchCore(lease, target, [operation])[0];
    }

    public IReadOnlyList<PhysicalSchemaOperationAcknowledgement> ApplyOperationBatch(
        PhysicalSchemaTargetIdentity target,
        IReadOnlyList<PhysicalSchemaOperation> operations,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var snapshot = operations.ToArray();
        if (snapshot.Any(operation => operation is null))
            throw new ArgumentException("A schema operation batch cannot contain null operations.", nameof(operations));
        var lease = RequireLock(target, applicationLock);
        lease.Verify();
        return ApplyOperationBatchCore(lease, target, snapshot);
    }

    public void PublishAppliedState(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(state);
        var lease = RequireLock(state.TargetIdentity, applicationLock);
        lease.Verify();
        using var transaction = dialect.BeginTransaction(lease.Connection);
        try
        {
            dialect.AssertFence(lease.Connection, transaction, state.TargetIdentity, lease.Owner, lease.Fence);
            dialect.PublishHistory(
                lease.Connection,
                transaction,
                state.TargetIdentity,
                state,
                expectedAppliedTargetFingerprint,
                lease.Owner,
                lease.Fence);
            dialect.AssertFence(lease.Connection, transaction, state.TargetIdentity, lease.Owner, lease.Fence);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public PhysicalSchemaInspectionResult InspectHistory(PhysicalSchemaTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var connection = OpenConnection();
        dialect.EnsureInfrastructure(connection);
        var history = dialect.ReadHistory(connection, target.Identity);
        if (history.AppliedState is null)
            return new PhysicalSchemaInspectionResult(history, IsAppliedSchemaValid: true);

        var applied = history.AppliedState;
        var appliedTarget = new PhysicalSchemaTarget(
            applied.Snapshot.Subject,
            applied.Provider,
            applied.Snapshot.ProviderDefinitions);
        using var transaction = dialect.BeginTransaction(connection);
        try
        {
            var inspection = InspectTarget(
                connection, transaction, appliedTarget, history, target.Subject.ForeignColumns);
            transaction.Commit();
            return inspection;
        }
        catch (InvalidOperationException)
        {
            transaction.Rollback();
            return new PhysicalSchemaInspectionResult(history, IsAppliedSchemaValid: false);
        }
    }

    /// <summary>
    /// Read-only variant of <see cref="InspectHistory"/> for runtime admission: it provisions no
    /// infrastructure and takes no provider locks, so it stays safe on read-only stores, hot
    /// standbys, and roles without DDL rights. A missing history catalog reports as no applied
    /// state instead of being created.
    /// </summary>
    public PhysicalSchemaInspectionResult InspectDeployedHistory(PhysicalSchemaTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var connection = OpenConnection();
        return InspectDeployedHistory(target, connection);
    }

    /// <summary>Read-only inspection against a caller-owned connection, which is left open.</summary>
    public PhysicalSchemaInspectionResult InspectDeployedHistory(PhysicalSchemaTarget target, DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
            connection.Open();
        if (!dialect.TableExists(connection, null, RelationalDialect.SchemaHistoryTable))
            return new PhysicalSchemaInspectionResult(PhysicalSchemaHistoryState.Empty, IsAppliedSchemaValid: true);
        var history = dialect.ReadHistory(connection, target.Identity);
        if (history.AppliedState is null)
            return new PhysicalSchemaInspectionResult(history, IsAppliedSchemaValid: true);

        var applied = history.AppliedState;
        var appliedTarget = new PhysicalSchemaTarget(
            applied.Snapshot.Subject,
            applied.Provider,
            applied.Snapshot.ProviderDefinitions);
        if (appliedTarget.Subject.DerivedColumns.Length != 0 &&
            !dialect.TableExists(connection, null, RelationalDialect.SearchKeyAlgorithmsTable))
        {
            return new PhysicalSchemaInspectionResult(
                history,
                IsAppliedSchemaValid: false,
                ColumnDrift: [new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Relational search-key algorithm catalog '{RelationalDialect.SearchKeyAlgorithmsTable}' is missing for '{appliedTarget.Subject.Name}'.",
                    "table")]);
        }
        return InspectTarget(connection, null, appliedTarget, history, target.Subject.ForeignColumns);
    }

    /// <summary>
    /// Compares the deployed catalog to an exact compiled target under the caller's application
    /// lock, consulting no history. This is the proof <c>groundwork adopt</c> rests on, so it runs
    /// on the lock's own connection: a catalog verified on a second connection could change before
    /// the applied state claiming it is published.
    /// </summary>
    public PhysicalSchemaInspectionResult InspectDeployedCatalog(
        PhysicalSchemaTarget target,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(target);
        var lease = RequireLock(target.Identity, applicationLock);
        lease.Verify();
        return InspectTarget(
            lease.Connection, null, target, PhysicalSchemaHistoryState.Empty, target.Subject.ForeignColumns);
    }

    public bool TryMapUniqueViolation(DbException exception, out string indexName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return dialect.TryMapUniqueViolation(exception, out indexName);
    }

    private void ExecuteOperation(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaOperation operation)
    {
        switch (operation)
        {
            case CreatePrimaryStorageOperation create:
                Execute(connection, transaction, RelationalSql.CreateTable(dialect, create.Subject.Definition));
                break;
            case AddColumnOperation add:
                // Derived keys for non-null source columns are added as a nullable staging
                // column, populated by the provider-neutral algorithm, then finalized below.
                // Fresh CREATE TABLE plans still materialize the target nullability directly.
                var stagedColumn = add.Column.Name.StartsWith(SearchKeyProjection.Prefix, StringComparison.Ordinal) &&
                                   !add.Column.IsNullable
                    ? add.Column with { IsNullable = true }
                    : add.Column;
                Execute(connection, transaction, RelationalSql.AddColumn(dialect, add.Subject.Name, stagedColumn));
                break;
            case BackfillColumnOperation backfill:
                if (backfill.Derived is not null)
                    BackfillDerivedColumn(connection, transaction, backfill);
                else
                    ExecuteOptional(
                        connection,
                        transaction,
                        dialect.BackfillColumnSql(backfill.Subject.Name, backfill.Column),
                        "BackfillColumn");
                break;
            case FinalizeColumnOperation finalize:
                dialect.FinalizeColumn(connection, transaction, finalize.Subject.Name, finalize.Column);
                break;
            case CreatePhysicalIndexOperation createIndex:
                Execute(connection, transaction, RelationalSql.CreateIndex(dialect, createIndex.Subject.Name, createIndex.Index));
                break;
            case RebuildPhysicalIndexOperation rebuild:
                Execute(connection, transaction, RelationalSql.DropIndex(dialect, rebuild.Subject.Name, rebuild.Index.Name));
                Execute(connection, transaction, RelationalSql.CreateIndex(dialect, rebuild.Subject.Name, rebuild.Index));
                break;
            case RenamePrimaryStorageOperation rename:
                dialect.RenameTable(connection, transaction, rename.FromName, rename.ToName);
                foreach (var carried in rename.CarriedIndexes)
                    dialect.RenameIndex(connection, transaction, rename.FromName, rename.ToName, carried);
                // The renamed storage records provider definitions under its new name later in this
                // plan; the ones named after the old storage are removed rather than left behind.
                foreach (var superseded in rename.SupersededProviderDefinitions)
                    dialect.DropProviderDefinition(connection, transaction, superseded);
                break;
            case RenameColumnOperation renameColumn:
                Execute(
                    connection,
                    transaction,
                    dialect.RenameColumnSql(renameColumn.Subject.Name, renameColumn.FromName, renameColumn.ToName));
                break;
            case AlterColumnOperation alter:
                dialect.AlterColumn(connection, transaction, alter.Subject.Name, alter.Column);
                break;
            case DropColumnOperation drop:
                dialect.DropColumn(connection, transaction, drop.Subject.Name, drop.Column);
                break;
            // A supersession marker is a durable ledger fact, not physical work: the expand plan
            // records that a column is deliberately still there, and the contract plan records that
            // the removal above happened.
            case ColumnSupersessionOperation:
                break;
            case DropPhysicalIndexOperation dropIndex:
                Execute(connection, transaction, RelationalSql.DropIndex(dialect, dropIndex.Subject.Name, dropIndex.Index.Name));
                break;
            case DropPrimaryStorageOperation dropStorage:
                foreach (var superseded in dropStorage.SupersededProviderDefinitions)
                    dialect.DropProviderDefinition(connection, transaction, superseded);
                Execute(connection, transaction, dialect.DropTableSql(dropStorage.Name));
                break;
            case ApplyProviderPhysicalSchemaDefinitionOperation applyProvider:
                dialect.ApplyProviderDefinition(connection, transaction, applyProvider.Definition);
                break;
            case ValidatePhysicalSchemaOperation validate:
                ValidateTarget(connection, transaction, validate.Target);
                break;
            case PublishAppliedStateOperation:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, "Unsupported relational schema operation.");
        }
    }

    private static void Execute(DbConnection connection, DbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteOptional(
        DbConnection connection,
        DbTransaction transaction,
        string? sql,
        string operation)
    {
        if (sql is null)
            throw new NotSupportedException($"The relational dialect does not implement {operation}.");
        Execute(connection, transaction, sql);
    }

    private void BackfillDerivedColumn(
        DbConnection connection,
        DbTransaction transaction,
        BackfillColumnOperation operation)
    {
        // A dialect may provide an authoritative SQL backfill for a derived column (for
        // example, a provider-specific generated expression). The shipped dialects return
        // null here because the portable search-key algorithm must run in the host process;
        // retaining this hook keeps custom dialects and contract-test doubles operational.
        if (dialect.BackfillColumnSql(operation.Subject.Name, operation.Column) is { } sql)
        {
            Execute(connection, transaction, sql);
            return;
        }

        // The same scan/transform/set-based-write used by the resumable data-migration runner,
        // driven to exhaustion inside the schema-apply transaction: a derived column must be
        // populated before the plan finalizes it, so this caller cannot stop on a budget.
        var unit = operation.Subject.Definition;
        var transform = new DerivedColumnTransform(unit, [operation.Derived!]);
        var projection = new DataMigration(
                operation.SemanticMigrationId ?? "derived-column-backfill",
                operation.Subject.Id,
                transform)
            .ValidateAgainst(unit);
        var admitted = RelationalRowMigration.AdmittedRows(
            dialect, unit.Key.Columns.Count, transform.TargetColumns.Length, DerivedBackfillBatchRows);
        IReadOnlyList<object?>? cursor = null;
        while (true)
        {
            var chunk = RelationalRowMigration.ExecuteChunk(
                dialect,
                connection,
                transaction,
                unit,
                projection,
                cursor,
                admitted,
                Project,
                RelationalExecution.Synchronous).GetAwaiter().GetResult();
            if (chunk.Exhausted)
                return;
            cursor = unit.Key.Columns.Select(column => chunk.LastRow![column]).ToArray();
        }

        IReadOnlyDictionary<string, object?>? Project(IReadOnlyDictionary<string, object?> row)
        {
            var produced = transform.Transform(new DataMigrationRow(row));
            return produced.HasValues ? produced.Values : null;
        }
    }

    /// <summary>
    /// Rows per set-based statement for the in-transaction derived-column backfill. The chunk is
    /// clamped further by the dialect's parameter budget.
    /// </summary>
    private const int DerivedBackfillBatchRows = 512;

    public DataMigrationCapabilities Capabilities =>
        DataMigrationCapabilities.KeysetScan |
        DataMigrationCapabilities.AtomicChunkProgress |
        DataMigrationCapabilities.SetBasedBatchUpdate |
        (dialect.DataMigrationLedgerUpsertSql is null
            ? DataMigrationCapabilities.None
            : DataMigrationCapabilities.AppliedLedger);

    public DataMigrationLedgerEntry? ReadLedgerEntry(PhysicalSchemaTargetIdentity target, string migrationId) =>
        ReadLedgerEntryCore(target, migrationId, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<DataMigrationLedgerEntry?> ReadLedgerEntryAsync(
        PhysicalSchemaTargetIdentity target,
        string migrationId,
        CancellationToken cancellationToken = default) =>
        ReadLedgerEntryCore(target, migrationId, RelationalExecution.Asynchronous(cancellationToken));

    public IReadOnlyList<DataMigrationLedgerEntry> ReadLedgerEntries(PhysicalSchemaTargetIdentity target) =>
        ReadLedgerEntriesCore(target, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<IReadOnlyList<DataMigrationLedgerEntry>> ReadLedgerEntriesAsync(
        PhysicalSchemaTargetIdentity target,
        CancellationToken cancellationToken = default) =>
        ReadLedgerEntriesCore(target, RelationalExecution.Asynchronous(cancellationToken));

    public void WriteLedgerEntry(DataMigrationLedgerEntry entry) =>
        WriteLedgerEntryCore(entry, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask WriteLedgerEntryAsync(DataMigrationLedgerEntry entry, CancellationToken cancellationToken = default) =>
        WriteLedgerEntryCore(entry, RelationalExecution.Asynchronous(cancellationToken));

    public DataMigrationChunkOutcome ExecuteChunk(DataMigrationChunkRequest request) =>
        ExecuteChunkCore(request, RelationalExecution.Synchronous).GetAwaiter().GetResult();

    public ValueTask<DataMigrationChunkOutcome> ExecuteChunkAsync(
        DataMigrationChunkRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteChunkCore(request, RelationalExecution.Asynchronous(cancellationToken));

    private ValueTask<DataMigrationLedgerEntry?> ReadLedgerEntryCore(
        PhysicalSchemaTargetIdentity target,
        string migrationId,
        RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        return WithConnection(
            target,
            connection => dialect.TableExists(connection, null, RelationalDataMigrationLedger.TableName)
                ? RelationalDataMigrationLedger.Read(dialect, connection, null, target, migrationId, mode)
                : new ValueTask<DataMigrationLedgerEntry?>((DataMigrationLedgerEntry?)null),
            mode,
            ensureInfrastructure: false);
    }

    private ValueTask<IReadOnlyList<DataMigrationLedgerEntry>> ReadLedgerEntriesCore(
        PhysicalSchemaTargetIdentity target,
        RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(target);
        return WithConnection(
            target,
            connection => dialect.TableExists(connection, null, RelationalDataMigrationLedger.TableName)
                ? RelationalDataMigrationLedger.ReadAll(dialect, connection, null, target, null, mode)
                : new ValueTask<IReadOnlyList<DataMigrationLedgerEntry>>(Array.Empty<DataMigrationLedgerEntry>()),
            mode,
            ensureInfrastructure: false);
    }

    private async ValueTask WriteLedgerEntryCore(DataMigrationLedgerEntry entry, RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await WithConnection<object?>(entry.Target, async connection =>
        {
            await RelationalDataMigrationLedger.Write(dialect, connection, null, entry, mode).ConfigureAwait(false);
            return null;
        }, mode).ConfigureAwait(false);
    }

    /// <summary>
    /// One chunk: read after the cursor, transform in the host process, write the produced values,
    /// and record the advanced ledger entry — all inside one transaction, so an interruption either
    /// leaves the rows unwritten and the cursor where it was, or both moved together.
    /// </summary>
    private ValueTask<DataMigrationChunkOutcome> ExecuteChunkCore(
        DataMigrationChunkRequest request,
        RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WithConnection(request.Entry.Target, async connection =>
        {
            var lease = Volatile.Read(ref activeLease);
            var transaction = await dialect.BeginTransaction(connection, mode).ConfigureAwait(false);
            try
            {
                if (lease is not null && ReferenceEquals(lease.Connection, connection))
                    dialect.AssertFence(connection, transaction, lease.Target, lease.Owner, lease.Fence);
                var admitted = RelationalRowMigration.AdmittedRows(
                    dialect,
                    request.Unit.Key.Columns.Count,
                    request.Migration.Transform.TargetColumns.Length,
                    request.MaxRows);
                var chunk = await RelationalRowMigration.ExecuteChunk(
                    dialect,
                    connection,
                    transaction,
                    request.Unit,
                    request.Projection,
                    request.Cursor?.Values.ToArray(),
                    admitted,
                    request.Apply,
                    mode).ConfigureAwait(false);

                var entry = request.Entry;
                if (chunk.RowsScanned > 0)
                {
                    entry = entry.Advance(
                        DataMigrationCursor.After(request.Unit, chunk.LastRow!),
                        chunk.RowsScanned,
                        chunk.RowsChanged,
                        DateTimeOffset.UtcNow);
                    await RelationalDataMigrationLedger.Write(dialect, connection, transaction, entry, mode)
                        .ConfigureAwait(false);
                }

                await mode.Commit(transaction).ConfigureAwait(false);
                return chunk.Exhausted
                    ? DataMigrationChunkOutcome.Exhausted(entry)
                    : DataMigrationChunkOutcome.Advanced(entry);
            }
            catch
            {
                await mode.Rollback(transaction).ConfigureAwait(false);
                throw;
            }
            finally
            {
                await mode.Dispose(transaction).ConfigureAwait(false);
            }
        }, mode);
    }

    /// <summary>
    /// Runs on the connection that holds the current application lock when one is held, so a data
    /// migration executed under a schema apply shares that apply's connection and fence; otherwise
    /// it opens and closes its own.
    /// </summary>
    private async ValueTask<T> WithConnection<T>(
        PhysicalSchemaTargetIdentity target,
        Func<DbConnection, ValueTask<T>> body,
        RelationalExecution mode,
        bool ensureInfrastructure = true)
    {
        mode.CancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref activeLease) is { } lease && lease.Target == target)
            return await body(lease.Connection).ConfigureAwait(false);
        var connection = OpenConnection();
        try
        {
            // Reads provision nothing: status and inspection must stay safe on a read-only store,
            // so a missing ledger reports as no recorded migration instead of being created.
            if (ensureInfrastructure)
                dialect.EnsureInfrastructure(connection);
            return await body(connection).ConfigureAwait(false);
        }
        finally
        {
            await RelationalExecution.CloseConnection(connection, mode).ConfigureAwait(false);
        }
    }


    private IReadOnlyList<PhysicalSchemaOperationAcknowledgement> ApplyOperationBatchCore(
        RelationalApplicationLock lease,
        PhysicalSchemaTargetIdentity target,
        IReadOnlyList<PhysicalSchemaOperation> operations)
    {
        using var transaction = dialect.BeginTransaction(lease.Connection);
        var acknowledgements = new List<PhysicalSchemaOperationAcknowledgement>(operations.Count);
        try
        {
            dialect.AssertFence(lease.Connection, transaction, target, lease.Owner, lease.Fence);
            foreach (var operation in operations)
            {
                dialect.AssertFence(lease.Connection, transaction, target, lease.Owner, lease.Fence);
                // The neutral planner records Add/Backfill/Finalize for every declaration so
                // providers can acknowledge one stable ledger. Dialects that create all columns
                // in their CREATE TABLE statement must not execute those duplicate operations.
                if (!(dialect.CreateTableIncludesColumns &&
                      operations.Any(item => item is CreatePrimaryStorageOperation) &&
                      operation is AddColumnOperation or BackfillColumnOperation or FinalizeColumnOperation))
                    ExecuteOperation(lease.Connection, transaction, operation);
                acknowledgements.Add(new PhysicalSchemaOperationAcknowledgement(
                    operation.Identity,
                    operation.Fingerprint,
                    DateTimeOffset.UtcNow));
            }

            dialect.AssertFence(lease.Connection, transaction, target, lease.Owner, lease.Fence);
            transaction.Commit();
            return acknowledgements;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    private void ValidateTarget(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTarget target)
    {
        var inspection = InspectTarget(
            connection, transaction, target, PhysicalSchemaHistoryState.Empty, target.Subject.ForeignColumns);
        if (inspection.HasColumnDrift || inspection.HasIndexDrift)
        {
            var refusal = inspection.HasColumnDrift ? inspection.ColumnDrift[0] : inspection.IndexDrift[0];
            throw new InvalidOperationException(refusal.Message);
        }
    }

    private PhysicalSchemaInspectionResult InspectTarget(
        DbConnection connection,
        DbTransaction? transaction,
        PhysicalSchemaTarget target,
        PhysicalSchemaHistoryState history,
        ForeignColumnPolicy foreignColumns)
    {
        var table = target.Subject.Name;
        if (target.Subject.Evolution.RetiresPrimaryStorage)
        {
            // A retired subject declares no catalog to compare against: its applied ledger is empty
            // by construction. Whether the removal still has to run is a planning question, and the
            // plan answers it with a pending drop.
            return new PhysicalSchemaInspectionResult(history, IsAppliedSchemaValid: true);
        }
        if (!dialect.TableExists(connection, transaction, table))
        {
            return new PhysicalSchemaInspectionResult(
                history,
                IsAppliedSchemaValid: false,
                ColumnDrift: [new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Relational schema table '{table}' does not exist.",
                    "table")]);
        }

        var columns = dialect.ReadColumns(connection, transaction, table)
            ?? throw new InvalidOperationException($"The relational dialect returned no column catalog for '{table}'.");
        var columnDrift = new List<SchemaRefusal>();
        foreach (var expected in target.Subject.Columns)
        {
            if (!columns.TryGetValue(expected.Name, out var actual) || actual is null)
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Relational schema table '{table}' is missing column '{expected.Name}'.",
                    $"columns.{expected.Name}"));
                continue;
            }
            var expectedKeyOrder = Array.IndexOf(target.Subject.Key.Columns.ToArray(), expected.Name) + 1;
            var differences = new List<string>();
            var expectedType = dialect.MapType(expected);
            var expectedDefault = dialect.MapDefault(expected);
            var expectedCollation = dialect.MapCollation(expected);
            if (!string.Equals(actual.Name, expected.Name, StringComparison.Ordinal))
                differences.Add($"name '{actual.Name}' != '{expected.Name}'");
            if (!string.Equals(actual.StoreType, expectedType, StringComparison.OrdinalIgnoreCase))
                differences.Add($"type '{actual.StoreType}' != '{expectedType}'");
            if (actual.IsNullable != expected.IsNullable)
                differences.Add($"nullability {actual.IsNullable} != {expected.IsNullable}");
            if (!string.Equals(actual.DefaultValue, expectedDefault, StringComparison.Ordinal))
                differences.Add($"default '{actual.DefaultValue ?? "<none>"}' != '{expectedDefault ?? "<none>"}'");
            if (!string.Equals(actual.Collation, expectedCollation, StringComparison.OrdinalIgnoreCase))
                differences.Add($"collation '{actual.Collation ?? "<none>"}' != '{expectedCollation ?? "<none>"}'");
            if (actual.PrimaryKeyOrder != expectedKeyOrder)
                differences.Add($"primary-key order {actual.PrimaryKeyOrder} != {expectedKeyOrder}");
            if (actual.Generation != expected.Generation)
                differences.Add($"generation {actual.Generation} != {expected.Generation}");
            if (actual.IsComputed)
                differences.Add("computed column is true");
            if (actual.IsPersisted)
                differences.Add("persisted computed column is true");
            if (actual.ComputedDefinition is not null)
                differences.Add($"computed definition '{actual.ComputedDefinition}' is present");
            if (differences.Count != 0)
            {
                columnDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-001",
                    $"Relational schema column '{table}.{expected.Name}' differs: {string.Join(", ", differences)}.",
                    $"columns.{expected.Name}"));
            }
        }

        // Columns the declaration does not describe. The provider reads the facts; the kernel owns
        // the decision, so a foreign column means the same thing at startup, at apply-time
        // validation, and at adoption.
        // A retained superseded column is described by the declaration — it names the column in
        // Evolution.Supersessions and deliberately keeps it in the catalog through the
        // dual-presence window — so it is not foreign. It is absent from Columns by construction:
        // SchemaSubject refuses a declaration that both supersedes a column and still declares it.
        var declared = target.Subject.Columns
            .Select(column => column.Name)
            .Concat(target.Subject.Evolution.Supersessions.Select(supersession => supersession.Name))
            .ToHashSet(StringComparer.Ordinal);
        var verdict = ForeignColumnAdmission.Classify(
            table,
            foreignColumns,
            columns.Values
                .Where(column => column is not null && !declared.Contains(column.Name))
                .Select(column => new ForeignPhysicalColumn(
                    column.Name,
                    column.IsNullable,
                    column.DefaultValue is not null,
                    column.Generation != ColumnGeneration.Supplied || column.IsComputed)));
        columnDrift.AddRange(verdict.Drift);

        if (target.Subject.DerivedColumns.Length != 0)
        {
            var algorithms = dialect.ReadDerivedSearchKeyAlgorithms(connection, transaction, table)
                ?? throw new InvalidOperationException(
                    $"The relational dialect returned no search-key algorithm catalog for '{table}'.");
            foreach (var expected in target.Subject.DerivedColumns)
            {
                var expectedAlgorithm = ProjectionAlgorithmId(expected);
                if (!algorithms.TryGetValue(expected.Name, out var actualAlgorithm) ||
                    !string.Equals(actualAlgorithm, expectedAlgorithm, StringComparison.Ordinal))
                {
                    columnDrift.Add(new SchemaRefusal(
                        "GW-RUNTIME-001",
                        $"Relational persisted search-key algorithm for derived column '{table}.{expected.Name}' differs: " +
                        $"'{actualAlgorithm ?? "<missing>"}' != '{expectedAlgorithm}'.",
                        $"columns.{expected.Name}.searchKeyAlgorithm"));
                }
            }
        }

        var indexDrift = new List<SchemaRefusal>();
        foreach (var expectedIndex in target.Subject.Indexes)
        {
            var actual = dialect.ReadIndex(connection, transaction, table, expectedIndex.Name);
            if (actual is null)
            {
                indexDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Relational schema table '{table}' is missing index '{expectedIndex.Name}'.",
                    $"indexes.{expectedIndex.Name}"));
                continue;
            }
            if (actual.IsUnique != expectedIndex.IsUnique)
            {
                indexDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Relational schema index '{table}.{expectedIndex.Name}' has unexpected uniqueness.",
                    $"indexes.{expectedIndex.Name}"));
                continue;
            }
            var expectedColumns = expectedIndex.Columns
                .Select(column => new RelationalIndexColumnMetadata(column.Column, column.Direction))
                .ToArray();
            if (!IndexColumnsMatch(actual.Columns, expectedColumns) ||
                !string.Equals(
                    NormalizeIndexFilter(actual.Filter),
                    NormalizeIndexFilter(dialect.IndexFilter(expectedIndex)),
                    StringComparison.Ordinal))
            {
                indexDrift.Add(new SchemaRefusal(
                    "GW-RUNTIME-002",
                    $"Relational schema index '{table}.{expectedIndex.Name}' does not match its declaration.",
                    $"indexes.{expectedIndex.Name}"));
            }
        }

        var inspection = new PhysicalSchemaInspectionResult(
            history,
            IsAppliedSchemaValid: columnDrift.Count == 0,
            columnDrift.ToImmutableArray(),
            indexDrift.ToImmutableArray())
        {
            ToleratedDrift = verdict.Tolerated
        };

        if (inspection.IsAppliedSchemaValid)
        {
            try
            {
                // Provider invariants remain part of the non-mutating open/inspection path. A
                // provider may reject a catalog shape that the neutral checks cannot describe.
                dialect.ValidateTarget(connection, transaction, target);
            }
            catch (InvalidOperationException exception)
            {
                return inspection with
                {
                    IsAppliedSchemaValid = false,
                    ColumnDrift = [new SchemaRefusal(
                        "GW-RUNTIME-001",
                        $"Relational provider invariant failed: {exception.Message}",
                        "provider")]
                };
            }
        }

        return inspection;
    }

    private static bool IndexColumnsMatch(
        IReadOnlyList<RelationalIndexColumnMetadata> actual,
        IReadOnlyList<RelationalIndexColumnMetadata> expected)
    {
        if (actual.Count != expected.Count)
            return false;
        for (var index = 0; index < expected.Count; index++)
        {
            var actualColumn = actual[index];
            var expectedColumn = expected[index];
            if (!string.Equals(actualColumn.Name, expectedColumn.Name, StringComparison.Ordinal) ||
                actualColumn.Direction != expectedColumn.Direction)
                return false;

            // Dialects that expose provider null ordering report it. A null value preserves
            // the historical direction-only contract for catalogs that cannot expose this bit.
            var expectedNullsFirst = expectedColumn.Direction == SortDirection.Ascending;
            if (actualColumn.NullsFirst is { } nullsFirst && nullsFirst != expectedNullsFirst)
                return false;
        }
        return true;
    }

    private static string ProjectionAlgorithmId(DerivedColumnDefinition definition) => definition.AlgorithmId ?? definition.Projection switch
    {
        PortableProjection.UnicodeFold => PortableStringComparison.UnicodeOrdinalIgnoreCaseAlgorithmId,
        PortableProjection.BoundarySearchKey => PortableStringComparison.SearchKeyAlgorithmId,
        PortableProjection.LocaleSortKey => throw new InvalidOperationException(
            $"Locale sort-key projection '{definition.Name}' requires an explicit algorithm identity."),
        PortableProjection.Sha256 => PortableStringComparison.LookupHashAlgorithmId,
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Projection, null)
    };

    private static string? NormalizeIndexFilter(string? filter) =>
        filter is null
            ? null
            : new string(filter.Where(character =>
                !char.IsWhiteSpace(character) &&
                character is not ('"' or '[' or ']' or '`' or '(' or ')')).ToArray());

    private DbConnection OpenConnection()
    {
        var connection = createConnection()
            ?? throw new InvalidOperationException("The relational connection factory returned null.");
        try
        {
            if (connection.State != ConnectionState.Open)
                connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private RelationalApplicationLock RequireLock(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (applicationLock is not RelationalApplicationLock lease || lease.Target != target)
            throw new ArgumentException("The application lock was not created for this relational executor and target.", nameof(applicationLock));
        return lease;
    }
}

/// <summary>Dedicated connection and fencing lease returned by the relational executor.</summary>
public sealed class RelationalApplicationLock : IPhysicalSchemaApplicationLock
{
    private readonly RelationalDialect dialect;
    private readonly string resource;
    private bool disposed;

    private readonly Action<RelationalApplicationLock>? released;

    internal RelationalApplicationLock(
        DbConnection connection,
        RelationalDialect dialect,
        PhysicalSchemaTargetIdentity target,
        string resource,
        string owner,
        long fence,
        long serverSessionId,
        Action<RelationalApplicationLock>? released = null)
    {
        this.released = released;
        Connection = connection;
        this.dialect = dialect;
        Target = target;
        this.resource = resource;
        Owner = owner;
        Fence = fence;
        ServerSessionId = serverSessionId;
    }

    public PhysicalSchemaTargetIdentity Target { get; }

    internal DbConnection Connection { get; }

    internal string Owner { get; }

    internal long Fence { get; }

    /// <summary>Provider session identity captured with the application lock.</summary>
    public long ServerSessionId { get; }

    internal void Verify()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!dialect.VerifyApplicationLock(Connection, resource))
            throw new InvalidOperationException($"The relational application lock '{resource}' is no longer held.");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        try
        {
            dialect.ReleaseApplicationLock(Connection, resource);
        }
        finally
        {
            try
            {
                Connection.Dispose();
            }
            finally
            {
                released?.Invoke(this);
            }
        }
    }
}
