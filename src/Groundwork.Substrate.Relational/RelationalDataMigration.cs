using System.Data.Common;
using System.Globalization;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;

namespace Groundwork.Substrate.Relational;

/// <summary>What one relational chunk scanned, changed, and where it stopped.</summary>
public sealed record RelationalRowMigrationChunk(
    long RowsScanned,
    long RowsChanged,
    IReadOnlyDictionary<string, object?>? LastRow,
    bool Exhausted);

/// <summary>
/// The one relational implementation of "scan rows in key order, run a host transform, write the
/// results back". The in-transaction derived-column backfill of a schema apply and the chunked,
/// resumable data-migration runner both drive this, so there is one definition of how a row is
/// read, transformed, and written rather than one per caller.
/// </summary>
public static class RelationalRowMigration
{
    private const string CursorParameterPrefix = "@gwc";

    /// <summary>
    /// Reads at most <paramref name="maxRows"/> rows after <paramref name="afterKey"/>, applies
    /// <paramref name="transform"/> in the host process, and writes the produced values back with
    /// one set-based statement per chunk rather than one statement per row.
    /// </summary>
    public static async ValueTask<RelationalRowMigrationChunk> ExecuteChunk(
        RelationalDialect dialect,
        DbConnection connection,
        DbTransaction? transaction,
        StorageUnit unit,
        IReadOnlyList<string> projection,
        IReadOnlyList<object?>? afterKey,
        int maxRows,
        Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>?> transform,
        RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(transform);
        if (maxRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxRows), maxRows, "A chunk must admit at least one row.");

        var keyColumns = unit.Key.Columns;
        var definitions = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var rows = new List<IReadOnlyDictionary<string, object?>>(Math.Min(maxRows, 1024));
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = ScanSql(dialect, unit, projection, afterKey is null ? 0 : keyColumns.Count, maxRows);
            if (afterKey is not null)
            {
                for (var index = 0; index < keyColumns.Count; index++)
                {
                    AddParameter(select, CursorParameterPrefix + index,
                        dialect.ConvertValue(afterKey[index], definitions[keyColumns[index]]));
                }
            }

            await using var readerScope = await mode.ExecuteReader(select).ConfigureAwait(false);
            var reader = readerScope.Reader;
            while (await mode.Read(reader).ConfigureAwait(false))
            {
                var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (var index = 0; index < projection.Count; index++)
                {
                    // Mapped through the dialect's read direction so a host transform sees the
                    // portable CLR type its declaration names, not the driver's storage class.
                    values[projection[index]] = reader.IsDBNull(index)
                        ? null
                        : dialect.ReadValue(reader.GetValue(index), definitions[projection[index]]);
                }
                rows.Add(values);
            }
        }

        if (rows.Count == 0)
            return new RelationalRowMigrationChunk(0, 0, null, Exhausted: true);

        var writes = new List<(IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?> Values)>(rows.Count);
        foreach (var row in rows)
        {
            if (transform(row) is { Count: > 0 } produced)
                writes.Add((row, produced));
        }

        var changed = writes.Count == 0
            ? 0
            : await WriteChunk(dialect, connection, transaction, unit, writes, mode).ConfigureAwait(false);
        return new RelationalRowMigrationChunk(rows.Count, changed, rows[^1], rows.Count < maxRows);
    }

    /// <summary>
    /// The largest chunk that fits a provider's parameter budget, given how many key and target
    /// columns each row binds. A provider refuses a query it cannot bind rather than truncating it,
    /// so the chunk is sized to the budget instead of discovering it at execution time.
    /// </summary>
    public static int AdmittedRows(RelationalDialect dialect, int keyColumns, int targetColumns, int requested)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        if (requested <= 0)
            throw new ArgumentOutOfRangeException(nameof(requested), requested, "A chunk must admit at least one row.");
        var perRow = Math.Max(1, keyColumns + targetColumns);
        var admitted = Math.Max(1, dialect.ParameterBudget / perRow);
        return Math.Min(requested, admitted);
    }

    private static string ScanSql(
        RelationalDialect dialect,
        StorageUnit unit,
        IReadOnlyList<string> projection,
        int cursorColumns,
        int maxRows)
    {
        var keyColumns = unit.Key.Columns;
        var selection = string.Join(", ", projection.Select(dialect.QuoteIdentifier));
        var order = string.Join(", ", keyColumns.Select(column => $"{dialect.QuoteIdentifier(column)} ASC"));
        var where = cursorColumns == 0 ? string.Empty : " WHERE " + KeysetPredicate(dialect, keyColumns);
        return $"SELECT {selection} FROM {dialect.QuoteIdentifier(unit.Name)}{where} ORDER BY {order}" +
               dialect.LimitClause(maxRows) + ";";
    }

    /// <summary>
    /// Strictly-after comparison over a composite key, spelled out term by term rather than as a
    /// row-value constructor, which SQL Server does not admit in a comparison.
    /// </summary>
    private static string KeysetPredicate(RelationalDialect dialect, IReadOnlyList<string> keyColumns)
    {
        var terms = new List<string>(keyColumns.Count);
        for (var index = 0; index < keyColumns.Count; index++)
        {
            var equalities = Enumerable.Range(0, index)
                .Select(prior => $"{dialect.QuoteIdentifier(keyColumns[prior])}={CursorParameterPrefix}{prior}");
            var greater = $"{dialect.QuoteIdentifier(keyColumns[index])}>{CursorParameterPrefix}{index}";
            terms.Add("(" + string.Join(" AND ", equalities.Append(greater)) + ")");
        }
        return string.Join(" OR ", terms);
    }

    /// <summary>One set-based UPDATE for a whole chunk, with the parameters it binds.</summary>
    public sealed record RelationalChunkUpdate(string Sql, IReadOnlyList<KeyValuePair<string, object?>> Parameters);

    /// <summary>
    /// Renders one chunk's writes as a single UPDATE whose assignments are CASE expressions keyed by
    /// row, rather than one UPDATE per row. The ELSE arm is the column itself, so the expression's
    /// type never depends on a driver inferring the type of a null parameter, and a null result is
    /// written as the SQL null literal instead of a parameter.
    /// </summary>
    public static RelationalChunkUpdate? RenderChunkUpdate(
        RelationalDialect dialect,
        StorageUnit unit,
        IReadOnlyList<(IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?> Values)> writes)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(writes);
        if (writes.Count == 0)
            return null;

        var keyColumns = unit.Key.Columns;
        var definitions = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        var targets = writes
            .SelectMany(write => write.Values.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(column => column, StringComparer.Ordinal)
            .ToArray();

        var parameters = new List<KeyValuePair<string, object?>>(writes.Count * (keyColumns.Count + targets.Length));
        var matches = new List<string>(writes.Count);
        for (var row = 0; row < writes.Count; row++)
        {
            matches.Add("(" + string.Join(" AND ", keyColumns.Select((column, index) =>
                $"{dialect.QuoteIdentifier(column)}=@gwk{row}_{index}")) + ")");
            for (var index = 0; index < keyColumns.Count; index++)
            {
                parameters.Add(new($"@gwk{row}_{index}",
                    dialect.ConvertValue(writes[row].Key[keyColumns[index]], definitions[keyColumns[index]])));
            }
        }

        var assignments = new List<string>(targets.Length);
        for (var target = 0; target < targets.Length; target++)
        {
            var column = definitions[targets[target]];
            var arms = new List<string>(writes.Count);
            for (var row = 0; row < writes.Count; row++)
            {
                if (!writes[row].Values.TryGetValue(targets[target], out var value))
                    continue;
                var converted = dialect.ConvertValue(value, column);
                var arm = "NULL";
                if (converted is not (null or DBNull))
                {
                    arm = $"@gwv{row}_{target}";
                    parameters.Add(new(arm, converted));
                }
                arms.Add($"WHEN {matches[row]} THEN {arm}");
            }
            if (arms.Count == 0)
                continue;
            var quoted = dialect.QuoteIdentifier(targets[target]);
            assignments.Add($"{quoted}=CASE {string.Join(" ", arms)} ELSE {quoted} END");
        }

        return assignments.Count == 0
            ? null
            : new RelationalChunkUpdate(
                $"UPDATE {dialect.QuoteIdentifier(unit.Name)} SET {string.Join(", ", assignments)} " +
                $"WHERE {string.Join(" OR ", matches)};",
                parameters);
    }

    private static async ValueTask<long> WriteChunk(
        RelationalDialect dialect,
        DbConnection connection,
        DbTransaction? transaction,
        StorageUnit unit,
        IReadOnlyList<(IReadOnlyDictionary<string, object?> Key, IReadOnlyDictionary<string, object?> Values)> writes,
        RelationalExecution mode)
    {
        if (RenderChunkUpdate(dialect, unit, writes) is not { } rendered)
            return 0;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = rendered.Sql;
        foreach (var parameter in rendered.Parameters)
            AddParameter(update, parameter.Key, parameter.Value);
        var affected = await mode.ExecuteNonQuery(update).ConfigureAwait(false);
        // Providers report -1 when row counts are suppressed; the chunk still changed what it wrote.
        return affected < 0 ? writes.Count : affected;
    }

    internal static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Provider-owned durable state for applied and in-flight data migrations.</summary>
public static class RelationalDataMigrationLedger
{
    public const string TableName = "__groundwork_data_migrations";

    private const string RunningState = "running";
    private const string CompletedState = "completed";

    /// <summary>
    /// Portable DDL for the ledger. Dialects whose <c>CREATE TABLE IF NOT EXISTS</c> spelling this
    /// matches append it to their infrastructure statement instead of restating the columns.
    /// </summary>
    public static string CreateTableIfNotExistsSql(string quotedTable, string text, string integer, string bigInteger) =>
        $"""
        CREATE TABLE IF NOT EXISTS {quotedTable} (
            "subject_id" {text} NOT NULL,
            "provider_name" {text} NOT NULL,
            "migration_id" {text} NOT NULL,
            "unit_name" {text} NOT NULL,
            "request_fingerprint" {text} NOT NULL,
            "state" {text} NOT NULL,
            "cursor" {text} NULL,
            "rows_scanned" {bigInteger} NOT NULL,
            "rows_changed" {bigInteger} NOT NULL,
            "batches" {integer} NOT NULL,
            "started_at" {text} NOT NULL,
            "updated_at" {text} NOT NULL,
            "completed_at" {text} NULL,
            PRIMARY KEY ("subject_id", "provider_name", "migration_id")
        );
        """;

    public static async ValueTask<DataMigrationLedgerEntry?> Read(
        RelationalDialect dialect,
        DbConnection connection,
        DbTransaction? transaction,
        PhysicalSchemaTargetIdentity target,
        string migrationId,
        RelationalExecution mode)
    {
        var entries = await ReadAll(dialect, connection, transaction, target, migrationId, mode).ConfigureAwait(false);
        return entries.Count == 0 ? null : entries[0];
    }

    public static async ValueTask<IReadOnlyList<DataMigrationLedgerEntry>> ReadAll(
        RelationalDialect dialect,
        DbConnection connection,
        DbTransaction? transaction,
        PhysicalSchemaTargetIdentity target,
        string? migrationId,
        RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(target);
        RequireLedger(dialect);
        var table = dialect.QuoteIdentifier(TableName);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SELECT {dialect.QuoteIdentifier("migration_id")},{dialect.QuoteIdentifier("unit_name")}," +
            $"{dialect.QuoteIdentifier("request_fingerprint")},{dialect.QuoteIdentifier("state")}," +
            $"{dialect.QuoteIdentifier("cursor")},{dialect.QuoteIdentifier("rows_scanned")}," +
            $"{dialect.QuoteIdentifier("rows_changed")},{dialect.QuoteIdentifier("batches")}," +
            $"{dialect.QuoteIdentifier("started_at")},{dialect.QuoteIdentifier("updated_at")}," +
            $"{dialect.QuoteIdentifier("completed_at")} FROM {table} " +
            $"WHERE {dialect.QuoteIdentifier("subject_id")}=@subject AND {dialect.QuoteIdentifier("provider_name")}=@provider" +
            (migrationId is null ? string.Empty : $" AND {dialect.QuoteIdentifier("migration_id")}=@migration") +
            $" ORDER BY {dialect.QuoteIdentifier("migration_id")} ASC;";
        RelationalRowMigration.AddParameter(command, "@subject", target.SubjectId.Value);
        RelationalRowMigration.AddParameter(command, "@provider", target.ProviderName);
        if (migrationId is not null)
            RelationalRowMigration.AddParameter(command, "@migration", migrationId);

        var entries = new List<DataMigrationLedgerEntry>();
        await using var readerScope = await mode.ExecuteReader(command).ConfigureAwait(false);
        var reader = readerScope.Reader;
        while (await mode.Read(reader).ConfigureAwait(false))
        {
            var state = reader.GetString(3);
            entries.Add(new DataMigrationLedgerEntry(
                target,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                state switch
                {
                    RunningState => DataMigrationRunState.Running,
                    CompletedState => DataMigrationRunState.Completed,
                    _ => throw new DataMigrationRefusedException(
                        DataMigrationCodes.LedgerCorrupt,
                        $"the data-migration ledger records unknown state '{state}' for '{reader.GetString(0)}'.")
                },
                reader.IsDBNull(4) ? null : reader.GetString(4),
                Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                Convert.ToInt64(reader.GetValue(6), CultureInfo.InvariantCulture),
                Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture),
                ReadInstant(reader.GetString(8)),
                ReadInstant(reader.GetString(9)),
                reader.IsDBNull(10) ? null : ReadInstant(reader.GetString(10))));
        }
        return entries;
    }

    public static async ValueTask Write(
        RelationalDialect dialect,
        DbConnection connection,
        DbTransaction? transaction,
        DataMigrationLedgerEntry entry,
        RelationalExecution mode)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(entry);
        var upsert = RequireLedger(dialect);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = upsert;
        RelationalRowMigration.AddParameter(command, "@subject", entry.Target.SubjectId.Value);
        RelationalRowMigration.AddParameter(command, "@provider", entry.Target.ProviderName);
        RelationalRowMigration.AddParameter(command, "@migration", entry.MigrationId);
        RelationalRowMigration.AddParameter(command, "@unit", entry.UnitName);
        RelationalRowMigration.AddParameter(command, "@fingerprint", entry.RequestFingerprint);
        RelationalRowMigration.AddParameter(command, "@state",
            entry.IsComplete ? CompletedState : RunningState);
        RelationalRowMigration.AddParameter(command, "@cursor", entry.Cursor);
        RelationalRowMigration.AddParameter(command, "@scanned", entry.RowsScanned);
        RelationalRowMigration.AddParameter(command, "@changed", entry.RowsChanged);
        RelationalRowMigration.AddParameter(command, "@batches", entry.Batches);
        RelationalRowMigration.AddParameter(command, "@started", WriteInstant(entry.StartedAt));
        RelationalRowMigration.AddParameter(command, "@updated", WriteInstant(entry.UpdatedAt));
        RelationalRowMigration.AddParameter(command, "@completed",
            entry.CompletedAt is { } completed ? WriteInstant(completed) : null);
        await mode.ExecuteNonQuery(command).ConfigureAwait(false);
    }

    private static string RequireLedger(RelationalDialect dialect) =>
        dialect.DataMigrationLedgerUpsertSql ?? throw new DataMigrationRefusedException(
            DataMigrationCodes.MissingCapability,
            $"relational dialect '{dialect.ProviderName}' provides no data-migration ledger, " +
            "so an applied data migration cannot be recorded and replays cannot be made idempotent.");

    private static string WriteInstant(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadInstant(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
