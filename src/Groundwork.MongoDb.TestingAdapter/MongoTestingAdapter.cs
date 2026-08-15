using Groundwork.MongoDb;
using Groundwork.Store;

namespace Groundwork.MongoDb.TestingAdapter;

/// <summary>
/// Compatibility name for consumers migrating from the pre-production Mongo adapter.
/// Use <see cref="MongoProviderFactory"/> from <c>Groundwork.MongoDb</c> for new code.
/// </summary>
[Obsolete("Use Groundwork.MongoDb.MongoProviderFactory from the production Groundwork.MongoDb package.")]
public sealed class MongoDbTestingFactory : IStorageProviderFactory
{
    private readonly MongoProviderFactory inner = new();

    public IStorageProviderConnection Create(string connectionString) =>
        inner.Create(connectionString);
}
