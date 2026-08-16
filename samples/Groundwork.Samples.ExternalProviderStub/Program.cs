using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;

Console.WriteLine(new ExternalProviderDialect().ProviderName);

// This project intentionally is not listed in Groundwork.slnx. It proves that an external
// provider can implement the complete public dialect contract without InternalsVisibleTo.
internal sealed class ExternalProviderDialect : RelationalDialect
{
    public override string ProviderName => "external-stub";
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
    public override bool TableExists(DbConnection connection, DbTransaction transaction, string table) => true;
    public override IReadOnlyDictionary<string, RelationalColumnMetadata> ReadColumns(DbConnection connection, DbTransaction transaction, string table) => new Dictionary<string, RelationalColumnMetadata>();
    public override RelationalIndexMetadata? ReadIndex(DbConnection connection, DbTransaction transaction, string table, string index) => null;
}
