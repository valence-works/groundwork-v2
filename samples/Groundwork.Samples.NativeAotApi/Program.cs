using System.Diagnostics;
using System.Text.Json.Serialization;
using Groundwork.Extensions.DependencyInjection;
using Groundwork.Records;
using Groundwork.Samples.NativeAotApi;
using Groundwork.Sqlite;
using Groundwork.Store;
using Microsoft.AspNetCore.Http.HttpResults;

var processStarted = Stopwatch.GetTimestamp();
var builder = WebApplication.CreateSlimBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, NativeAotJsonContext.Default));
builder.Services.AddGroundwork().AddConnection(options =>
{
    options
        .UseProvider(
            new SqliteProviderFactory(),
            builder.Configuration["Groundwork:ConnectionString"] ?? "Data Source=groundwork-native-aot.db")
        .AddUnits(TodoStorage.Table.Definition);
    options.AutoApplyOnStartup = string.Equals(
        builder.Configuration["Groundwork:DevelopmentApplySchema"],
        "true",
        StringComparison.OrdinalIgnoreCase);
});

var app = builder.Build();
app.MapGet("/health", static () => TypedResults.Ok(new HealthView("ready")));
app.MapPost("/todos", CreateTodo);
app.MapGet("/todos/{id}", ReadTodo);
app.MapGet("/todos", QueryTodos);

await app.StartAsync();
Console.WriteLine(
    "GROUNDWORK_NATIVE_AOT_READY " +
    $"app_startup_ms={Stopwatch.GetElapsedTime(processStarted).TotalMilliseconds:F1} " +
    $"dynamic_codegen={RecordTable<TodoItem>.AccessorDynamicCodeGenerationCount}");
await app.WaitForShutdownAsync();

static async Task<Results<Created<TodoView>, Conflict<ApiError>>> CreateTodo(
    CreateTodoRequest request,
    IGroundworkStorage storage,
    CancellationToken cancellationToken)
{
    using var work = storage.BeginUnitOfWork(
        StorageAccess.Global,
        BatchWriteOptions.Exact,
        TodoStorage.Table.Definition);
    var todo = new TodoItem(request.Id, request.Title, request.IsDone);
    work.Stage(RowWrite.Insert(
        TodoStorage.Table.Definition,
        new StorageValues(TodoStorage.Table.ToRowValues(todo).Values)));
    WriteOutcome outcome;
    try
    {
        outcome = (await work.CommitWithOutcomesAsync(cancellationToken)).Outcomes[0].Outcome;
    }
    catch (BatchWriteException exception)
    {
        outcome = exception.Outcomes[0].Outcome;
    }

    return outcome.Status == WriteOutcomeStatus.Inserted
        ? TypedResults.Created($"/todos/{todo.Id}", TodoView.From(todo))
        : TypedResults.Conflict(new ApiError(outcome.Status.ToString(), $"Todo '{todo.Id}' already exists."));
}

static async Task<Results<Ok<TodoView>, NotFound>> ReadTodo(
    string id,
    IGroundworkStorage storage,
    CancellationToken cancellationToken)
{
    var session = storage.OpenSession(TodoStorage.Table.Definition, StorageAccess.Global);
    var entry = await session.ReadAsync(
        new StorageKey(new Dictionary<string, object?> { ["id"] = id }),
        cancellationToken);
    return entry is null
        ? TypedResults.NotFound()
        : TypedResults.Ok(TodoView.From(TodoStorage.Table.FromRowValues(
            new RowValues(entry.Values.Values))));
}

static async Task<Ok<TodoView[]>> QueryTodos(
    bool? done,
    IGroundworkStorage storage,
    CancellationToken cancellationToken)
{
    var query = (done ?? false)
        ? TodoStorage.Table.Query.Where(static todo => todo.IsDone == true)
        : TodoStorage.Table.Query.Where(static todo => todo.IsDone == false);
    query = query
        .OrderBy(todo => todo.Id)
        .Take(100);
    var session = storage.OpenSession(TodoStorage.Table.Definition, StorageAccess.Global);
    var result = await session.QueryAsync(
        query.ToQueryRequest(),
        TodoStorage.Table.Definition.CreateQueryRenderOptions("by_done"),
        cancellationToken);
    return TypedResults.Ok(result.Rows
        .Select(row => TodoView.From(TodoStorage.Table.FromRowValues(new RowValues(row))))
        .ToArray());
}

internal sealed record CreateTodoRequest(string Id, string Title, bool IsDone);

internal sealed record TodoView(string Id, string Title, bool IsDone)
{
    internal static TodoView From(TodoItem item) => new(item.Id, item.Title, item.IsDone);
}

internal sealed record HealthView(string Status);

internal sealed record ApiError(string Status, string Message);

[JsonSerializable(typeof(CreateTodoRequest))]
[JsonSerializable(typeof(TodoView))]
[JsonSerializable(typeof(TodoView[]))]
[JsonSerializable(typeof(HealthView))]
[JsonSerializable(typeof(ApiError))]
internal sealed partial class NativeAotJsonContext : JsonSerializerContext;
