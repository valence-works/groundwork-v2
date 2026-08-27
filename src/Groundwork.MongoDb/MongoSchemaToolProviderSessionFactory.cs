using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Mongo;
using MongoDB.Driver;

namespace Groundwork.MongoDb;

/// <summary>
/// Schema-tool plug-in that opens MongoDB deployments for exact schema deployment, so
/// <c>groundwork plan/validate/status/apply/adopt</c> reach MongoDB through the same session
/// contract as every relational provider.
/// </summary>
public sealed class MongoSchemaToolProviderSessionFactory : ISchemaToolProviderSessionFactory
{
    public string Alias => "mongodb";

    public ISchemaToolProviderSession Open(SchemaToolProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Connection))
            throw new SchemaToolProviderInvocationException("The MongoDB schema tool requires --connection.");
        var url = new MongoUrlBuilder(options.Connection);
        if (options.Database is not null)
            url.DatabaseName = options.Database;
        if (string.IsNullOrWhiteSpace(url.DatabaseName))
        {
            throw new SchemaToolProviderInvocationException(
                "The MongoDB schema tool requires a database, named in --connection or in --database.");
        }

        MongoClientContext context;
        try
        {
            context = new MongoClientContext(url.ToMongoUrl().ToString());
        }
        catch (ArgumentException exception)
        {
            throw new SchemaToolProviderInvocationException(exception.Message);
        }

        try
        {
            // Publishing applied state commits the ledger and the provider catalog together, which
            // needs a transaction. A standalone deployment cannot start one, so the tool says so by
            // name here rather than failing part-way through an apply.
            if (!context.SupportsTransactions())
            {
                throw new SchemaToolProviderException(
                    "This MongoDB deployment is standalone and cannot start a transaction, so the schema tool " +
                    "cannot publish an applied schema ledger atomically. Deploy against a replica set or a " +
                    "sharded cluster.");
            }
        }
        catch (MongoException exception)
        {
            context.Dispose();
            throw new SchemaToolProviderException($"The MongoDB deployment could not be reached: {exception.Message}");
        }
        catch
        {
            context.Dispose();
            throw;
        }

        return new MongoSchemaToolSession(context);
    }

    private sealed class MongoSchemaToolSession : ISchemaToolProviderSession
    {
        private readonly MongoClientContext context;

        internal MongoSchemaToolSession(MongoClientContext context)
        {
            this.context = context;
            var executor = new MongoSchemaExecutor(context);
            Executor = executor;
            Inspector = executor;
            DataMigrations = new MongoDataMigrationExecutor(context);
        }

        public ProviderIdentity Provider => MongoSchemaTargets.Provider;

        public IPhysicalSchemaTargetCompiler Targets { get; } = new MongoSchemaTargetCompiler();

        public IPhysicalSchemaExecutor Executor { get; }

        public IPhysicalSchemaHistoryInspector Inspector { get; }

        public IDataMigrationExecutor? DataMigrations { get; }

        public void Dispose() => context.Dispose();
    }
}
