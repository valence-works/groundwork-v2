using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Groundwork.Kernel;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Groundwork.Diagnostics;

namespace Groundwork.SqlServer;

/// <summary>Creates SQL Server provider connections.</summary>
public sealed class SqlServerProviderFactory : IStorageProviderFactory
{
    public IStorageProviderConnection Create(string connectionString) =>
        new SqlServerProviderConnection(connectionString);
}

public sealed class SqlServerProviderConnection : IStorageProviderConnection, IQueryAdmissionProviderConnection
{
    /// <summary>
    /// The budget SqlServerQueryRenderer enforces, so the pre-execution fence and the renderer cannot
    /// disagree about it.
    /// </summary>
    public QueryAdmissionProfile QueryAdmission { get; } = new()
    {
        MaximumParameters = SqlServerQueryRenderer.ParameterBudget,
        // Keep two slots below SQL Server's 2,100-parameter renderer ceiling, as required by the
        // keyed batch-read admission budget.
        MaximumBatchReadKeys = 2_098
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string connectionString;
    private readonly List<SqlConnection> sessionConnections = [];
    private readonly SqlServerSchemaCoordinator schemaCoordinator;
    private bool disposed;

    public SqlServerProviderConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        this.connectionString = connectionString;
        schemaCoordinator = new SqlServerSchemaCoordinator(this);
        Schema = schemaCoordinator;
        Catalog = new SqlServerProviderCatalog(this);
    }

    public IProviderCatalog Catalog { get; }

    public ISchemaCoordinator Schema { get; }

    public IReadOnlyList<CapabilityDescriptor> Capabilities => BatchWriteCapabilities.ForProvider(
        "SQL Server", nativeBatch: true,
        exactOutcomeCost: "one OUTPUT result per MERGE batch",
        batchCost: "uses one durable table-valued-parameter MERGE batch; VALUES is a compatibility fallback",
        exactAppendOutcomes: true,
        durableHighWaterInspection: true,
        exactRetention: true,
        atomicCommit: true,
        compareAndDelete: true);

    /// <summary>
    /// Serializes the writes and connection bookkeeping of every session this connection owns.
    /// The gate is a semaphore rather than a monitor because the asynchronous write path holds it
    /// across an await, which a monitor cannot do.
    /// </summary>
    internal IDisposable EnterGate()
    {
        gate.Wait();
        return new GateScope(gate);
    }

    internal ValueTask<IDisposable> EnterGate(RelationalExecution mode) =>
        mode.IsAsync ? EnterGateAsync(mode.CancellationToken) : new(EnterGate());

    private async ValueTask<IDisposable> EnterGateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateScope(gate);
    }

    private sealed class GateScope(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    internal SqlConnection CreateIndependentConnection()
    {
        ThrowIfDisposed();
        var connection = new SqlConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access, IProviderCommandObserver? observer = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        PortabilityValidator.EnsurePhysicalIdentifiers(unit);
        SqlServerSchemaCoordinator.ValidateAccess(unit, access);
        var connection = CreateIndependentConnection();
        try
        {
            schemaCoordinator.EnsureRuntimeAdmission(unit, observer, connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        using (EnterGate())
            sessionConnections.Add(connection);
        return new SqlServerStorageSession(this, SqlServerSchemaCoordinator.Physicalize(unit), access, connection, null, observer);
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
        if (units.Any(unit => unit is null))
            throw new ArgumentException("A unit of work cannot contain a null storage unit.", nameof(units));
        if (units.Select(unit => unit.Id).Distinct().Count() != units.Length)
            throw new ArgumentException("A unit of work cannot list the same storage unit twice.", nameof(units));
        var connection = CreateIndependentConnection();
        try
        {
            foreach (var unit in units)
            {
                PortabilityValidator.EnsurePhysicalIdentifiers(unit);
                SqlServerSchemaCoordinator.ValidateAccess(unit, access);
                schemaCoordinator.EnsureRuntimeAdmission(unit, observer, connection);
            }

            var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            return new SqlServerUnitOfWork(this, connection, transaction, units, access, options, observer);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(SqlServerProviderConnection));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        using (EnterGate())
        {
            foreach (var connection in sessionConnections)
                connection.Dispose();
            sessionConnections.Clear();
        }
    }

    internal static string QuoteIdentifier(string identifier) =>
        $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    internal static object? ToSqlServerValue(object? value, ColumnDefinition definition)
    {
        if (value is null)
            return DBNull.Value;
        return definition.Type switch
        {
            PortableType.String when value is string text => ValidateLength(text, definition),
            PortableType.Binary when value is byte[] bytes => ValidateLength(bytes, definition),
            PortableType.DateTimeOffset when value is DateTimeOffset date => date.ToUniversalTime(),
            PortableType.Decimal when value is decimal number => number,
            PortableType.Guid when value is Guid guid => guid,
            PortableType.Json when value is string text => text,
            PortableType.Json => JsonSerializer.Serialize(value),
            _ => value
        };
    }

    internal static void AddParameter(SqlCommand command, string name, object? value, ColumnDefinition definition)
    {
        var parameter = command.Parameters.Add(name, definition.Type switch
        {
            PortableType.String or PortableType.Json => SqlDbType.NVarChar,
            PortableType.Int32 => SqlDbType.Int,
            PortableType.Int64 => SqlDbType.BigInt,
            PortableType.Decimal => SqlDbType.Decimal,
            PortableType.Boolean => SqlDbType.Bit,
            PortableType.DateTimeOffset => SqlDbType.DateTimeOffset,
            PortableType.Guid => SqlDbType.UniqueIdentifier,
            PortableType.Binary => SqlDbType.VarBinary,
            PortableType.Double => SqlDbType.Float,
            _ => throw new ArgumentOutOfRangeException(nameof(definition), definition.Type, null)
        });
        parameter.Value = ToSqlServerValue(value, definition);
        if (definition.Type is PortableType.String or PortableType.Json)
            parameter.Size = definition.MaxLength ?? -1;
        else if (definition.Type == PortableType.Binary)
            parameter.Size = definition.MaxLength ?? -1;
        if (definition.Type == PortableType.Decimal)
        {
            parameter.Precision = checked((byte)(definition.Precision ?? 38));
            parameter.Scale = checked((byte)(definition.Scale ?? 0));
        }
    }

    private static string ValidateLength(string value, ColumnDefinition definition)
    {
        if (definition.MaxLength is int maximum && value.Length > maximum)
            throw new ArgumentException($"Value for column '{definition.Name}' exceeds MaxLength {maximum}.", nameof(value));
        return value;
    }

    private static byte[] ValidateLength(byte[] value, ColumnDefinition definition)
    {
        if (definition.MaxLength is int maximum && value.Length > maximum)
            throw new ArgumentException($"Value for column '{definition.Name}' exceeds MaxLength {maximum}.", nameof(value));
        return value.ToArray();
    }
}
