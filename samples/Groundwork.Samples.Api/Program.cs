using System.Collections.Immutable;
using Groundwork.Extensions.DependencyInjection;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Records;
using Groundwork.Samples.Api;
using Groundwork.Store;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration.GetSection("Groundwork");

// One registration. Connections become process singletons; sessions and units of work come from the
// scoped IGroundworkStorage. Neither lifetime is configurable, because only one of them is correct.
builder.Services.AddGroundwork().AddConnection(options =>
{
    options
        .UseProvider(
            SampleStorage.ProviderFactory(configuration["Provider"] ?? "sqlite"),
            configuration["ConnectionString"] ?? "Data Source=groundwork-sample.db")
        .AddUnits(SampleStorage.Orders.Definition, SampleStorage.Notes)
        // Capabilities are advertised by the deployed database, not assumed from the provider name.
        // Startup refuses with GW-HOST-006 if this one is missing.
        .RequireCapabilities(BatchWriteCapabilities.StagedUnitOfWork.Value);

    // DEVELOPMENT ONLY. Physical schema is deployment-time work: the supported path is
    //     groundwork apply --schema groundwork.schema.json --provider sqlite --safe
    // and the runtime is inspect-only by default. This switch exists so `dotnet run` and the sample's
    // own tests can stand a database up without a deployment step, and it is doubly gated: the
    // environment must be Development *and* Groundwork:DevelopmentApplySchema must be true.
    options.AutoApplyOnStartup =
        builder.Environment.IsDevelopment() && configuration.GetValue<bool>("DevelopmentApplySchema");
});
builder.Services.AddHealthChecks().AddGroundwork();

var app = builder.Build();

// Reports startup admission and live capability advertisement — not a synthetic ping.
app.MapHealthChecks("/health");

// What the deployed database actually says it can do.
app.MapGet("/capabilities", (IStorageProviderConnection connection) => connection.Capabilities
    .Select(capability => new CapabilityView(capability.Id.Value, capability.DisplayName, capability.Description))
    .OrderBy(capability => capability.Id, StringComparer.Ordinal));

// ---------------------------------------------------------------------------------------------
// Typed writes go through a unit of work owned by the request scope. If the request throws before
// commit, the scope disposes the unit of work and the transaction rolls back — nothing to remember.
// ---------------------------------------------------------------------------------------------

app.MapPost("/orders", (OrderRequest request, IGroundworkStorage storage) =>
{
    var order = new Order
    {
        Id = request.Id,
        Customer = request.Customer,
        Total = request.Total,
        PlacedAt = DateTimeOffset.UtcNow
    };
    var work = storage.BeginUnitOfWork(
        StorageAccess.Global, BatchWriteOptions.Exact, SampleStorage.Orders.Definition);
    work.Stage(RowWrite.Insert(SampleStorage.Orders.Definition, Values(order)));
    var outcome = Commit(work);

    return outcome.Status == WriteOutcomeStatus.Inserted
        ? Results.Created($"/orders/{order.Id}", OrderView.From(order, outcome.Version))
        : Results.Conflict(new RefusalView(outcome.Status.ToString(), $"Order '{order.Id}' already exists."));
});

// Optimistic concurrency: the caller must present the version it read, in If-Match.
app.MapPut("/orders/{id}", (string id, OrderRequest request, HttpRequest http, IGroundworkStorage storage) =>
{
    if (!TryReadIfMatch(http, out var expectedVersion))
        return Results.BadRequest(new RefusalView("missing-if-match", "Supply the version you read in If-Match."));

    var order = new Order
    {
        Id = id,
        Customer = request.Customer,
        Total = request.Total,
        PlacedAt = request.PlacedAt ?? DateTimeOffset.UtcNow
    };
    var work = storage.BeginUnitOfWork(
        StorageAccess.Global, BatchWriteOptions.Exact, SampleStorage.Orders.Definition);
    work.Stage(RowWrite.Update(
        SampleStorage.Orders.Definition, Values(order), WriteOptions.IfVersion(expectedVersion)));
    var outcome = Commit(work);

    return outcome.Status switch
    {
        WriteOutcomeStatus.Updated => Results.Ok(OrderView.From(order, outcome.Version)),
        WriteOutcomeStatus.NotFound => Results.NotFound(),
        // A status, not an exception: someone else wrote this row since you read it.
        _ => Results.StatusCode(StatusCodes.Status412PreconditionFailed)
    };
});

app.MapDelete("/orders/{id}", (string id, IGroundworkStorage storage) =>
{
    var work = storage.BeginUnitOfWork(
        StorageAccess.Global, BatchWriteOptions.Exact, SampleStorage.Orders.Definition);
    work.Stage(RowWrite.Delete(SampleStorage.Orders.Definition, OrderKey(id)));
    return Commit(work).Status == WriteOutcomeStatus.Deleted
        ? Results.NoContent()
        : Results.NotFound();
});

// One unit of work, many rows, one transaction, one outcome per staged row.
app.MapPost("/orders/batch", (OrderRequest[] requests, IGroundworkStorage storage) =>
{
    var work = storage.BeginUnitOfWork(
        StorageAccess.Global, BatchWriteOptions.Exact, SampleStorage.Orders.Definition);
    foreach (var request in requests)
    {
        work.Stage(RowWrite.Upsert(SampleStorage.Orders.Definition, Values(new Order
        {
            Id = request.Id,
            Customer = request.Customer,
            Total = request.Total,
            PlacedAt = request.PlacedAt ?? DateTimeOffset.UtcNow
        })));
    }

    var report = work.CommitWithOutcomes();
    return Results.Ok(new BatchView(
        report.Submitted,
        report.Applied,
        report.Failed,
        [.. report.Outcomes.Select(outcome => outcome.Outcome.Status.ToString())]));
});

// ---------------------------------------------------------------------------------------------
// Reads open a session directly on the process-singleton connection, once per request. This is the
// lifetime model Groundwork documents: the connection is shared, the session is a cheap non-owning
// view over it, and there is nothing to dispose.
//
// Caveat, stated plainly: a session opened this way currently keeps its provider connection until
// the *storage* connection is disposed, so a long-running service doing this per request
// accumulates one open database handle per request. See
// https://github.com/valence-works/groundwork-v2/issues/199. Units of work — the write path above —
// own and release their connection at commit, rollback, or dispose, and are unaffected.
// ---------------------------------------------------------------------------------------------

app.MapGet("/orders/{id}", (string id, IGroundworkStorage storage) =>
{
    var session = storage.OpenSession(SampleStorage.Orders.Definition, StorageAccess.Global);
    var entry = session.Read(OrderKey(id));
    return entry is null
        ? Results.NotFound()
        : Results.Ok(OrderView.From(
            SampleStorage.Orders.FromRowValues(new RowValues(entry.Values.Values)), entry.Version));
});

// A covered query: `by_customer` is declared, so this shape has an index to serve it.
app.MapGet("/orders", (string customer, int? skip, int? take, IGroundworkStorage storage) =>
{
    var records = SampleStorage.Orders.Open(storage.Connection, StorageAccess.Global);
    var query = SampleStorage.Orders.Query
        .Where(order => order.Customer == customer)
        .OrderBy(order => order.Id)
        .Skip(skip ?? 0)
        .Take(Math.Clamp(take ?? 20, 1, 200));
    return Results.Ok(new PageView<OrderView>(
        [.. records.Query(query, RecordQueryOptions.UsingIndex("by_customer"))
            .Select(order => OrderView.From(order, version: null))],
        Next: null));
});

// ---------------------------------------------------------------------------------------------
// Multi-tenancy. `notes` is declared Scoped, so every session must name exactly one tenant. Opening
// it with StorageAccess.Global fails before any I/O — there is no ambient tenant to forget.
// ---------------------------------------------------------------------------------------------

app.MapPost("/tenants/{tenant}/notes", (string tenant, NoteRequest request, IGroundworkStorage storage) =>
{
    var work = storage.BeginUnitOfWork(
        Tenant(tenant), BatchWriteOptions.Exact, SampleStorage.Notes);
    work.Stage(RowWrite.Insert(SampleStorage.Notes, new StorageValues(new Dictionary<string, object?>
    {
        ["id"] = request.Id,
        ["body"] = request.Body,
        ["createdAt"] = DateTimeOffset.UtcNow
    })));
    var outcome = Commit(work);
    return outcome.Status == WriteOutcomeStatus.Inserted
        ? Results.Created($"/tenants/{tenant}/notes/{request.Id}", new NoteView(request.Id, request.Body))
        : Results.Conflict(new RefusalView(outcome.Status.ToString(), $"Note '{request.Id}' already exists in '{tenant}'."));
});

// Keyset paging, which is what you want for a page-through endpoint: the continuation token carries
// the ordering tuple, so page N+1 costs the same as page 1 no matter how deep you are.
app.MapGet("/tenants/{tenant}/notes", (string tenant, int? limit, string? continuation, IGroundworkStorage storage) =>
{
    var session = storage.OpenSession(SampleStorage.Notes, Tenant(tenant));
    var table = new TableId(SampleStorage.Notes.Name);
    var id = new ColumnRef(table, "id", QueryType.String, isNullable: false);
    var page = Math.Clamp(limit ?? 20, 1, 200);
    var result = session.Query(new QueryRequest(
        table,
        Predicate.AlwaysTrue.Instance,
        ImmutableArray.Create(new OrderTerm(id, OrderDirection.Ascending, NullOrder.Last)),
        Projection.All,
        continuation is null ? Paging.Keyset(page) : Paging.Continuation(continuation, page)));

    return Results.Ok(new PageView<NoteView>(
        [.. result.Rows.Select(row => new NoteView((string)row["id"]!, (string)row["body"]!))],
        result.NextContinuationToken));
});

app.Run();

// A unit of work is all or nothing: one refused row aborts the whole unit, and the provider reports
// that by throwing with the attributed outcome rather than half-committing. For a single-row request
// the interesting part is the status, so unwrap it and let the caller see a refusal, not a 500.
static WriteOutcome Commit(IUnitOfWork work)
{
    try
    {
        return work.CommitWithOutcomes().Outcomes[0].Outcome;
    }
    catch (BatchWriteException exception)
    {
        return exception.Outcomes[0].Outcome;
    }
}

static StorageValues Values(Order order) =>
    new(SampleStorage.Orders.ToRowValues(order).Values);

static StorageKey OrderKey(string id) =>
    new(new Dictionary<string, object?> { ["id"] = id });

static StorageAccess Tenant(string tenant) =>
    StorageAccess.Scoped(new StorageScope(tenant));

static bool TryReadIfMatch(HttpRequest request, out long version)
{
    version = 0;
    var header = request.Headers.IfMatch.ToString().Trim('"');
    return header.Length != 0 && long.TryParse(header, out version);
}

internal sealed record OrderRequest(string Id, string Customer, decimal Total, DateTimeOffset? PlacedAt);

internal sealed record OrderView(string Id, string Customer, decimal Total, DateTimeOffset PlacedAt, long? Version)
{
    internal static OrderView From(Order order, long? version) =>
        new(order.Id, order.Customer, order.Total, order.PlacedAt, version ?? (order.Version == 0 ? null : order.Version));
}

internal sealed record NoteRequest(string Id, string Body);

internal sealed record NoteView(string Id, string Body);

internal sealed record PageView<T>(IReadOnlyList<T> Items, string? Next);

internal sealed record CapabilityView(string Id, string Name, string Description);

internal sealed record BatchView(int Submitted, int Applied, int Failed, IReadOnlyList<string> Outcomes);

internal sealed record RefusalView(string Status, string Message);

/// <summary>Exposed so the sample's tests can host it with <c>WebApplicationFactory</c>.</summary>
public partial class Program;
