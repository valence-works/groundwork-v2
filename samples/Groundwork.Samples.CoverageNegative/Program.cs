using Groundwork.Schema;

var database = new Database();
var status = "open";
_ = database.Table<Ticket>().Where(ticket => ticket.Status == status).QueryAsync();

[GwTable("tickets")]
public sealed class Ticket
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public sealed class Database
{
    public Query<T> Table<T>() => new();
}

public sealed class Query<T>
{
    public Query<T> Where(Func<T, bool> predicate) => this;
    public Task QueryAsync() => Task.CompletedTask;
}
