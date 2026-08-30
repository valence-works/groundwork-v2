using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using MySqlConnector;

namespace Groundwork.MySql;

/// <summary>
/// MySQL/MariaDB SQL and catalog mapping for the shared relational substrate.
/// </summary>
/// <remarks>
/// MySQL's ordinary collations are not a portable ordinal contract. Groundwork therefore emits
/// <c>utf8mb4_0900_bin</c> for no-pad ordinal declarations and uses binary expressions for comparisons and
/// ordering, while refusing the two case-folding contracts until a provider-specific, versioned
/// Unicode implementation can prove them.
/// </remarks>
public sealed class MySqlDialect : RelationalDialect
{
    public const string OrdinalCollation = "utf8mb4_0900_bin";
    internal const int QueryParameterBudget = 65_535;

    public override string ProviderName => "MySQL/MariaDB";

    public override RelationalQueryRenderer CreateQueryRenderer() => new MySqlQueryRenderer();

    public override bool CreateTableIncludesColumns => true;

    public override string RenderAggregationContains(string expression, string literal) =>
        $"JSON_CONTAINS({expression}, JSON_QUOTE({literal}))";

    public override string RenderAggregationSourceContains(string expression, string literal) =>
        $"(CHAR_LENGTH({literal}) = 0 OR INSTR(BINARY {expression}, BINARY {literal}) > 0)";

    public override string RenderAggregationSourceEndsWith(string expression, string literal) =>
        $"(CHAR_LENGTH({literal}) = 0 OR BINARY RIGHT({expression}, CHAR_LENGTH({literal})) = BINARY {literal})";

    protected override string RenderAggregationOrder(
        string expression,
        PortableType type,
        SortDirection direction)
    {
        if (type != PortableType.String)
            return base.RenderAggregationOrder(expression, type, direction);
        var descending = direction == SortDirection.Descending;
        var order = descending ? "DESC" : "ASC";
        return $"CASE WHEN {expression} IS NULL THEN {(descending ? 1 : 0)} ELSE {(descending ? 0 : 1)} END, " +
            $"HEX(CONVERT({expression} USING utf16)) {order}";
    }

    public override string RenderAggregationLiteral(object? value, PortableType type) => value switch
    {
        bool boolean => boolean ? "1" : "0",
        DateTimeOffset instant => instant.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        _ => base.RenderAggregationLiteral(value, type)
    };

    public override string QuoteIdentifier(string identifier) =>
        $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    internal static string Quote(string identifier) =>
        $"`{identifier.Replace("`", "``", StringComparison.Ordinal)}`";

    public override string MapType(ColumnDefinition definition) => definition.Type switch
    {
        PortableType.String => definition.MaxLength is { } length ? $"varchar({length})" : "longtext",
        PortableType.Int32 => "int",
        PortableType.Int64 => "bigint",
        PortableType.Decimal => $"decimal({definition.Precision ?? throw new ArgumentException($"Decimal column '{definition.Name}' requires precision.")},{definition.Scale ?? 0})",
        PortableType.Boolean => "tinyint(1)",
        // Store UTC ticks rather than a provider timestamp: MySQL/MariaDB timestamp precision and
        // timezone behavior vary by server version, while ticks are part of the portable contract.
        PortableType.DateTimeOffset => "bigint",
        PortableType.Guid => "char(36)",
        PortableType.Binary => definition.MaxLength is { } binaryLength ? $"varbinary({binaryLength})" : "longblob",
        PortableType.Json => "json",
        PortableType.Double => "double",
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Type, null)
    };

    public override string? MapGeneration(ColumnDefinition definition) =>
        definition.Generation == ColumnGeneration.ProviderSequence ? "AUTO_INCREMENT" : null;

    public override string? MapCollation(ColumnDefinition definition) => definition.Type switch
    {
        PortableType.String => definition.Collation switch
        {
            null or PortableCollation.Ordinal => OrdinalCollation,
            PortableCollation.OrdinalIgnoreCase => throw new NotSupportedException(
                "MySQL/MariaDB does not provide the portable OrdinalIgnoreCase collation."),
            PortableCollation.UnicodeOrdinalIgnoreCase => throw new NotSupportedException(
                "MySQL/MariaDB does not provide the portable UnicodeOrdinalIgnoreCase collation."),
            _ => throw new ArgumentOutOfRangeException(nameof(definition))
        },
        _ when definition.Collation is not null => throw new ArgumentException(
            $"MySQL/MariaDB collation is only valid for String column '{definition.Name}'.", nameof(definition)),
        _ => null
    };

    public override string? MapDefault(ColumnDefinition definition)
    {
        if (definition.Default is null)
            return null;
        var value = definition.Default.Value;
        return definition.Type switch
        {
            PortableType.String => Utf8Expression(Convert.ToString(value, CultureInfo.InvariantCulture)!),
            PortableType.Json => Utf8Expression(JsonText(value)),
            PortableType.Binary => BinaryExpression((byte[])value!),
            _ => Literal(value, definition.Type)
        };
    }

    public override string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey) =>
        $"CREATE TABLE IF NOT EXISTS {QuoteIdentifier(table)} ({string.Join(", ", columns)}" +
        (primaryKey.Count == 0
            ? ") ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4;"
            : $", PRIMARY KEY ({string.Join(", ", primaryKey.Select(QuoteIdentifier))})) " +
              "ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4;");

    public override string CreateTableSql(
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> primaryKey,
        string? providerSequenceColumn)
    {
        if (providerSequenceColumn is null)
            return CreateTableSql(table, columns, primaryKey);
        if (primaryKey.Count != 1 || !string.Equals(primaryKey[0], providerSequenceColumn, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "MySQL/MariaDB AUTO_INCREMENT requires the generated column to be the sole primary-key column.");
        return CreateTableSql(table, columns, primaryKey);
    }

    public override string AddColumnSql(string table, string column, string definition)
    {
        var staged = definition.Replace(" NOT NULL DEFAULT", " NULL DEFAULT", StringComparison.Ordinal);
        if (staged.EndsWith(" NOT NULL", StringComparison.Ordinal))
            staged = staged[..^" NOT NULL".Length] + " NULL";
        return $"ALTER TABLE {QuoteIdentifier(table)} ADD COLUMN {staged};";
    }

    public override string FinalizeColumnSql(string table, string column, ColumnDefinition definition)
    {
        var declaration = $"{QuoteIdentifier(column)} {MapType(definition)}";
        if (MapGeneration(definition) is { } generation)
            declaration += " " + generation;
        if (MapCollation(definition) is { } collation)
            declaration += " COLLATE " + collation;
        declaration += definition.IsNullable ? " NULL" : " NOT NULL";
        if (MapDefault(definition) is { } value)
            declaration += " DEFAULT " + value;
        return $"ALTER TABLE {QuoteIdentifier(table)} MODIFY COLUMN {declaration};";
    }

    public override string CreateIndexSql(string table, IndexDefinition index, string? filter)
    {
        if (filter is not null)
            throw new NotSupportedException("MySQL/MariaDB has no portable partial-index predicate.");
        var unique = index.IsUnique ? "UNIQUE " : string.Empty;
        var columns = string.Join(", ", index.Columns.Select(column =>
            $"{QuoteIdentifier(column.Column)} {(column.Direction == SortDirection.Ascending ? "ASC" : "DESC")}"));
        return $"CREATE {unique}INDEX {QuoteIdentifier(PhysicalIndexName(table, index.Name))} ON {QuoteIdentifier(table)} ({columns});";
    }

    public override string? IndexFilter(IndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(index);
        // MySQL unique indexes already allow repeated keys whenever any indexed column is NULL,
        // which is the portable Excluded contract without a physical partial-index predicate.
        return null;
    }

    public override string DropIndexSql(string table, string index) =>
        $"DROP INDEX {QuoteIdentifier(PhysicalIndexName(table, index))} ON {QuoteIdentifier(table)};";

    public override string DropTableSql(string table) =>
        $"DROP TABLE IF EXISTS {QuoteIdentifier(table)};";

    public override void RenameIndex(
        DbConnection connection,
        DbTransaction transaction,
        string fromTable,
        string toTable,
        IndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var from = PhysicalIndexName(fromTable, index.Name);
        var to = PhysicalIndexName(toTable, index.Name);
        if (ReadIndexByName(connection, transaction, toTable, to) is not null)
            return;
        using var command = Command(
            connection,
            transaction,
            $"ALTER TABLE {QuoteIdentifier(toTable)} RENAME INDEX {QuoteIdentifier(from)} TO {QuoteIdentifier(to)};");
        command.ExecuteNonQuery();
    }

    public override string ConditionalUpsertSql(RelationalWriteShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var columns = string.Join(", ", shape.Columns.Select(column => QuoteIdentifier(column.Name)));
        var values = string.Join(", ", shape.Columns.Select(column => "@" + column.ParameterName));
        var updates = shape.UpdateColumns.Count == 0
            ? $" ON DUPLICATE KEY UPDATE {QuoteIdentifier(shape.KeyColumns[0])}={QuoteIdentifier(shape.KeyColumns[0])}"
            : " ON DUPLICATE KEY UPDATE " + string.Join(", ", shape.UpdateColumns.Select(column =>
                $"{QuoteIdentifier(column)}=VALUES({QuoteIdentifier(column)})"));
        return $"INSERT INTO {QuoteIdentifier(shape.Table)} ({columns}) VALUES ({values}){updates};";
    }

    public override string BatchInsertSql(RelationalWriteShape shape, int batchSize)
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        var columns = string.Join(", ", shape.Columns.Select(column => QuoteIdentifier(column.Name)));
        return $"INSERT INTO {QuoteIdentifier(shape.Table)} ({columns}) VALUES " +
            string.Join(", ", Enumerable.Range(0, batchSize).Select(row =>
                $"({string.Join(", ", shape.Columns.Select(column => $"@{column.ParameterName}_{row}"))})")) + ";";
    }

    public override object? ConvertValue(object? value, ColumnDefinition definition) => value switch
    {
        null => DBNull.Value,
        DateTimeOffset timestamp => timestamp.ToUniversalTime().Ticks,
        bool boolean => boolean ? 1 : 0,
        Guid guid => guid.ToString("D"),
        byte[] bytes => bytes.ToArray(),
        JsonDocument document => document.RootElement.GetRawText(),
        JsonElement element => element.GetRawText(),
        // The closed portable JSON forms (string, JsonDocument and JsonElement) are handled
        // above. An arbitrary CLR graph needs generated metadata; this substrate deliberately
        // refuses it rather than introducing reflection into an AOT-compatible provider.
        _ when definition.Type == PortableType.Json => throw new NotSupportedException(
            "MySQL/MariaDB JSON values must be supplied as a string, JsonDocument, or JsonElement."),
        _ => value
    };

    public override object? ReadValue(object? value, ColumnDefinition definition) =>
        value is null ? null : ReadPortableValue(value, definition);

    public static object? ReadPortableValue(object value, ColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (value is DBNull)
            return null;
        return definition.Type switch
        {
            PortableType.Boolean => Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0,
            PortableType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            PortableType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            PortableType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            PortableType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            PortableType.DateTimeOffset => new DateTimeOffset(Convert.ToInt64(value, CultureInfo.InvariantCulture), TimeSpan.Zero),
            PortableType.Guid => value is Guid guid ? guid : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
            PortableType.Binary when value is byte[] bytes => bytes.ToArray(),
            PortableType.Json when value is string json => JsonDocument.Parse(json).RootElement.Clone(),
            PortableType.Json when value is JsonDocument document => document.RootElement.Clone(),
            PortableType.Json when value is JsonElement element => element.Clone(),
            _ => value
        };
    }

    public override void Validate(ColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.MaxLength is <= 0 || definition.Precision is <= 0 or > 65 || definition.Scale is < 0 or > 30 ||
            definition.Precision is not null && definition.Scale is not null && definition.Scale > definition.Precision)
            throw new ArgumentException($"Invalid MySQL/MariaDB declaration metadata for column '{definition.Name}'.", nameof(definition));
        if (definition.Type == PortableType.Decimal && definition.Precision is null)
            throw new ArgumentException($"Decimal column '{definition.Name}' requires precision.", nameof(definition));
        if (definition.Generation == ColumnGeneration.ProviderSequence && definition.Type is not (PortableType.Int32 or PortableType.Int64))
            throw new ArgumentException("MySQL/MariaDB AUTO_INCREMENT requires an Int32 or Int64 column.", nameof(definition));
    }

    public override bool TryMapUniqueViolation(DbException exception, out string indexName)
    {
        if (exception is MySqlException mysql && mysql.ErrorCode is MySqlErrorCode.DuplicateKeyEntry or MySqlErrorCode.DuplicateKey)
        {
            indexName = ExtractDuplicateIndex(mysql.Message);
            return true;
        }
        indexName = string.Empty;
        return false;
    }

    public override void AcquireApplicationLock(DbConnection connection, string resource)
    {
        using var command = Command(connection, null, "SELECT GET_LOCK(@resource, 2147483647);");
        Add(command, "resource", NormalizeLockResource(resource));
        var result = command.ExecuteScalar();
        if (result is null || Convert.ToInt32(result, CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException($"MySQL/MariaDB could not acquire application lock '{resource}'.");
    }

    public override void ReleaseApplicationLock(DbConnection connection, string resource)
    {
        using var command = Command(connection, null, "SELECT RELEASE_LOCK(@resource);");
        Add(command, "resource", NormalizeLockResource(resource));
        _ = command.ExecuteScalar();
    }

    public override bool VerifyApplicationLock(DbConnection connection, string resource)
    {
        using var command = Command(connection, null, "SELECT IS_USED_LOCK(@resource) = CONNECTION_ID();");
        Add(command, "resource", NormalizeLockResource(resource));
        return Convert.ToBoolean(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public override long ReadServerSessionId(DbConnection connection)
    {
        using var command = Command(connection, null, "SELECT CONNECTION_ID();");
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public override long AcquireFence(DbConnection connection, PhysicalSchemaTargetIdentity target, string owner)
    {
        using var command = Command(connection, null, """
            INSERT INTO `__groundwork_schema_fences` (`subject_id`,`provider_name`,`fence`,`owner`)
            VALUES (@subject,@provider,1,@owner)
            ON DUPLICATE KEY UPDATE `fence`=`fence`+1, `owner`=VALUES(`owner`);
            """);
        Add(command, "subject", target.SubjectId.Value);
        Add(command, "provider", target.ProviderName);
        Add(command, "owner", owner);
        command.ExecuteNonQuery();
        using var read = Command(connection, null,
            "SELECT `fence` FROM `__groundwork_schema_fences` WHERE `subject_id`=@subject AND `provider_name`=@provider;");
        Add(read, "subject", target.SubjectId.Value);
        Add(read, "provider", target.ProviderName);
        return Convert.ToInt64(read.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public override void AssertFence(DbConnection connection, DbTransaction transaction, PhysicalSchemaTargetIdentity target, string owner, long fence)
    {
        using var command = Command(connection, transaction, "SELECT `fence` FROM `__groundwork_schema_fences` WHERE `subject_id`=@subject AND `provider_name`=@provider AND `owner`=@owner FOR UPDATE;");
        Add(command, "subject", target.SubjectId.Value);
        Add(command, "provider", target.ProviderName);
        Add(command, "owner", owner);
        var actual = command.ExecuteScalar();
        if (actual is null || Convert.ToInt64(actual, CultureInfo.InvariantCulture) != fence)
            throw new InvalidOperationException($"MySQL/MariaDB schema fence for '{target}' is no longer owned by this operation.");
    }

    public override DbTransaction BeginTransaction(DbConnection connection) =>
        connection.BeginTransaction(IsolationLevel.ReadCommitted);

    public override IsolationLevel TransactionIsolation => IsolationLevel.ReadCommitted;

    public override int ParameterBudget => MySqlQueryRenderer.ParameterBudget;

    public override string? DataMigrationLedgerUpsertSql =>
        "INSERT INTO `__groundwork_data_migrations` (`subject_id`,`provider_name`,`migration_id`,`unit_name`,`request_fingerprint`,`state`,`cursor`,`rows_scanned`,`rows_changed`,`batches`,`started_at`,`updated_at`,`completed_at`) VALUES (@subject,@provider,@migration,@unit,@fingerprint,@state,@cursor,@scanned,@changed,@batches,@started,@updated,@completed) " +
        "ON DUPLICATE KEY UPDATE `unit_name`=VALUES(`unit_name`),`request_fingerprint`=VALUES(`request_fingerprint`),`state`=VALUES(`state`),`cursor`=VALUES(`cursor`),`rows_scanned`=VALUES(`rows_scanned`),`rows_changed`=VALUES(`rows_changed`),`batches`=VALUES(`batches`),`updated_at`=VALUES(`updated_at`),`completed_at`=VALUES(`completed_at`);";

    public override void EnsureInfrastructure(DbConnection connection)
    {
        using (var verifyCollation = Command(
                   connection,
                   null,
                   $"SELECT _utf8mb4'a' COLLATE {OrdinalCollation} = _utf8mb4'a ' COLLATE {OrdinalCollation};"))
        {
            try
            {
                if (Convert.ToInt32(verifyCollation.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                {
                    throw new NotSupportedException(
                        $"MySQL/MariaDB collation '{OrdinalCollation}' must have NO PAD semantics. " +
                        "Use MySQL 8.0.17 or later, or a MariaDB release whose utf8mb4_0900_bin alias is NO PAD.");
                }
            }
            catch (DbException exception)
            {
                throw new NotSupportedException(
                    $"MySQL/MariaDB collation '{OrdinalCollation}' is unavailable. " +
                    "Use MySQL 8.0.17 or later, or a MariaDB release whose utf8mb4_0900_bin alias is NO PAD.",
                    exception);
            }
        }
        AcquireApplicationLock(connection, "groundwork:infrastructure");
        try
        {
            using var command = Command(connection, null, """
                CREATE TABLE IF NOT EXISTS `__groundwork_schema_history` (`subject_id` varchar(255) NOT NULL, `provider_name` varchar(128) NOT NULL, `target_fingerprint` varchar(128) NOT NULL, `state_json` longtext NOT NULL, PRIMARY KEY (`subject_id`,`provider_name`)) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin;
                CREATE TABLE IF NOT EXISTS `__groundwork_schema_fences` (`subject_id` varchar(255) NOT NULL, `provider_name` varchar(128) NOT NULL, `fence` bigint NOT NULL, `owner` varchar(128) NOT NULL, PRIMARY KEY (`subject_id`,`provider_name`)) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin;
                CREATE TABLE IF NOT EXISTS `__groundwork_search_key_algorithms` (`table_name` varchar(255) NOT NULL, `column_name` varchar(255) NOT NULL, `algorithm_id` varchar(512) NOT NULL, PRIMARY KEY (`table_name`,`column_name`)) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin;
                CREATE TABLE IF NOT EXISTS `__groundwork_data_migrations` (`subject_id` varchar(255) NOT NULL, `provider_name` varchar(128) NOT NULL, `migration_id` varchar(255) NOT NULL, `unit_name` varchar(255) NOT NULL, `request_fingerprint` varchar(128) NOT NULL, `state` varchar(16) NOT NULL, `cursor` longtext NULL, `rows_scanned` bigint NOT NULL, `rows_changed` bigint NOT NULL, `batches` int NOT NULL, `started_at` varchar(40) NOT NULL, `updated_at` varchar(40) NOT NULL, `completed_at` varchar(40) NULL, PRIMARY KEY (`subject_id`,`provider_name`,`migration_id`)) ENGINE=InnoDB DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_bin;
                """);
            command.ExecuteNonQuery();
        }
        finally
        {
            ReleaseApplicationLock(connection, "groundwork:infrastructure");
        }
    }

    protected override bool IsSchemaOperationSatisfied(
        DbConnection connection,
        DbTransaction transaction,
        PhysicalSchemaOperation operation) => operation switch
        {
            CreatePrimaryStorageOperation create => TableExists(connection, transaction, create.Subject.Name),
            AddColumnOperation add => ReadColumns(connection, transaction, add.Subject.Name).ContainsKey(add.Column.Name),
            CreatePhysicalIndexOperation createIndex =>
                ReadIndex(connection, transaction, createIndex.Subject.Name, createIndex.Index.Name) is not null,
            DropPhysicalIndexOperation dropIndex =>
                ReadIndex(connection, transaction, dropIndex.Subject.Name, dropIndex.Index.Name) is null,
            RenameColumnOperation renameColumn => ColumnRenameIsSatisfied(connection, transaction, renameColumn),
            DropColumnOperation drop =>
                !ReadColumns(connection, transaction, drop.Subject.Name).ContainsKey(drop.Column.Name),
            _ => false
        };

    public override void RenameTable(
        DbConnection connection,
        DbTransaction transaction,
        string table,
        string renamed)
    {
        if (!TableExists(connection, transaction, table) && TableExists(connection, transaction, renamed))
            return;
        base.RenameTable(connection, transaction, table, renamed);
    }

    public override void ApplyProviderDefinition(
        DbConnection connection,
        DbTransaction transaction,
        ProviderPhysicalSchemaDefinition definition)
    {
        if (!string.Equals(definition.Kind, SearchKeyDefinitionKind, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported MySQL/MariaDB provider definition '{definition.Kind}'.");
        RelationalSearchKeyCatalog.Apply(
            connection,
            transaction,
            definition,
            "INSERT INTO `__groundwork_search_key_algorithms` (`table_name`,`column_name`,`algorithm_id`) VALUES (@table,@column,@algorithm) ON DUPLICATE KEY UPDATE `algorithm_id`=VALUES(`algorithm_id`);");
    }

    public override void DropProviderDefinition(
        DbConnection connection,
        DbTransaction transaction,
        ProviderPhysicalSchemaDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.Kind, SearchKeyDefinitionKind, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported MySQL/MariaDB provider definition '{definition.Kind}'.");
        RelationalSearchKeyCatalog.Drop(
            connection,
            transaction,
            definition,
            "DELETE FROM `__groundwork_search_key_algorithms` WHERE `table_name`=@table AND `column_name`=@column;");
    }

    public override IReadOnlyDictionary<string, string> ReadDerivedSearchKeyAlgorithms(
        DbConnection connection,
        DbTransaction? transaction,
        string table)
        => RelationalSearchKeyCatalog.Read(
            connection,
            transaction,
            table,
            "SELECT `column_name`,`algorithm_id` FROM `__groundwork_search_key_algorithms` WHERE `table_name`=@table;");

    public override PhysicalSchemaHistoryState ReadHistory(DbConnection connection, PhysicalSchemaTargetIdentity target)
    {
        using var command = Command(connection, null, "SELECT `state_json` FROM `__groundwork_schema_history` WHERE `subject_id`=@subject AND `provider_name`=@provider;");
        Add(command, "subject", target.SubjectId.Value);
        Add(command, "provider", target.ProviderName);
        var json = command.ExecuteScalar() as string;
        return json is null ? PhysicalSchemaHistoryState.Empty : PhysicalSchemaHistoryState.FromApplied(PhysicalSchemaAppliedStateSerializer.Deserialize(json));
    }

    public override void PublishHistory(DbConnection connection, DbTransaction transaction, PhysicalSchemaTargetIdentity target, PhysicalSchemaAppliedState state, string? expectedAppliedTargetFingerprint, string owner, long fence)
    {
        AssertFence(connection, transaction, target, owner, fence);
        using var read = Command(connection, transaction, "SELECT `target_fingerprint` FROM `__groundwork_schema_history` WHERE `subject_id`=@subject AND `provider_name`=@provider FOR UPDATE;");
        Add(read, "subject", target.SubjectId.Value);
        Add(read, "provider", target.ProviderName);
        var actual = read.ExecuteScalar() as string;
        if (!string.Equals(actual, expectedAppliedTargetFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException($"MySQL/MariaDB schema history CAS failed for '{target}'.");

        using var command = Command(connection, transaction, actual is null
            ? "INSERT INTO `__groundwork_schema_history` (`subject_id`,`provider_name`,`target_fingerprint`,`state_json`) VALUES (@subject,@provider,@fingerprint,@json);"
            : "UPDATE `__groundwork_schema_history` SET `target_fingerprint`=@fingerprint,`state_json`=@json WHERE `subject_id`=@subject AND `provider_name`=@provider;");
        Add(command, "subject", target.SubjectId.Value);
        Add(command, "provider", target.ProviderName);
        Add(command, "fingerprint", state.TargetFingerprint);
        Add(command, "json", PhysicalSchemaAppliedStateSerializer.Serialize(state));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"MySQL/MariaDB schema history publish affected an unexpected number of rows for '{target}'.");
    }

    public override bool TableExists(DbConnection connection, DbTransaction? transaction, string table)
    {
        using var command = Command(connection, transaction, "SELECT 1 FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@table LIMIT 1;");
        Add(command, "table", table);
        return command.ExecuteScalar() is not null;
    }

    public override IReadOnlyDictionary<string, RelationalColumnMetadata> ReadColumns(DbConnection connection, DbTransaction? transaction, string table)
    {
        var jsonAliases = ReadJsonAliasColumns(connection, transaction, table);
        using var command = Command(connection, transaction, """
            SELECT c.COLUMN_NAME,c.COLUMN_TYPE,c.IS_NULLABLE,c.COLUMN_DEFAULT,c.COLLATION_NAME,
                   COALESCE(k.ORDINAL_POSITION,0),IF(c.EXTRA LIKE '%auto_increment%',1,0)
            FROM information_schema.COLUMNS c
            LEFT JOIN information_schema.KEY_COLUMN_USAGE k ON k.TABLE_SCHEMA=c.TABLE_SCHEMA AND k.TABLE_NAME=c.TABLE_NAME AND k.COLUMN_NAME=c.COLUMN_NAME AND k.CONSTRAINT_NAME='PRIMARY'
            WHERE c.TABLE_SCHEMA=DATABASE() AND c.TABLE_NAME=@table ORDER BY c.ORDINAL_POSITION;
            """);
        Add(command, "table", table);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, RelationalColumnMetadata>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var isJsonAlias = jsonAliases.Contains(name);
            var storeType = NormalizeStoreType(reader.GetString(1), isJsonAlias);
            var rawDefault = reader.IsDBNull(3) ? null : reader.GetValue(3);
            result[name] = new(
                name, storeType, string.Equals(reader.GetString(2), "YES", StringComparison.Ordinal),
                rawDefault is null || string.Equals(Convert.ToString(rawDefault, CultureInfo.InvariantCulture), "NULL", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : NormalizeCatalogDefault(rawDefault, storeType),
                isJsonAlias || reader.IsDBNull(4) ? null : reader.GetString(4), Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture),
                Generation: Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture) == 1 ? ColumnGeneration.ProviderSequence : ColumnGeneration.Supplied);
        }
        return result;
    }

    public override RelationalIndexMetadata? ReadIndex(DbConnection connection, DbTransaction? transaction, string table, string index)
    {
        var physical = PhysicalIndexName(table, index);
        var result = ReadIndexByName(connection, transaction, table, physical);
        return result ?? (physical == index ? null : ReadIndexByName(connection, transaction, table, index));
    }

    public override RelationalConstraintMetadata? ReadConstraint(DbConnection connection, DbTransaction? transaction, string table, string constraint)
    {
        using (var check = Command(connection, transaction, """
            SELECT cc.CHECK_CLAUSE
            FROM information_schema.TABLE_CONSTRAINTS tc
            JOIN information_schema.CHECK_CONSTRAINTS cc
              ON cc.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA AND cc.CONSTRAINT_NAME=tc.CONSTRAINT_NAME
            WHERE tc.CONSTRAINT_SCHEMA=DATABASE() AND tc.TABLE_NAME=@table
              AND tc.CONSTRAINT_NAME=@constraint AND tc.CONSTRAINT_TYPE='CHECK'
            LIMIT 1;
            """))
        {
            Add(check, "table", table);
            Add(check, "constraint", constraint);
            if (check.ExecuteScalar() is string expression)
                return new RelationalConstraintMetadata(RelationalConstraintKind.Check, [], checkExpression: expression);
        }

        using var command = Command(connection, transaction, """
            SELECT k.COLUMN_NAME,k.REFERENCED_TABLE_NAME,k.REFERENCED_COLUMN_NAME
            FROM information_schema.KEY_COLUMN_USAGE k
            WHERE k.TABLE_SCHEMA=DATABASE() AND k.TABLE_NAME=@table AND k.CONSTRAINT_NAME=@constraint
              AND k.REFERENCED_TABLE_NAME IS NOT NULL ORDER BY k.ORDINAL_POSITION;
            """);
        Add(command, "table", table);
        Add(command, "constraint", constraint);
        using var reader = command.ExecuteReader();
        var source = new List<string>();
        var target = new List<string>();
        string? targetTable = null;
        while (reader.Read())
        {
            source.Add(reader.GetString(0));
            targetTable ??= reader.GetString(1);
            target.Add(reader.GetString(2));
        }
        return targetTable is null ? null : new RelationalConstraintMetadata(RelationalConstraintKind.ForeignKey, source, targetTable, target);
    }

    public override string? BackfillColumnSql(string table, ColumnDefinition column) =>
        column.Default is null ? null : $"UPDATE {QuoteIdentifier(table)} SET {QuoteIdentifier(column.Name)}={MapDefault(column)} WHERE {QuoteIdentifier(column.Name)} IS NULL;";

    private RelationalIndexMetadata? ReadIndexByName(DbConnection connection, DbTransaction? transaction, string table, string index)
    {
        using var command = Command(connection, transaction, """
            SELECT s.NON_UNIQUE,s.COLUMN_NAME,s.COLLATION
            FROM information_schema.STATISTICS s
            WHERE s.TABLE_SCHEMA=DATABASE() AND s.TABLE_NAME=@table AND s.INDEX_NAME=@index AND s.SEQ_IN_INDEX>0
            ORDER BY s.SEQ_IN_INDEX;
            """);
        Add(command, "table", table);
        Add(command, "index", index);
        using var reader = command.ExecuteReader();
        var columns = new List<RelationalIndexColumnMetadata>();
        var found = false;
        var unique = false;
        while (reader.Read())
        {
            found = true;
            unique = Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture) == 0;
            columns.Add(new(reader.GetString(1), string.Equals(reader.GetString(2), "D", StringComparison.Ordinal) ? SortDirection.Descending : SortDirection.Ascending));
        }
        return found ? new RelationalIndexMetadata(unique, columns, null) : null;
    }

    private static IReadOnlySet<string> ReadJsonAliasColumns(
        DbConnection connection,
        DbTransaction? transaction,
        string table)
    {
        using var command = Command(connection, transaction, """
            SELECT cc.CHECK_CLAUSE
            FROM information_schema.TABLE_CONSTRAINTS tc
            JOIN information_schema.CHECK_CONSTRAINTS cc
              ON cc.CONSTRAINT_SCHEMA=tc.CONSTRAINT_SCHEMA AND cc.CONSTRAINT_NAME=tc.CONSTRAINT_NAME
            WHERE tc.CONSTRAINT_SCHEMA=DATABASE() AND tc.TABLE_NAME=@table
              AND tc.CONSTRAINT_TYPE='CHECK';
            """);
        Add(command, "table", table);
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var expression = reader.GetString(0).Trim();
            const string prefix = "json_valid(`";
            if (!expression.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !expression.EndsWith("`)", StringComparison.Ordinal))
                continue;
            var name = expression[prefix.Length..^2].Replace("``", "`", StringComparison.Ordinal);
            if (name.Length != 0)
                result.Add(name);
        }
        return result;
    }

    private static string NormalizeStoreType(string storeType, bool isJsonAlias)
    {
        if (isJsonAlias)
            return "json";
        return storeType.ToLowerInvariant() switch
        {
            "int(11)" => "int",
            "bigint(20)" => "bigint",
            _ => storeType
        };
    }

    private bool ColumnRenameIsSatisfied(
        DbConnection connection,
        DbTransaction transaction,
        RenameColumnOperation operation)
    {
        var columns = ReadColumns(connection, transaction, operation.Subject.Name);
        return !columns.ContainsKey(operation.FromName) && columns.ContainsKey(operation.ToName);
    }

    private static string Literal(object? value, PortableType type)
    {
        return (value, type) switch
        {
            (null, _) => "NULL",
            (_, PortableType.Int32 or PortableType.Int64) => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            (_, PortableType.Decimal) => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture),
            (_, PortableType.Boolean) => value is bool boolean && boolean ? "1" : "0",
            (_, PortableType.DateTimeOffset) => value is DateTimeOffset instant ? instant.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture)!,
            (_, PortableType.Guid) => "'" + Escape(Convert.ToString(value, CultureInfo.InvariantCulture)!) + "'",
            (_, PortableType.Double) => PortableDouble.ToLiteral(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string Utf8Expression(string value) =>
        $"(convert({HexLiteral(Encoding.UTF8.GetBytes(value))} using utf8mb4))";

    private static string BinaryExpression(byte[] value) => $"({HexLiteral(value)})";

    private static string HexLiteral(byte[] value) => value.Length == 0
        ? "x''"
        : "0x" + Convert.ToHexString(value).ToLowerInvariant();

    private static string JsonText(object? value) => value switch
    {
        string text => text,
        JsonDocument document => document.RootElement.GetRawText(),
        JsonElement element => element.GetRawText(),
        _ => throw new NotSupportedException(
            "MySQL/MariaDB JSON defaults must be supplied as a string, JsonDocument, or JsonElement.")
    };

    private static string NormalizeCatalogDefault(object value, string storeType)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (text.StartsWith("convert(", StringComparison.OrdinalIgnoreCase))
            return "(" + text.Replace("\\'", "'", StringComparison.Ordinal).ToLowerInvariant() + ")";
        if ((storeType.StartsWith("varbinary(", StringComparison.OrdinalIgnoreCase) ||
             storeType.EndsWith("blob", StringComparison.OrdinalIgnoreCase)) &&
            (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("X'", StringComparison.OrdinalIgnoreCase)))
            return "(" + text.ToLowerInvariant() + ")";
        return storeType.StartsWith("varchar(", StringComparison.OrdinalIgnoreCase) ||
               storeType.StartsWith("char(", StringComparison.OrdinalIgnoreCase) ||
               storeType.EndsWith("text", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(storeType, "json", StringComparison.OrdinalIgnoreCase)
            ? "'" + Escape(text) + "'"
            : text;
    }

    private static string ExtractDuplicateIndex(string message)
    {
        const string marker = "for key ";
        var start = message.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;
        var value = message[(start + marker.Length)..].Trim();
        if (value.Length >= 2 && ((value[0] == '\'' && value[^1] == '\'') || (value[0] == '`' && value[^1] == '`')))
            value = value[1..^1];
        return value;
    }

    private static string NormalizeLockResource(string resource)
    {
        if (resource.Length <= 64)
            return resource;
        return "groundwork:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource)))[..48].ToLowerInvariant();
    }

    internal static string PhysicalIndexName(string table, string index)
    {
        var logical = $"__groundwork_ix_{table.Length}_{table}_{index.Length}_{index}";
        if (logical.Length <= 64)
            return logical;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(logical)))[..10].ToLowerInvariant();
        return logical[..(64 - hash.Length - 1)] + "_" + hash;
    }

    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@" + name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
