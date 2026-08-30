using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Groundwork.Diagnostics;
using System.Text;

namespace Groundwork.Sqlite;

/// <summary>Creates SQLite provider connections with one durable store-scoped schema lock.</summary>
public sealed class SqliteProviderFactory : IStorageProviderFactory
{
    public IStorageProviderConnection Create(string connectionString) =>
        new SqliteProviderConnection(connectionString);
}

public sealed class SqliteProviderConnection : IStorageProviderConnection, IQueryAdmissionProviderConnection
{
    /// <summary>
    /// SQLite's parameter ceiling is a compile-time option of the library this process loaded, so it is
    /// advertised here rather than assumed by callers.
    /// </summary>
    public QueryAdmissionProfile QueryAdmission { get; } = new()
    {
        MaximumParameters = SqliteQueryRenderer.ParameterBudget,
        MaximumBatchReadKeys = SqliteQueryRenderer.ParameterBudget
    };

    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private readonly FileStream? schemaLock;
    private readonly List<SqliteConnection> sessionConnections = [];
    private readonly bool isMemory;
    private readonly SqliteSchemaCoordinator schemaCoordinator;
    private readonly SchemaSessionPublicationRegistry schemaSessions = new();
    private volatile ISessionRegistrationObserver? activeRegistrationObserver;
    private volatile bool disposed;

    public SqliteProviderConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new SqliteConnectionStringBuilder(connectionString);
        FileStream? acquiredLock = null;
        SqliteConnection? opened = null;
        try
        {
            acquiredLock = AcquireSchemaLock(builder);
            opened = CreateOpenConnection(builder.ConnectionString);
            schemaLock = acquiredLock;
            connection = opened;
            isMemory = SqliteDataSource.IsMemory(builder);
            schemaCoordinator = new SqliteSchemaCoordinator(this);
            Schema = schemaCoordinator;
            Catalog = new SqliteProviderCatalog(this);
        }
        catch
        {
            opened?.Dispose();
            acquiredLock?.Dispose();
            throw;
        }
    }

    public IProviderCatalog Catalog { get; }

    public ISchemaCoordinator Schema { get; }

    public IReadOnlyList<CapabilityDescriptor> Capabilities => SchemaCapabilityAdmission.AdvertiseEnforcedConstraints(
        BatchWriteCapabilities.ForProvider(
            "SQLite", nativeBatch: true,
            exactOutcomeCost: "one RETURNING result per native batch",
            batchCost: "uses variable-limit-aware multi-row INSERT/UPSERT commands; secondary unique declarations use the row-attributed fallback",
            exactAppendOutcomes: true,
            durableHighWaterInspection: true,
            exactRetention: true,
            atomicCommit: true,
            compareAndDelete: true,
            setMutation: "Updates or deletes every row matching an index-covered portable predicate on SQLite in one UPDATE/DELETE statement; the statement is atomic and reports its affected-row count."));

    internal object Gate => gate;

    internal SqliteConnection Connection => connection;

    internal bool UsesSharedSessionConnection => isMemory;

    internal SqliteConnection CreateIndependentConnection() =>
        CreateOpenConnection(connection.ConnectionString);

    internal SchemaSessionLease CaptureSchemaSession(StorageUnit physicalUnit) =>
        schemaSessions.Capture(SqliteSchemaCoordinator.Target(physicalUnit));

    internal void PublishSchema(PhysicalSchemaTarget target) => schemaSessions.Publish(target);

    internal void RefreshSchema()
    {
        lock (gate)
        {
            ThrowIfDisposed();
            connection.Close();
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA schema_version;";
            _ = command.ExecuteScalar();
        }
    }

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access, IProviderCommandObserver? observer = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        PortabilityValidator.EnsurePhysicalIdentifiers(unit);
        SqliteSchemaCoordinator.ValidateAccess(unit, access);
        var physicalUnit = SqliteSchemaCoordinator.Physicalize(unit);
        var sessionConnection = isMemory ? connection : CreateIndependentConnection();
        try
        {
            schemaCoordinator.EnsureRuntimeAdmission(unit, observer, sessionConnection);
            var schemaSession = CaptureSchemaSession(physicalUnit);
            var session = new SqliteStorageSession(
                this, physicalUnit, access, sessionConnection, null, schemaSession, observer);
            RegisterSessionConnection(sessionConnection, observer);
            return session;
        }
        catch
        {
            if (!isMemory)
                sessionConnection.Dispose();
            throw;
        }
    }

    public IOwnedStorageSession OpenOwnedSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        PortabilityValidator.EnsurePhysicalIdentifiers(unit);
        SqliteSchemaCoordinator.ValidateAccess(unit, access);
        var physicalUnit = SqliteSchemaCoordinator.Physicalize(unit);
        var sessionConnection = isMemory ? connection : CreateIndependentConnection();
        try
        {
            schemaCoordinator.EnsureRuntimeAdmission(unit, observer, sessionConnection);
            // Shared in-memory mode is the one place ownership cannot transfer: that connection IS the database,
            // so releasing it would drop every table. Disposal there closes the session only. SQLite serializes
            // internally, so the concurrency this seam buys elsewhere costs nothing to forgo here.
            var schemaSession = CaptureSchemaSession(physicalUnit);
            return new OwnedSqliteStorageSession(
                this, physicalUnit, access, sessionConnection, schemaSession, observer, !isMemory);
        }
        catch
        {
            if (!isMemory)
                sessionConnection.Dispose();
            throw;
        }
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units)
        => BeginUnitOfWork(access, BatchWriteOptions.Default, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        params StorageUnit[] units)
        => BeginUnitOfWork(access, options, observer: null, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        IProviderCommandObserver? observer,
        params StorageUnit[] units)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(units);
        StorageAccessValidation.EnsureUnitOfWork(access);
        if (units.Length == 0)
            throw new ArgumentException("A unit of work must declare at least one storage unit.", nameof(units));
        if (units.Select(unit => unit.Id).Distinct().Count() != units.Length)
            throw new ArgumentException("A unit of work cannot list the same storage unit twice.", nameof(units));
        var transactional = CreateIndependentConnection();
        try
        {
            foreach (var unit in units)
            {
                ArgumentNullException.ThrowIfNull(unit);
                PortabilityValidator.EnsurePhysicalIdentifiers(unit);
                SqliteSchemaCoordinator.ValidateAccess(unit, access);
                schemaCoordinator.EnsureRuntimeAdmission(unit, observer, transactional);
            }

            var transaction = transactional.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            var physicalUnits = units.ToDictionary(
                unit => unit.Id,
                SqliteSchemaCoordinator.Physicalize);
            var lifetime = new RelationalUnitOfWorkLifetime(
                transactional,
                transaction,
                supportsAsync: false,
                disposeTransaction: true,
                rollback: () => SqliteTransactionCleanup.RollbackOrClearPool(transaction, transactional));
            return new RelationalUnitOfWork(
                units,
                options,
                unit =>
                {
                    var session = new SqliteStorageSession(
                        this,
                        physicalUnits[unit.Id],
                        access,
                        transactional,
                        transaction,
                        CaptureSchemaSession(physicalUnits[unit.Id]),
                        observer);
                    return new RelationalUnitOfWorkSession(session, session.Close);
                },
                lifetime);
        }
        catch
        {
            transactional.Dispose();
            throw;
        }
    }

    internal void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(SqliteProviderConnection));
    }

    public void Dispose()
    {
        activeRegistrationObserver?.OnProviderDisposalAttempted();
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            connection.Dispose();
            foreach (var sessionConnection in sessionConnections)
                sessionConnection.Dispose();
            sessionConnections.Clear();
            schemaLock?.Dispose();
        }
    }

    private void RegisterSessionConnection(
        SqliteConnection sessionConnection,
        IProviderCommandObserver? observer)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (observer is ISessionRegistrationObserver registrationObserver)
            {
                activeRegistrationObserver = registrationObserver;
                try
                {
                    registrationObserver.OnSessionRegistrationEligibilityChecked();
                    ThrowIfDisposed();
                    if (!isMemory)
                        sessionConnections.Add(sessionConnection);
                }
                finally
                {
                    activeRegistrationObserver = null;
                }
            }
            else if (!isMemory)
            {
                sessionConnections.Add(sessionConnection);
            }
        }
    }

    internal static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal static object? ToSqliteValue(object? value, ColumnDefinition definition)
    {
        if (value is null)
            return DBNull.Value;
        return definition.Type switch
        {
            PortableType.Boolean when value is bool boolean => boolean ? 1 : 0,
            PortableType.Guid when value is Guid guid => guid.ToString("D"),
            PortableType.DateTimeOffset when value is DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O"),
            PortableType.Decimal when value is decimal decimalValue => decimalValue.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            PortableType.Json => value is string text ? text : PortableJsonSerializer.Serialize(value),
            PortableType.Binary when value is byte[] bytes => bytes.ToArray(),
            _ => value
        };
    }

    private static FileStream? AcquireSchemaLock(SqliteConnectionStringBuilder builder)
    {
        if (SqliteDataSource.IsMemory(builder))
            return null;

        var fullPath = SqliteDataSource.FullPath(builder.DataSource);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var path = $"{fullPath}.schema.lock";
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            // The lock is held for the whole life of a connection, so this is just as likely to be a
            // second connection inside this process — the reflex an ASP.NET Core developer brings from
            // per-request data-access libraries — as it is a second process. Name both, and name the fix.
            throw new InvalidOperationException(
                $"GW-SQLITE-LIFETIME-001: SQLite store '{fullPath}' already has an open Groundwork connection " +
                "holding its schema lock, in this process or another one. A SQLite store allows exactly one " +
                "IStorageProviderConnection per database file, held for the life of the process. Keep the one " +
                "connection and open a session or unit of work per request from it — under a host, register it " +
                "with AddGroundwork().AddConnection(...), which registers connections as process singletons. " +
                "In tests, give each test its own database file or use 'Data Source=:memory:'.",
                exception);
        }
    }

    private static SqliteConnection CreateOpenConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            connection.CreateCollation("GROUNDWORK_UTF16_ORDINAL", static (left, right) => string.CompareOrdinal(left, right));
            connection.CreateFunction<string, string>(
                "groundwork_scope_token",
                static scope => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(scope))),
                isDeterministic: true);
            connection.CreateFunction<string?, long, int, string?, long?, string?>(
                "groundwork_time_bucket",
                static (value, widthTicks, kind, timeZoneId, originTicks) =>
                {
                    if (value is null)
                        return null;
                    var timestamp = DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    var origin = originTicks is long ticks
                        ? new DateTimeOffset(ticks, TimeSpan.Zero)
                        : (DateTimeOffset?)null;
                    var bucket = AggregationTimeBucketCalculator.Bucket(
                        timestamp,
                        (AggregationTimeBucketKind)kind,
                        TimeSpan.FromTicks(widthTicks),
                        timeZoneId,
                        origin);
                    return bucket.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                },
                isDeterministic: true);
            connection.CreateCollation("GROUNDWORK_DECIMAL_18_4", static (left, right) =>
            {
                if (decimal.TryParse(left, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var leftNumber) &&
                    decimal.TryParse(right, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var rightNumber))
                    return leftNumber.CompareTo(rightNumber);
                return string.CompareOrdinal(left, right);
            });
            connection.CreateAggregate<string?, DecimalSum, string?>(
                "groundwork_decimal_sum",
                new DecimalSum(0m, false),
                static (state, value) => value is null
                    ? state
                    : new DecimalSum(
                        checked(state.Value + decimal.Parse(
                            value,
                            System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.InvariantCulture)),
                        true),
                static state => state.HasValue
                    ? state.Value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)
                    : null,
                isDeterministic: true);
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
                pragma.ExecuteNonQuery();
            }

            using var version = connection.CreateCommand();
            version.CommandText = "SELECT sqlite_version();";
            var value = Convert.ToString(version.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (!Version.TryParse(value, out var actual) || actual < new Version(3, 35, 0))
            {
                throw new InvalidOperationException(
                    $"SQLite 3.35.0 or newer is required for Groundwork.Sqlite (found '{value ?? "unknown"}').");
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private readonly record struct DecimalSum(decimal Value, bool HasValue);
}
