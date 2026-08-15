using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;

namespace Groundwork.SqlServer;

internal sealed class SqlServerDialect : RelationalDialect
{
    public override string ProviderName => "SQLServer";
    public override bool CreateTableIncludesColumns => true;

    public override string QuoteIdentifier(string identifier) => SqlServerProviderConnection.QuoteIdentifier(identifier);

    public override string MapType(ColumnDefinition definition) => definition.Type switch
    {
        PortableType.String or PortableType.Json => definition.MaxLength is { } length ? $"nvarchar({length})" : "nvarchar(max)",
        PortableType.Int32 => "int",
        PortableType.Int64 => "bigint",
        PortableType.Decimal => $"decimal({definition.Precision ?? 38},{definition.Scale ?? 0})",
        PortableType.Boolean => "bit",
        PortableType.DateTimeOffset => "datetimeoffset(7)",
        PortableType.Guid => "uniqueidentifier",
        PortableType.Binary => definition.MaxLength is { } binaryLength ? $"varbinary({binaryLength})" : "varbinary(max)",
        _ => throw new ArgumentOutOfRangeException(nameof(definition))
    };

    public override string? MapCollation(ColumnDefinition definition) => definition.Type is PortableType.String or PortableType.Json
        ? definition.Collation switch
        {
            null or PortableCollation.Ordinal => "Latin1_General_100_BIN2",
            _ => throw new NotSupportedException(
                $"SQL Server requires a persisted/versioned search-key projection for {definition.Collation}.")
        }
        : null;

    public override string? MapDefault(ColumnDefinition definition) => definition.Default is null
        ? null
        : SqlLiteral(definition.Default.Value, definition.Type);

    public override void ApplyProviderDefinition(
        DbConnection connection,
        DbTransaction transaction,
        ProviderPhysicalSchemaDefinition definition)
    {
        if (!string.Equals(definition.Kind, SqlServerSchemaCoordinator.BatchTypeKind, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported SQL Server provider definition '{definition.Kind}'.");

        using var document = JsonDocument.Parse(definition.CanonicalDefinition);
        var columns = document.RootElement.EnumerateArray().Select(element => new ColumnDefinition
        {
            Name = element.GetProperty("Name").GetString()!,
            Type = (PortableType)element.GetProperty("Type").GetInt32(),
            MaxLength = ReadNullableInt(element, "MaxLength"),
            Precision = ReadNullableInt(element, "Precision"),
            Scale = ReadNullableInt(element, "Scale")
        }).ToArray();
        if (columns.Length == 0)
            throw new InvalidOperationException("A SQL Server batch table type must declare at least one column.");

        var typeName = $"[dbo].{SqlServerProviderConnection.QuoteIdentifier(definition.SubjectIdentity)}";
        var body = string.Join(", ", columns.Select(column =>
            $"{SqlServerProviderConnection.QuoteIdentifier(column.Name)} {MapType(column)} NULL"));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"IF TYPE_ID(N'dbo.{definition.SubjectIdentity.Replace("'", "''", StringComparison.Ordinal)}') IS NOT NULL DROP TYPE {typeName}; CREATE TYPE {typeName} AS TABLE ({body});";
        command.ExecuteNonQuery();
    }

    private static int? ReadNullableInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetInt32()
            : null;

    public override string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey)
    {
        var body = string.Join(", ", columns);
        if (primaryKey.Count > 0)
        {
            var name = SqlServerPhysicalName.Normalize("__groundwork_pk_" + table);
            body += $", CONSTRAINT {QuoteIdentifier(name)} PRIMARY KEY NONCLUSTERED ({string.Join(", ", primaryKey.Select(QuoteIdentifier))})";
        }
        return $"CREATE TABLE {QuoteIdentifier(table)} ({body});";
    }

    public override string AddColumnSql(string table, string column, string definition) =>
        $"ALTER TABLE {QuoteIdentifier(table)} ADD {definition};";

    public override string FinalizeColumnSql(string table, string column, ColumnDefinition definition) =>
        $"ALTER TABLE {QuoteIdentifier(table)} ALTER COLUMN {QuoteIdentifier(column)} {ColumnDefinitionTail(definition)};";

    public override DbTransaction BeginTransaction(DbConnection connection) =>
        ((SqlConnection)connection).BeginTransaction(IsolationLevel.Serializable);

    public override string CreateIndexSql(string table, IndexDefinition index, string? filter)
    {
        var unique = index.IsUnique ? "UNIQUE " : string.Empty;
        var name = PhysicalIndexName(table, index.Name);
        var columns = string.Join(", ", index.Columns.Select(column =>
            $"{QuoteIdentifier(column.Column)} {(column.Direction == SortDirection.Ascending ? "ASC" : "DESC")}"));
        return $"CREATE {unique}NONCLUSTERED INDEX {QuoteIdentifier(name)} ON {QuoteIdentifier(table)} ({columns})" +
               (filter is null ? ";" : $" WHERE {filter};");
    }

    public override string DropIndexSql(string table, string index) =>
        $"DROP INDEX {QuoteIdentifier(PhysicalIndexName(table, index))} ON {QuoteIdentifier(table)};";

    public override string ConditionalUpsertSql(RelationalWriteShape shape)
    {
        var keys = string.Join(" AND ", shape.KeyColumns.Select(column =>
            $"target.{QuoteIdentifier(column)}=@{column}"));
        var updates = string.Join(", ", shape.UpdateColumns.Select(column =>
            $"{QuoteIdentifier(column)}=@{column}"));
        var insertColumns = string.Join(", ", shape.Columns.Select(column => QuoteIdentifier(column.Name)));
        var insertValues = string.Join(", ", shape.Columns.Select(column => "@" + column.ParameterName));
        if (updates.Length == 0)
            return $"IF NOT EXISTS (SELECT 1 FROM {QuoteIdentifier(shape.Table)} AS target WITH (UPDLOCK, SERIALIZABLE) WHERE {keys}) INSERT INTO {QuoteIdentifier(shape.Table)} ({insertColumns}) VALUES ({insertValues});";
        var update = $"UPDATE {QuoteIdentifier(shape.Table)} SET {updates} WHERE {string.Join(" AND ", shape.KeyColumns.Select(column => $"{QuoteIdentifier(column)}=@{column}"))};";
        return $"IF EXISTS (SELECT 1 FROM {QuoteIdentifier(shape.Table)} AS target WITH (UPDLOCK, SERIALIZABLE) WHERE {keys}) {update} ELSE INSERT INTO {QuoteIdentifier(shape.Table)} ({insertColumns}) VALUES ({insertValues});";
    }

    public override string BatchInsertSql(RelationalWriteShape shape, int batchSize) =>
        $"INSERT INTO {QuoteIdentifier(shape.Table)} ({string.Join(", ", shape.Columns.Select(column => QuoteIdentifier(column.Name)))}) VALUES " +
        string.Join(", ", Enumerable.Range(0, batchSize).Select(row =>
            $"({string.Join(", ", shape.Columns.Select(column => $"@{column.ParameterName}_{row}"))})")) + ";";

    public override object? ConvertValue(object? value, ColumnDefinition definition) =>
        SqlServerProviderConnection.ToSqlServerValue(value, definition);

    public override void Validate(ColumnDefinition definition)
    {
        if (definition.MaxLength is <= 0 || definition.MaxLength is > 8000 && definition.Type == PortableType.Binary ||
            definition.Type is PortableType.String or PortableType.Json && definition.MaxLength is > 4000 ||
            definition.Precision is <= 0 or > 38 || definition.Scale is < 0 ||
            definition.Precision is not null && definition.Scale is not null && definition.Scale > definition.Precision)
            throw new ArgumentException($"Invalid SQL Server declaration metadata for column '{definition.Name}'.", nameof(definition));
        if (definition.Type == PortableType.Decimal && definition.Precision is null)
            throw new ArgumentException($"Decimal column '{definition.Name}' requires precision.", nameof(definition));
        _ = MapType(definition);
        _ = MapCollation(definition);
        _ = MapDefault(definition);
    }

    public override bool TryMapUniqueViolation(DbException exception, out string indexName)
    {
        if (exception is SqlException sql && sql.Number is 2601 or 2627)
        {
            indexName = ExtractConstraintName(sql.Message);
            return true;
        }
        indexName = string.Empty;
        return false;
    }

    private static string ExtractConstraintName(string message)
    {
        foreach (var marker in new[] { "index '", "constraint '" })
        {
            var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                continue;
            start += marker.Length;
            var end = message.IndexOf('\'', start);
            if (end > start)
                return message[start..end];
        }
        return message;
    }

    public override void AcquireApplicationLock(DbConnection connection, string resource)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource=@resource, @LockMode='Exclusive', @LockOwner='Session', @DbPrincipal='public'; SELECT @result;";
        AddParameter(command, "@resource", resource);
        var result = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (result < 0)
            throw new InvalidOperationException($"SQL Server could not acquire application lock '{resource}' (result {result}).");
    }

    public override void ReleaseApplicationLock(DbConnection connection, string resource)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_releaseapplock @Resource=@resource, @LockOwner='Session'; SELECT @result;";
        AddParameter(command, "@resource", resource);
        _ = command.ExecuteScalar();
    }

    public override bool VerifyApplicationLock(DbConnection connection, string resource)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT APPLOCK_MODE('public', @resource, 'Session');";
        AddParameter(command, "@resource", resource);
        return string.Equals(Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture), "Exclusive", StringComparison.OrdinalIgnoreCase);
    }

    public override long ReadServerSessionId(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@SPID;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public override long AcquireFence(DbConnection connection, PhysicalSchemaTargetIdentity target, string owner)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @next bigint;
            SELECT @next = fence + 1 FROM [__groundwork_schema_fences] WITH (UPDLOCK, HOLDLOCK)
              WHERE subject_id=@id AND provider_name=@provider;
            IF @next IS NULL BEGIN SET @next=1; INSERT INTO [__groundwork_schema_fences] (subject_id,provider_name,fence,owner) VALUES (@id,@provider,@next,@owner); END
            ELSE UPDATE [__groundwork_schema_fences] SET fence=@next,owner=@owner WHERE subject_id=@id AND provider_name=@provider;
            SELECT @next;
            """;
        AddParameter(command, "@id", target.SubjectId.Value);
        AddParameter(command, "@provider", target.ProviderName);
        AddParameter(command, "@owner", owner);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public override void AssertFence(DbConnection connection, DbTransaction transaction, PhysicalSchemaTargetIdentity target, string owner, long fence)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT fence FROM [__groundwork_schema_fences] WITH (UPDLOCK, HOLDLOCK) WHERE subject_id=@id AND provider_name=@provider AND owner=@owner;";
        AddParameter(command, "@id", target.SubjectId.Value);
        AddParameter(command, "@provider", target.ProviderName);
        AddParameter(command, "@owner", owner);
        var actual = command.ExecuteScalar();
        if (actual is null || Convert.ToInt64(actual, CultureInfo.InvariantCulture) != fence)
            throw new InvalidOperationException($"SQL Server schema fence for '{target}' is no longer owned by this operation.");
    }

    public override void EnsureInfrastructure(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            IF OBJECT_ID(N'[__groundwork_schema_history]', N'U') IS NULL
            CREATE TABLE [__groundwork_schema_history] (
                subject_id nvarchar(450) NOT NULL,
                provider_name nvarchar(128) NOT NULL,
                target_fingerprint nvarchar(128) NOT NULL,
                state_json nvarchar(max) NOT NULL,
                CONSTRAINT [PK___groundwork_schema_history] PRIMARY KEY NONCLUSTERED (subject_id, provider_name));
            IF OBJECT_ID(N'[__groundwork_schema_fences]', N'U') IS NULL
            CREATE TABLE [__groundwork_schema_fences] (
                subject_id nvarchar(450) NOT NULL,
                provider_name nvarchar(128) NOT NULL,
                fence bigint NOT NULL,
                owner nvarchar(64) NOT NULL,
                CONSTRAINT [PK___groundwork_schema_fences] PRIMARY KEY NONCLUSTERED (subject_id, provider_name));
            """;
        command.ExecuteNonQuery();
    }

    public override PhysicalSchemaHistoryState ReadHistory(DbConnection connection, PhysicalSchemaTargetIdentity target)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM [__groundwork_schema_history] WHERE subject_id=@id AND provider_name=@provider;";
        AddParameter(command, "@id", target.SubjectId.Value);
        AddParameter(command, "@provider", target.ProviderName);
        var json = command.ExecuteScalar() as string;
        return json is null ? PhysicalSchemaHistoryState.Empty : PhysicalSchemaHistoryState.FromApplied(PhysicalSchemaAppliedStateSerializer.Deserialize(json));
    }

    public override void PublishHistory(DbConnection connection, DbTransaction transaction, PhysicalSchemaTargetIdentity target,
        PhysicalSchemaAppliedState state, string? expectedAppliedTargetFingerprint, string owner, long fence)
    {
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT target_fingerprint FROM [__groundwork_schema_history] WITH (UPDLOCK,HOLDLOCK) WHERE subject_id=@id AND provider_name=@provider;";
        AddParameter(read, "@id", target.SubjectId.Value);
        AddParameter(read, "@provider", target.ProviderName);
        var actual = read.ExecuteScalar() as string;
        if (!string.Equals(actual, expectedAppliedTargetFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException($"SQL Server schema history CAS failed for '{target}'.");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = actual is null
            ? "INSERT INTO [__groundwork_schema_history] (subject_id,provider_name,target_fingerprint,state_json) VALUES (@id,@provider,@fingerprint,@json);"
            : "UPDATE [__groundwork_schema_history] SET target_fingerprint=@fingerprint,state_json=@json WHERE subject_id=@id AND provider_name=@provider;";
        AddParameter(command, "@id", target.SubjectId.Value);
        AddParameter(command, "@provider", target.ProviderName);
        AddParameter(command, "@fingerprint", state.TargetFingerprint);
        AddParameter(command, "@json", PhysicalSchemaAppliedStateSerializer.Serialize(state));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException($"SQL Server schema history publish affected an unexpected number of rows for '{target}'.");
    }

    public override bool TableExists(DbConnection connection, DbTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sys.tables WHERE schema_id=SCHEMA_ID(N'dbo') AND name=@name;";
        AddParameter(command, "@name", table);
        return command.ExecuteScalar() is not null;
    }

    public override IReadOnlyDictionary<string, RelationalColumnMetadata> ReadColumns(DbConnection connection, DbTransaction transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable,
                   dc.definition, c.collation_name, ISNULL(ic.key_ordinal,0), c.is_computed,
                   cc.is_persisted, cc.definition
            FROM sys.columns c JOIN sys.tables tb ON tb.object_id=c.object_id
            JOIN sys.types t ON t.user_type_id=c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id=c.object_id AND dc.parent_column_id=c.column_id
            LEFT JOIN sys.indexes pk ON pk.object_id=c.object_id AND pk.is_primary_key=1
            LEFT JOIN sys.index_columns ic ON ic.object_id=c.object_id AND ic.column_id=c.column_id AND ic.index_id=pk.index_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id=c.object_id AND cc.column_id=c.column_id
            WHERE tb.schema_id=SCHEMA_ID(N'dbo') AND tb.name=@table ORDER BY c.column_id;
            """;
        AddParameter(command, "@table", table);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, RelationalColumnMetadata>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1);
            var maxLength = reader.GetInt16(2);
            var precision = reader.GetByte(3);
            var scale = reader.GetByte(4);
            result[name] = new(name, StoreType(type, maxLength, precision, scale), reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : NormalizeDefault(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7), Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture), reader.GetBoolean(9),
                !reader.IsDBNull(10) && reader.GetBoolean(10), reader.IsDBNull(11) ? null : reader.GetString(11));
        }
        return result;
    }

    public override RelationalIndexMetadata? ReadIndex(DbConnection connection, DbTransaction transaction, string table, string index)
    {
        var physical = PhysicalIndexName(table, index);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT i.is_unique,i.filter_definition FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id WHERE t.schema_id=SCHEMA_ID(N'dbo') AND t.name=@table AND i.name=@index;";
        AddParameter(command, "@table", table);
        AddParameter(command, "@index", physical);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var unique = reader.GetBoolean(0);
        var filter = reader.IsDBNull(1) ? null : reader.GetString(1);
        reader.Close();
        using var columns = connection.CreateCommand();
        columns.Transaction = transaction;
        columns.CommandText = "SELECT c.name,ic.is_descending_key FROM sys.index_columns ic JOIN sys.tables t ON t.object_id=ic.object_id JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id JOIN sys.indexes i ON i.object_id=ic.object_id AND i.index_id=ic.index_id WHERE t.schema_id=SCHEMA_ID(N'dbo') AND t.name=@table AND i.name=@index AND ic.key_ordinal>0 ORDER BY ic.key_ordinal;";
        AddParameter(columns, "@table", table);
        AddParameter(columns, "@index", physical);
        using var columnReader = columns.ExecuteReader();
        var result = new List<RelationalIndexColumnMetadata>();
        while (columnReader.Read()) result.Add(new(columnReader.GetString(0), columnReader.GetBoolean(1) ? SortDirection.Descending : SortDirection.Ascending));
        return new RelationalIndexMetadata(unique, result, filter);
    }

    public override string? BackfillColumnSql(string table, ColumnDefinition column) =>
        column.Default is null ? null : $"UPDATE {QuoteIdentifier(table)} SET {QuoteIdentifier(column.Name)}={MapDefault(column)} WHERE {QuoteIdentifier(column.Name)} IS NULL;";

    public override void ValidateTarget(DbConnection connection, DbTransaction transaction, PhysicalSchemaTarget target) =>
        SqlServerIndexKeyBudgetValidator.Validate(target.Subject.Definition);

    internal static string PhysicalIndexName(string table, string logicalName) =>
        SqlServerPhysicalName.Normalize("__groundwork_ix_" + table + "_" + logicalName);

    private string ColumnDefinitionTail(ColumnDefinition definition) =>
        $"{MapType(definition)}" +
        (MapCollation(definition) is { } collation ? $" COLLATE {collation}" : string.Empty) +
        (definition.IsNullable ? " NULL" : " NOT NULL");

    private static string StoreType(string type, short maxLength, byte precision, byte scale) => type.ToLowerInvariant() switch
    {
        "nvarchar" or "nchar" => $"{type.ToLowerInvariant()}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})",
        "varchar" or "char" or "varbinary" or "binary" => $"{type.ToLowerInvariant()}({(maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})",
        "decimal" or "numeric" => $"decimal({precision},{scale})",
        "datetimeoffset" => $"datetimeoffset({scale})",
        _ => type.ToLowerInvariant()
    };

    private static string NormalizeDefault(string value)
    {
        var result = value.Trim();
        while (result.Length > 1 && result[0] == '(' && result[^1] == ')') result = result[1..^1].Trim();
        return result;
    }

    private static string SqlLiteral(object? value, PortableType type) => value is null ? "NULL" : type switch
    {
        PortableType.Boolean => value is bool boolean && boolean ? "1" : "0",
        PortableType.Int32 or PortableType.Int64 => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        PortableType.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture),
        PortableType.Binary => $"0x{Convert.ToHexString((byte[])value)}",
        PortableType.DateTimeOffset => $"N'{((DateTimeOffset)value).ToUniversalTime():O}'",
        PortableType.Guid => $"N'{value}'",
        _ => $"N'{(value is string text ? text : JsonSerializer.Serialize(value)).Replace("'", "''", StringComparison.Ordinal)}'"
    };

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
