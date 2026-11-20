using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Mail.Write;

/// <summary>
/// Phase 12 against the running stack (ADR 0006 D5): flag dates, completion, categories and importance kept
/// in Stalwart's message metadata, and copy. Each message is sent by alice to herself with a unique subject.
/// </summary>
public sealed class MailParityLiveTests : IAsyncLifetime
{
    private const string Alice = "alice";
    private static readonly string AliceAddress = DevStack.Address("alice");

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

    private async Task<string> DeliverToSelfAsync(HttpClient alice, string subject)
    {
        var (status, _) = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/sendMail", $$$"""
            {"message":{"subject":"{{{subject}}}","body":{"contentType":"text","content":"parity"},
              "toRecipients":[{"emailAddress":{"address":"{{{AliceAddress}}}"}}]},"saveToSentItems":false}
            """);
        status.ShouldBe(HttpStatusCode.Accepted);
        var received = await _probe.WaitForAsync(Alice, "inbox", subject, Ct);
        return "e." + received["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task Flag_completion_categories_and_importance_survive_a_round_trip_and_keep_the_id()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var id = await DeliverToSelfAsync(alice, $"Nightware parity flag {_run}");
        try
        {
            var (patched, body) = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/messages/{id}", """
                {"flag":{"flagStatus":"flagged","dueDateTime":{"dateTime":"2026-12-01T17:00:00","timeZone":"UTC"}},
                 "categories":["Blue category","Ünïcode"],"importance":"high"}
                """);
            patched.ShouldBe(HttpStatusCode.OK);
            body!["id"]!.GetValue<string>().ShouldBe(id);

            var (_, read) = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/messages/{id}?$select=flag,categories,importance");
            read!["flag"]!["flagStatus"]!.GetValue<string>().ShouldBe("flagged");
            read["flag"]!["dueDateTime"]!["dateTime"]!.GetValue<string>().ShouldStartWith("2026-12-01T17:00:00");
            read["categories"]!.AsArray().Select(c => c!.GetValue<string>()).ShouldBe(["Blue category", "Ünïcode"]);
            read["importance"]!.GetValue<string>().ShouldBe("high");
            (await _probe.GetAsync(Alice, id[2..], Ct, """["keywords"]"""))["keywords"]!["$flagged"]!.GetValue<bool>().ShouldBeTrue();

            await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/messages/{id}",
                """{"flag":{"flagStatus":"complete","completedDateTime":{"dateTime":"2026-11-21T09:00:00","timeZone":"UTC"}}}""");
            var (_, completed) = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/messages/{id}?$select=flag,categories");
            completed!["flag"]!["flagStatus"]!.GetValue<string>().ShouldBe("complete");
            completed["categories"]!.AsArray().Count.ShouldBe(2);
            (await _probe.GetAsync(Alice, id[2..], Ct, """["keywords"]"""))["keywords"]!.AsObject().ContainsKey("$flagged").ShouldBeFalse();
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/messages/{id}/permanentDelete");
        }
    }

    [Fact]
    public async Task Copy_makes_a_second_message_with_its_own_id_and_the_same_categories()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var id = await DeliverToSelfAsync(alice, $"Nightware parity copy {_run}");
        string? copyId = null;
        try
        {
            await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/messages/{id}", """{"categories":["Copied"]}""");

            var (status, copy) = await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/messages/{id}/copy", """{"destinationId":"sentitems"}""");

            status.ShouldBe(HttpStatusCode.Created);
            copyId = copy!["id"]!.GetValue<string>();
            copyId.ShouldNotBe(id);
            copy["categories"]!.AsArray().Single()!.GetValue<string>().ShouldBe("Copied");
            var (_, original) = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/messages/{id}?$select=parentFolderId");
            original!["parentFolderId"]!.GetValue<string>().ShouldNotBe(copy["parentFolderId"]!.GetValue<string>());
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/messages/{id}/permanentDelete");
            if (copyId is not null)
            {
                await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/messages/{copyId}/permanentDelete");
            }
        }
    }
}
