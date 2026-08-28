# Groundwork SQL Server provider

`Groundwork.SqlServer` implements the provider-neutral `IStorageProviderFactory` contract over
SQL Server 2022 and newer. It uses native typed tables and nonclustered primary/secondary
indexes; it does not add a document envelope or a synthetic identity column.

```csharp
using var store = new SqlServerProviderFactory().Create(
    "Server=localhost,14339;Database=app;User Id=sa;Password=...;Encrypt=False;TrustServerCertificate=True");
```

SQL Server limits nonclustered index keys to 32 columns and 1,700 bytes. Groundwork validates
those limits while applying the declaration, using declared worst-case widths (for example,
`nvarchar(320)` contributes 640 bytes). Variable-length key columns must declare `MaxLength`;
unbounded strings, binary values, JSON, and unsupported collations are refused before opening a
provider connection. Decimal keys use SQL Server's native precision tiers.

`IStorageSession`'s asynchronous members run on Microsoft.Data.SqlClient's asynchronous ADO.NET
surface and genuinely yield the calling thread, including the write transaction and the
unit-of-work commit. The connection gate that serializes write transactions across the sessions of
one provider connection is a `SemaphoreSlim` rather than a monitor, because the asynchronous write
path holds it across an await.

The provider uses `sp_getapplock` plus a durable fence/history pair for schema coordination and
serializable write transactions for optimistic concurrency. The conformance suite runs against the
server named by `GROUNDWORK_SQLSERVER_CONNECTION` and skips without one, like every other live
suite: CI proves it in the jobs that provision a SQL Server, not in the ones that do not.

Folded prefix indexes target provider-owned ASCII search-key columns. SQL Server validates their
physical key budget against the logical source width using the declared expansion factor: `5x`
for ASCII ignore-case and `7x` for Unicode ordinal ignore-case.
