using System.Text;
using Groundwork.Kernel;

namespace Groundwork.Substrate.Relational;

/// <summary>Generic DDL and DML emission driven only by kernel declarations.</summary>
public static class RelationalSql
{
    public static string CreateTable(RelationalDialect dialect, StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Key.Columns is null || unit.Key.Columns.Count == 0)
            throw new ArgumentException("A relational table requires a non-empty key.", nameof(unit));

        var columns = (unit.Columns ?? throw new ArgumentException("A relational table requires columns.", nameof(unit)))
            .Select(column => ColumnDefinitionSql(dialect, column))
            .ToArray();
        return dialect.CreateTableSql(unit.Name, columns, unit.Key.Columns);
    }

    public static string AddColumn(
        RelationalDialect dialect,
        string table,
        ColumnDefinition column)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(column);
        dialect.Validate(column);
        return dialect.AddColumnSql(table, column.Name, ColumnDefinitionSql(dialect, column));
    }

    public static string FinalizeColumn(
        RelationalDialect dialect,
        string table,
        string column)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        return dialect.FinalizeColumnSql(table, column);
    }

    public static string CreateIndex(
        RelationalDialect dialect,
        string table,
        IndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(index);
        return dialect.CreateIndexSql(table, index, dialect.IndexFilter(index));
    }

    public static string DropIndex(
        RelationalDialect dialect,
        string table,
        string index)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        return dialect.DropIndexSql(table, index);
    }

    public static string ConditionalUpsert(
        RelationalDialect dialect,
        RelationalWriteShape shape)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(shape);
        return dialect.ConditionalUpsertSql(shape);
    }

    public static string BatchInsert(
        RelationalDialect dialect,
        RelationalWriteShape shape,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(shape);
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        return dialect.BatchInsertSql(shape, batchSize);
    }

    private static string ColumnDefinitionSql(RelationalDialect dialect, ColumnDefinition column)
    {
        dialect.Validate(column);
        var builder = new StringBuilder()
            .Append(dialect.QuoteIdentifier(column.Name))
            .Append(' ')
            .Append(dialect.MapType(column));
        if (dialect.MapCollation(column) is { } collation)
            builder.Append(" COLLATE ").Append(collation);
        builder.Append(column.IsNullable ? " NULL" : " NOT NULL");
        if (dialect.MapDefault(column) is { } value)
            builder.Append(" DEFAULT ").Append(value);
        return builder.ToString();
    }
}
