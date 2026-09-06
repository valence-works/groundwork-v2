using System.Reflection;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

public sealed class MongoExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task First_find_emits_native_limit_before_opening_cursor(bool asynchronous)
    {
        var source = DispatchProxy.Create<IFindFluent<BsonDocument, BsonDocument>, FindProbe>();
        var probe = (FindProbe)(object)source;
        probe.Source = source;
        using var cancellation = new CancellationTokenSource();
        var mode = asynchronous ? MongoExecution.Asynchronous(cancellation.Token) : MongoExecution.Synchronous;

        var result = await mode.FirstOrDefault(source);

        Assert.Same(probe.Document, result);
        Assert.Equal(1, probe.IssuedLimit);
        Assert.Equal(asynchronous, probe.OpenedAsynchronously);
        Assert.Equal(asynchronous ? cancellation.Token : CancellationToken.None, probe.Token);
        Assert.True(probe.Cursor.Disposed);
    }

    public class FindProbe : DispatchProxy
    {
        internal IFindFluent<BsonDocument, BsonDocument> Source { get; set; } = null!;
        internal BsonDocument Document { get; } = new("_id", "private-key");
        internal FindOptions<BsonDocument, BsonDocument> Options { get; } = new();
        internal CursorProbe Cursor { get; private set; } = null!;
        internal int? IssuedLimit { get; private set; }
        internal bool OpenedAsynchronously { get; private set; }
        internal CancellationToken Token { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_Options": return Options;
                case "Limit":
                    Options.Limit = (int?)args![0];
                    return Source;
                case "ToCursor":
                case "ToCursorAsync":
                    IssuedLimit = Options.Limit;
                    Token = (CancellationToken)args![0]!;
                    OpenedAsynchronously = method.Name == "ToCursorAsync";
                    Cursor = new CursorProbe(Document);
                    return OpenedAsynchronously
                        ? Task.FromResult<IAsyncCursor<BsonDocument>>(Cursor)
                        : Cursor;
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }

    internal sealed class CursorProbe(BsonDocument document) : IAsyncCursor<BsonDocument>
    {
        public IEnumerable<BsonDocument> Current => [document];
        internal bool Disposed { get; private set; }
        public bool MoveNext(CancellationToken cancellationToken = default) => true;
        public Task<bool> MoveNextAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public void Dispose() => Disposed = true;
    }
}
