using System.Data.Common;
using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;

namespace Groundwork.Sqlite;

internal sealed class SqliteDialect : RelationalDialect
{
    public override string ProviderName => "SQLite";
    public override bool CreateTableIncludesColumns => true;

    public override string RenderAggregationContains(string expression, string literal) =>
        $"EXISTS (SELECT 1 FROM json_each({expression}) WHERE value = {literal} COLLATE BINARY)";

    public override string RenderAggregationSourceContains(string expression, string literal) =>
        $"(length({literal}) = 0 OR instr({expression} COLLATE GROUNDWORK_UTF16_ORDINAL, {literal}) > 0)";

    public override string RenderAggregationSourceEndsWith(string expression, string literal) =>
        $"(length({literal}) = 0 OR substr({expression} COLLATE GROUNDWORK_UTF16_ORDINAL, -length({literal})) = {literal})";

    public override string QuoteIdentifier(string identifier) => SqliteProviderConnection.QuoteIdentifier(identifier);

    public override string MapType(ColumnDefinition definition) => definition.Type switch
    {
        PortableType.String => definition.MaxLength is { } length ? $"TEXT" : "TEXT",
        PortableType.Int32 or PortableType.Int64 or PortableType.Boolean => "INTEGER",
        PortableType.Decimal => "TEXT",
        PortableType.DateTimeOffset or PortableType.Guid or PortableType.Json => "TEXT",
        PortableType.Binary => "BLOB",
        _ => throw new ArgumentOutOfRangeException(nameof(definition))
    };

    public override string? MapCollation(ColumnDefinition definition) => definition.Collation switch
    {
        null or PortableCollation.Ordinal => "BINARY",
        PortableCollation.OrdinalIgnoreCase => "NOCASE",
        PortableCollation.UnicodeOrdinalIgnoreCase => throw new NotSupportedException(
            "SQLite does not provide the portable UnicodeOrdinalIgnoreCase collation."),
        _ => throw new ArgumentOutOfRangeException(nameof(definition))
    };

    public override string? MapDefault(ColumnDefinition definition) => definition.Default is null
        ? null
        : Literal(definition.Default.Value, definition.Type);

    public override string CreateTableSql(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> primaryKey,
        string? providerSequenceColumn)
    {
        if (providerSequenceColumn is null)
            return base.CreateTableSql(table, columns, primaryKey, providerSequenceColumn);

        if (primaryKey.Count != 1 || !string.Equals(primaryKey[0], providerSequenceColumn, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "SQLite ProviderSequence requires the generated column to be the sole primary-key column.");

        var quoted = QuoteIdentifier(providerSequenceColumn);
        var sequenceColumn = columns.SingleOrDefault(column =>
            column.StartsWith(quoted + " ", StringComparison.Ordinal));
        if (sequenceColumn is null || !sequenceColumn.Contains("INTEGER", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "SQLite ProviderSequence requires an INTEGER primary-key declaration.");

        var replacement = sequenceColumn.Replace(" NOT NULL", "", StringComparison.Ordinal) +
            " PRIMARY KEY AUTOINCREMENT";
        var body = string.Join(", ", columns.Select(column =>
            string.Equals(column, sequenceColumn, StringComparison.Ordinal) ? replacement : column));
        return $"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(table)} ({body});";
    }

    public override string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey) =>
        $"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(table)} ({string.Join(", ", columns)}" +
        (primaryKey.Count == 0 ? ")" : $", PRIMARY KEY ({string.Join(", ", primaryKey.Select(QuoteIdentifier))}))" ) + ";";

    public override string AddColumnSql(string table, string column, string definition)
    {
        // SQLite only permits a non-null ADD COLUMN when a non-null literal default exists. The
        // planner backfills the staged nullable column before FinalizeColumn rebuilds the table.
        var staged = definition.Replace(" NOT NULL", " NULL", StringComparison.Ordinal);
        return $"ALTER TABLE {QuoteIdentifier(table)} ADD COLUMN {staged};";
    }

    public override string FinalizeColumnSql(string table, string column, ColumnDefinition definition) =>
        throw new NotSupportedException("SQLite finalizes columns with a transactional table rebuild.");

    public override DbTransaction BeginTransaction(DbConnection connection) =>
        ((SqliteConnection)connection).BeginTransaction(IsolationLevel.Serializable, deferred: false);

    public override string CreateIndexSql(string table, IndexDefinition index, string? filter)
    {
        var unique = index.IsUnique ? "UNIQUE " : string.Empty;
        var columns = string.Join(", ", index.Columns.Select(column =>
            $"{QuoteIdentifier(column.Column)} {(column.Direction == SortDirection.Ascending ? "ASC" : "DESC")}"));
        return $"CREATE {unique}INDEX IF NOT EXISTS {QuoteIdentifier(PhysicalIndexName(table, index.Name))} ON {QuoteIdentifier(table)} ({columns})" +
            (filter is null ? ";" : $" WHERE {filter};");
    }

    public override string DropIndexSql(string table, string index) =>
        $"DROP INDEX IF EXISTS {QuoteIdentifier(PhysicalIndexName(table, index))};";

    public override string ConditionalUpsertSql(RelationalWriteShape shape) =>
        UpsertSql(shape);

    public override string BatchInsertSql(RelationalWriteShape shape, int batchSize) =>
        $"INSERT INTO {QuoteIdentifier(shape.Table)} ({string.Join(", ", shape.Columns.Select(column => QuoteIdentifier(column.Name)))}) VALUES " +
        string.Join(", ", Enumerable.Range(0, batchSize).Select(row =>
            $"({string.Join(", ", shape.Columns.Select(column => $"@{column.ParameterName}_{row}"))})")) + ";";

    public override object? ConvertValue(object? value, ColumnDefinition definition) =>
        SqliteProviderConnection.ToSqliteValue(value, definition);

    public override void Validate(ColumnDefinition definition)
    {
        if (definition.MaxLength is <= 0 || definition.Precision is <= 0 || definition.Scale is < 0 ||
            definition.Precision is not null && definition.Scale is not null && definition.Scale > definition.Precision)
            throw new ArgumentException($"Invalid SQLite declaration metadata for column '{definition.Name}'.", nameof(definition));
        if (definition.Type == PortableType.Decimal && definition.Precision is null)
            throw new ArgumentException($"Decimal column '{definition.Name}' requires precision.", nameof(definition));
    }

    public override bool TryMapUniqueViolation(DbException exception, out string indexName)
    {
        if (exception is SqliteException { SqliteErrorCode: 19 or 2067 or 1555 } sqlite)
        {
            indexName = sqlite.Message.Split(':').LastOrDefault()?.Trim() ?? string.Empty;
            return true;
        }

        indexName = string.Empty;
        return false;
    }

    public override void AcquireApplicationLock(DbConnection connection, string resource) { }
    public override void ReleaseApplicationLock(DbConnection connection, string resource) { }
    public override bool VerifyApplicationLock(DbConnection connection, string resource) => true;
    public override long ReadServerSessionId(DbConnection connection) => Environment.ProcessId;
    public override long AcquireFence(DbConnection connection, PhysicalSchemaTargetIdentity target, string owner) => 1;
    public override void AssertFence(DbConnection connection, DbTransaction transaction, PhysicalSchemaTargetIdentity target, string owner, long fence) { }

    public override void EnsureInfrastructure(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS "__groundwork_schema_history" (
                "subject_id" TEXT NOT NULL,
                "provider_name" TEXT NOT NULL,
                "target_fingerprint" TEXT NOT NULL,
                "state_json" TEXT NOT NULL,
                PRIMARY KEY ("subject_id", "provider_name")
            );
            CREATE TABLE IF NOT EXISTS "__groundwork_search_key_algorithms" (
                "table_name" TEXT NOT NULL,
                "column_name" TEXT NOT NULL,
                "algorithm_id" TEXT NOT NULL,
                PRIMARY KEY ("table_name", "column_name")
            );
            """;
        command.ExecuteNonQuery();
    }

    public override void ApplyProviderDefinition(
        DbConnection connection,
        DbTransaction transaction,
        ProviderPhysicalSchemaDefinition definition)
    {
        if (!string.Equals(definition.Kind, RelationalDialect.SearchKeyDefinitionKind, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported SQLite provider definition '{definition.Kind}'.");
        RelationalSearchKeyCatalog.Apply(
            connection,
            transaction,
            definition,
            "INSERT INTO \"__groundwork_search_key_algorithms\" (\"table_name\",\"column_name\",\"algorithm_id\") VALUES (@table,@column,@algorithm) ON CONFLICT (\"table_name\",\"column_name\") DO UPDATE SET \"algorithm_id\"=excluded.\"algorithm_id\";");
    }

    public override IReadOnlyDictionary<string, string> ReadDerivedSearchKeyAlgorithms(
        DbConnection connection,
        DbTransaction transaction,
        string table)
        => RelationalSearchKeyCatalog.Read(
            connection,
            transaction,
            table,
            "SELECT \"column_name\",\"algorithm_id\" FROM \"__groundwork_search_key_algorithms\" WHERE \"table_name\"=@table;");

    public override PhysicalSchemaHistoryState ReadHistory(DbConnection connection, PhysicalSchemaTargetIdentity target)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"state_json\" FROM \"__groundwork_schema_history\" WHERE \"subject_id\"=@id AND \"provider_name\"=@provider;";
        AddParameter(command, "@id", target.SubjectId.Value);
        AddParameter(command, "@provider", target.ProviderName);
        var json = command.ExecuteScalar() as string;
        return json is null ? PhysicalSchemaHistoryState.Empty : PhysicalSchemaHistoryState.FromApplied(PhysicalSchemaAppliedStateSerializer.Deserialize(json));
    }

    public override void PublishHistory(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        string owner,
        long fence)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT \"target_fingerprint\" FROM \"__groundwork_schema_history\" WHERE \"subject_id\"=@id AND \"provider_name\"=@provider;";
        AddParameter(read, "@id", target.SubjectId.Value);
        AddParameter(read, "@provider", target.ProviderName);
        var actual = read.ExecuteScalar() as string;
        if (!string.Equals(actual, expectedAppliedTargetFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException($"SQLite schema history CAS failed for '{target}'.");

        using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = actual is null
            ? "INSERT INTO \"__groundwork_schema_history\" (\"subject_id\",\"provider_name\",\"target_fingerprint\",\"state_json\") VALUES (@id,@provider,@fingerprint,@json);"
            : "UPDATE \"__groundwork_schema_history\" SET \"target_fingerprint\"=@fingerprint,\"state_json\"=@json WHERE \"subject_id\"=@id AND \"provider_name\"=@provider;";
        AddParameter(command, "@id", target.SubjectId.Value);
        AddParameter(command, "@provider", target.ProviderName);
        AddParameter(command, "@fingerprint", state.TargetFingerprint);
        AddParameter(command, "@json", PhysicalSchemaAppliedStateSerializer.Serialize(state));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"SQLite schema history publish affected an unexpected number of rows for '{target}'.");
    }

    public override bool TableExists(DbConnection connection, DbTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name;";
        AddParameter(command, "@name", table);
        return command.ExecuteScalar() is not null;
    }

    public override IReadOnlyDictionary<string, RelationalColumnMetadata> ReadColumns(DbConnection connection, DbTransaction transaction, string table)
    {
        var createSql = ReadCreateSql((SqliteConnection)connection, transaction, table);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, RelationalColumnMetadata>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var declaration = createSql is null ? null : SqliteCreateTableSql.ExtractColumnDeclaration(createSql, name);
            var providerSequence = declaration?.Contains("AUTOINCREMENT", StringComparison.OrdinalIgnoreCase) == true;
            result[name] = new(name, reader.GetString(2), !providerSequence && reader.GetInt32(3) == 0,
                reader.IsDBNull(4) ? null : reader.GetString(4), "BINARY", reader.GetInt32(5),
                Generation: providerSequence ? ColumnGeneration.ProviderSequence : ColumnGeneration.Supplied);
        }
        return result;
    }

    public override RelationalIndexMetadata? ReadIndex(DbConnection connection, DbTransaction transaction, string table, string index)
    {
        using var list = connection.CreateCommand();
        list.Transaction = transaction;
        list.CommandText = $"PRAGMA index_list({QuoteIdentifier(table)});";
        using var reader = list.ExecuteReader();
        var found = false;
        var unique = false;
        var physicalIndex = PhysicalIndexName(table, index);
        while (reader.Read())
        {
            if (!string.Equals(reader.GetString(1), physicalIndex, StringComparison.Ordinal))
                continue;
            found = true;
            unique = reader.GetInt32(2) != 0;
            break;
        }
        if (!found)
            return null;
        reader.Close();
        using var columns = connection.CreateCommand();
        columns.Transaction = transaction;
        columns.CommandText = $"PRAGMA index_xinfo({QuoteIdentifier(PhysicalIndexName(table, index))});";
        using var indexReader = columns.ExecuteReader();
        var result = new List<RelationalIndexColumnMetadata>();
        while (indexReader.Read())
        {
            if (indexReader.GetInt32(5) == 0 || indexReader.IsDBNull(2))
                continue;
            result.Add(new(indexReader.GetString(2), indexReader.GetInt32(3) == 0 ? SortDirection.Ascending : SortDirection.Descending));
        }
        return new RelationalIndexMetadata(unique, result, ReadIndexFilter((SqliteConnection)connection, (SqliteTransaction?)transaction, physicalIndex));
    }

    public override string? BackfillColumnSql(string table, ColumnDefinition column) =>
        column.Default is null ? null : $"UPDATE {QuoteIdentifier(table)} SET {QuoteIdentifier(column.Name)}={MapDefault(column)} WHERE {QuoteIdentifier(column.Name)} IS NULL;";

    public override void FinalizeColumn(DbConnection connection, DbTransaction transaction, string table, ColumnDefinition definition)
    {
        var sqlite = (SqliteConnection)connection;
        var createSql = ReadCreateSql(sqlite, (SqliteTransaction)transaction, table) ?? throw new InvalidOperationException($"SQLite table '{table}' has no CREATE SQL.");
        var indexes = ReadIndexSql(sqlite, (SqliteTransaction)transaction, table);
        var temporary = $"__groundwork_rebuild_{Guid.NewGuid():N}";
        var columnSql = ColumnDefinitionSql(definition);
        var rebuilt = SqliteCreateTableSql.ReplaceTableAndColumn(createSql, table, QuoteIdentifier(temporary), definition.Name, columnSql);
        Execute(connection, transaction, rebuilt);

        var columns = ReadColumns(connection, transaction, table).Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var names = string.Join(", ", columns.Select(QuoteIdentifier));
        Execute(connection, transaction, $"INSERT INTO {QuoteIdentifier(temporary)} ({names}) SELECT {names} FROM {QuoteIdentifier(table)};");
        Execute(connection, transaction, $"DROP TABLE {QuoteIdentifier(table)};");
        Execute(connection, transaction, $"ALTER TABLE {QuoteIdentifier(temporary)} RENAME TO {QuoteIdentifier(table)};");
        foreach (var sql in indexes)
            Execute(connection, transaction, sql);
    }

    private string ColumnDefinitionSql(ColumnDefinition column) =>
        $"{QuoteIdentifier(column.Name)} {MapType(column)}" +
        (MapCollation(column) is { } collation ? $" COLLATE {collation}" : string.Empty) +
        (column.IsNullable ? " NULL" : " NOT NULL") +
        (MapDefault(column) is { } value ? $" DEFAULT {value}" : string.Empty);

    internal static string PhysicalIndexName(string table, string logicalName) =>
        $"__groundwork_ix_{table}_{logicalName}";

    private static void Execute(DbConnection connection, DbTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? ReadCreateSql(SqliteConnection connection, DbTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@name;";
        AddParameter(command, "@name", table);
        return command.ExecuteScalar() as string;
    }

    private static IReadOnlyList<string> ReadIndexSql(SqliteConnection connection, DbTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name=@table AND sql IS NOT NULL ORDER BY name;";
        AddParameter(command, "@table", table);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0) + ";");
        return result;
    }

    private static string? ReadIndexFilter(SqliteConnection connection, SqliteTransaction? transaction, string index)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=@name;";
        AddParameter(command, "@name", index);
        var sql = command.ExecuteScalar() as string;
        if (sql is null) return null;
        var where = SqliteCreateTableSql.FindKeyword(sql, "WHERE");
        return where < 0 ? null : sql[(where + "WHERE".Length)..].Trim().TrimEnd(';').Trim();
    }

    private string UpsertSql(RelationalWriteShape shape)
    {
        var columns = string.Join(", ", shape.Columns.Select(column => QuoteIdentifier(column.Name)));
        var values = string.Join(", ", shape.Columns.Select(column => $"@{column.ParameterName}"));
        var updates = shape.UpdateColumns.Count == 0
            ? $"{QuoteIdentifier(shape.KeyColumns[0])}=excluded.{QuoteIdentifier(shape.KeyColumns[0])}"
            : string.Join(", ", shape.UpdateColumns.Select(column => $"{QuoteIdentifier(column)}=excluded.{QuoteIdentifier(column)}"));
        var keys = string.Join(", ", shape.KeyColumns.Select(QuoteIdentifier));
        return $"INSERT INTO {QuoteIdentifier(shape.Table)} ({columns}) VALUES ({values}) ON CONFLICT ({keys}) DO UPDATE SET {updates};";
    }

    private static string Literal(object? value, PortableType type) => value is null ? "NULL" : type switch
    {
        PortableType.Boolean => (value is bool boolean && boolean) ? "1" : "0",
        PortableType.Int32 or PortableType.Int64 => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        PortableType.Decimal => $"'{Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture)}'",
        PortableType.Binary => $"X'{Convert.ToHexString((byte[])value)}'",
        _ => $"'{(value is DateTimeOffset date ? date.ToUniversalTime().ToString("O") : Convert.ToString(value, CultureInfo.InvariantCulture))!.Replace("'", "''", StringComparison.Ordinal)}'"
    };

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
