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

The provider uses `sp_getapplock` plus a durable fence/history pair for schema coordination and
serializable write transactions for optimistic concurrency. The conformance test can run against
an existing server by setting `GROUNDWORK_SQLSERVER_CONNECTION`; otherwise its fixture starts the
SQL Server 2022 CU21 container used by CI.

Folded prefix indexes target provider-owned ASCII search-key columns. SQL Server validates their
physical key budget against the logical source width using the declared expansion factor: `5x`
for ASCII ignore-case and `7x` for Unicode ordinal ignore-case.
