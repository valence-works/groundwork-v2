using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Groundwork.Samples.Api.Tests;

/// <summary>
/// End-to-end proof for the sample: declaration, schema, typed CRUD, a covered query with paging,
/// a unit of work, optimistic concurrency, tenant scopes, capability advertisement, and health.
/// </summary>
public sealed class SampleApiTests : IClassFixture<SampleApiFactory>
{
    private readonly HttpClient client;

    public SampleApiTests(SampleApiFactory factory) => client = factory.CreateClient();

    [Fact]
    public async Task Health_reports_the_admitted_declaration()
    {
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Capabilities_are_read_from_the_deployed_database()
    {
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/capabilities");
        var ids = capabilities.EnumerateArray()
            .Select(capability => capability.GetProperty("id").GetString())
            .ToArray();
        Assert.Contains("groundwork.storage.batched-unit-of-work", ids);
        Assert.Contains("groundwork.storage.compare-and-delete", ids);
    }

    [Fact]
    public async Task An_order_can_be_created_read_updated_and_deleted()
    {
        var id = Unique("crud");
        var created = await client.PostAsJsonAsync("/orders", Order(id, "ada", 12.50m));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var read = await client.GetFromJsonAsync<JsonElement>($"/orders/{id}");
        Assert.Equal("ada", read.GetProperty("customer").GetString());
        Assert.Equal(12.50m, read.GetProperty("total").GetDecimal());
        var version = read.GetProperty("version").GetInt64();

        var updated = await Update(id, "grace", 20m, version);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/orders/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/orders/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_duplicate_order_is_refused_as_a_status_rather_than_an_exception()
    {
        var id = Unique("duplicate");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/orders", Order(id, "ada", 1m))).StatusCode);
        var again = await client.PostAsJsonAsync("/orders", Order(id, "ada", 1m));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_stale_version_loses_the_optimistic_concurrency_race()
    {
        var id = Unique("concurrency");
        await client.PostAsJsonAsync("/orders", Order(id, "ada", 5m));
        var stale = (await client.GetFromJsonAsync<JsonElement>($"/orders/{id}")).GetProperty("version").GetInt64();

        Assert.Equal(HttpStatusCode.OK, (await Update(id, "ada", 6m, stale)).StatusCode);
        // The second writer read the same version the first one did, and finds out before writing.
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await Update(id, "ada", 7m, stale)).StatusCode);
    }

    [Fact]
    public async Task A_unit_of_work_commits_every_staged_row_together()
    {
        var customer = Unique("batch-customer");
        var batch = new[]
        {
            Order(Unique("batch"), customer, 1m),
            Order(Unique("batch"), customer, 2m),
            Order(Unique("batch"), customer, 3m)
        };

        var response = await client.PostAsJsonAsync("/orders/batch", batch);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, report.GetProperty("submitted").GetInt32());
        Assert.Equal(3, report.GetProperty("applied").GetInt32());
        Assert.Equal(0, report.GetProperty("failed").GetInt32());

        var page = await client.GetFromJsonAsync<JsonElement>($"/orders?customer={customer}");
        Assert.Equal(3, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_covered_query_pages_through_its_declared_index()
    {
        var customer = Unique("paged-customer");
        await client.PostAsJsonAsync("/orders/batch", Enumerable.Range(0, 5)
            .Select(index => Order($"paged-{customer}-{index}", customer, index)).ToArray());

        var first = await client.GetFromJsonAsync<JsonElement>($"/orders?customer={customer}&take=2");
        var second = await client.GetFromJsonAsync<JsonElement>($"/orders?customer={customer}&skip=2&take=2");
        Assert.Equal(2, first.GetProperty("items").GetArrayLength());
        Assert.Equal(2, second.GetProperty("items").GetArrayLength());
        Assert.NotEqual(
            first.GetProperty("items")[0].GetProperty("id").GetString(),
            second.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Tenants_cannot_see_each_other_and_pages_continue_by_keyset()
    {
        var left = Unique("tenant-a");
        var right = Unique("tenant-b");
        foreach (var index in Enumerable.Range(0, 3))
        {
            await client.PostAsJsonAsync($"/tenants/{left}/notes", new { id = $"note-{index}", body = "left" });
            await client.PostAsJsonAsync($"/tenants/{right}/notes", new { id = $"note-{index}", body = "right" });
        }

        // The same logical keys live in both tenants without colliding.
        var page = await client.GetFromJsonAsync<JsonElement>($"/tenants/{left}/notes?limit=2");
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.All(page.GetProperty("items").EnumerateArray(),
            note => Assert.Equal("left", note.GetProperty("body").GetString()));

        var token = page.GetProperty("next").GetString();
        Assert.NotNull(token);
        var next = await client.GetFromJsonAsync<JsonElement>(
            $"/tenants/{left}/notes?limit=2&continuation={Uri.EscapeDataString(token)}");
        Assert.Equal(1, next.GetProperty("items").GetArrayLength());
        Assert.Equal("note-2", next.GetProperty("items")[0].GetProperty("id").GetString());
    }

    private Task<HttpResponseMessage> Update(string id, string customer, decimal total, long version)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/orders/{id}")
        {
            Content = JsonContent.Create(Order(id, customer, total))
        };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
        return client.SendAsync(request);
    }

    private static object Order(string id, string customer, decimal total) =>
        new { id, customer, total, placedAt = (DateTimeOffset?)null };

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];
}
