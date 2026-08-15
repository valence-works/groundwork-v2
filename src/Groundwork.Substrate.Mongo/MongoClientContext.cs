using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.Substrate.Mongo;

/// <summary>Owns the Mongo client lifecycle and selects the database named by the connection string.</summary>
public sealed class MongoClientContext : IDisposable
{
    private bool disposed;

    public MongoClientContext(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var url = new MongoUrl(connectionString);
        if (string.IsNullOrWhiteSpace(url.DatabaseName))
            throw new ArgumentException("The MongoDB connection string must name a database.", nameof(connectionString));

        Client = new MongoClient(url);
        Database = Client.GetDatabase(url.DatabaseName);
    }

    public IMongoClient Client { get; }

    public IMongoDatabase Database { get; }

    public IClientSessionHandle StartSession()
    {
        ThrowIfDisposed();
        return Client.StartSession();
    }

    public bool SupportsTransactions()
    {
        ThrowIfDisposed();
        var hello = Database.RunCommand<BsonDocument>(new BsonDocument("hello", 1));
        return hello.Contains("setName") ||
               string.Equals(hello.GetValue("msg", string.Empty).AsString, "isdbgrid", StringComparison.Ordinal);
    }

    public void RequireTransactions(string feature)
    {
        if (SupportsTransactions())
            return;

        throw new InvalidOperationException(
            $"{feature} requires a transaction-capable MongoDB replica set or sharded deployment; " +
            "standalone MongoDB cannot provide the required atomic commit " +
            "(capability 'groundwork.column.provider-sequence').");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Client.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(MongoClientContext));
    }
}
