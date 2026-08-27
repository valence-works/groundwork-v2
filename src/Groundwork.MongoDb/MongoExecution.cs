using MongoDB.Bson;
using MongoDB.Driver;

namespace Groundwork.MongoDb;

/// <summary>
/// Selects the synchronous or the asynchronous MongoDB driver surface for one shared command body,
/// so the provider keeps a single implementation of every operation rather than two that can drift.
/// </summary>
internal readonly struct MongoExecution
{
    private MongoExecution(bool isAsync, CancellationToken cancellationToken)
    {
        IsAsync = isAsync;
        CancellationToken = cancellationToken;
    }

    /// <summary>Runs the body on the calling thread; every returned task is already completed.</summary>
    internal static MongoExecution Synchronous { get; } = new(false, CancellationToken.None);

    internal static MongoExecution Asynchronous(CancellationToken cancellationToken) =>
        new(true, cancellationToken);

    internal bool IsAsync { get; }

    internal CancellationToken CancellationToken { get; }

    internal ValueTask<T> Run<T>(Func<CancellationToken, Task<T>> asynchronous, Func<T> synchronous) =>
        IsAsync ? new(asynchronous(CancellationToken)) : new(synchronous());

    internal ValueTask Run(Func<CancellationToken, Task> asynchronous, Action synchronous)
    {
        if (IsAsync)
            return new(asynchronous(CancellationToken));
        synchronous();
        return default;
    }

    internal ValueTask<List<T>> ToList<T>(IAsyncCursorSource<T> source)
    {
        var cancellationToken = CancellationToken;
        return Run(token => source.ToListAsync(token), () => source.ToList(cancellationToken));
    }

    internal ValueTask<List<T>> ToList<T>(IAsyncCursorSource<T> source, CancellationToken cancellationToken) =>
        Run(_ => source.ToListAsync(cancellationToken), () => source.ToList(cancellationToken));

    internal ValueTask<List<T>> ToList<T>(IAsyncCursor<T> cursor)
    {
        var cancellationToken = CancellationToken;
        return Run(token => cursor.ToListAsync(token), () => cursor.ToList(cancellationToken));
    }

    internal ValueTask<T> FirstOrDefault<T>(IAsyncCursorSource<T> source)
    {
        var cancellationToken = CancellationToken;
        return Run(token => source.FirstOrDefaultAsync(token), () => source.FirstOrDefault(cancellationToken));
    }

    internal ValueTask<IAsyncCursor<T>> ToCursor<T>(IAsyncCursorSource<T> source, CancellationToken cancellationToken) =>
        Run(_ => source.ToCursorAsync(cancellationToken), () => source.ToCursor(cancellationToken));

    internal ValueTask<bool> MoveNext<T>(IAsyncCursor<T> cursor, CancellationToken cancellationToken) =>
        Run(_ => cursor.MoveNextAsync(cancellationToken), () => cursor.MoveNext(cancellationToken));

    /// <summary>Reads a rendered find command, which has no fluent form that carries find options.</summary>
    internal ValueTask<List<BsonDocument>> Find(
        IMongoCollection<BsonDocument> collection,
        IClientSessionHandle? session,
        FilterDefinition<BsonDocument> filter,
        FindOptions<BsonDocument> options)
    {
        var cancellationToken = CancellationToken;
        return Run<List<BsonDocument>>(
            async token =>
            {
                var cursor = session is null
                    ? await collection.FindAsync(filter, options, token).ConfigureAwait(false)
                    : await collection.FindAsync(session, filter, options, token).ConfigureAwait(false);
                return await cursor.ToListAsync(token).ConfigureAwait(false);
            },
            () => (session is null
                ? collection.FindSync(filter, options)
                : collection.FindSync(session, filter, options)).ToList(cancellationToken));
    }
}
