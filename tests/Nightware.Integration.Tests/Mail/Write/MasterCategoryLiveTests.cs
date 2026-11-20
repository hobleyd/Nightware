using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Mail.Write;

/// <summary>Phase 12 master categories against the running stack: the list lives in the Inbox's metadata.</summary>
public sealed class MasterCategoryLiveTests : IAsyncLifetime
{
    private const string Alice = "alice";
    private LiveStack _stack = null!;
    private StalwartProbe _probe = null!;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _stack = LiveStack.Start();
        _probe = new StalwartProbe(_stack);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stack is not null)
        {
            await _stack.DisposeAsync();
        }
    }

    private static async Task<(HttpStatusCode Status, JsonObject? Body)> SendAsync(HttpClient client, HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text)!.AsObject());
    }

    [Fact]
    public async Task A_category_is_created_recoloured_listed_and_deleted()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var name = $"Nightware live {_run}";

        var (created, category) = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/outlook/masterCategories", $$"""{"displayName":"{{name}}","color":"preset2"}""");
        created.ShouldBe(HttpStatusCode.Created);
        var id = category!["id"]!.GetValue<string>();
        try
        {
            (await SendAsync(alice, HttpMethod.Post, "/v1.0/me/outlook/masterCategories", $$"""{"displayName":"{{name.ToUpperInvariant()}}"}""")).Status
                .ShouldBe(HttpStatusCode.Conflict);
            (await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/outlook/masterCategories/{id}", """{"color":"preset5"}""")).Body!["color"]!.GetValue<string>()
                .ShouldBe("preset5");

            var (_, list) = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/outlook/masterCategories");
            list!["value"]!.AsArray().ShouldContain(c => c!["id"]!.GetValue<string>() == id && c["color"]!.GetValue<string>() == "preset5");
        }
        finally
        {
            (await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/outlook/masterCategories/{id}")).Status.ShouldBe(HttpStatusCode.NoContent);
        }

        var (_, after) = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/outlook/masterCategories");
        after!["value"]!.AsArray().ShouldNotContain(c => c!["id"]!.GetValue<string>() == id);
    }
}
