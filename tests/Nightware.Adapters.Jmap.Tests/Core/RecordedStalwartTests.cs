using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Mail;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Jmap.Tests.Core;

/// <summary>
/// Cross-checks against real Stalwart v1.1.0 exchanges (deploy/recordings/jmap). Each test
/// builds the recorded request with the core API and asserts the wire form matches the
/// recording (call ids normalised to c0, c1, …), then reads the recorded response.
/// </summary>
public sealed class RecordedStalwartTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static JsonObject Load(string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "recorded", file)))!.AsObject();

    /// <summary>Stubs the session (behind the real 307) and the API with the recording, and returns the normalised recorded calls.</summary>
    private static JsonArray Arrange(JmapTestHost host, string name)
    {
        host.Server.Given(Request.Create().WithPath("/.well-known/jmap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(307).WithHeader("Location", "/jmap/session"));
        host.StubSession(Load("01-session.response.json").ToJsonString());

        var calls = Load($"{name}.request.json")["methodCalls"]!.AsArray();
        var ids = calls.Select((c, i) => (Recorded: c![2]!.GetValue<string>(), Ours: $"c{i}")).ToDictionary(p => p.Recorded, p => p.Ours);

        var response = Load($"{name}.response.json");
        foreach (var invocation in response["methodResponses"]!.AsArray())
        {
            invocation![2] = ids[invocation[2]!.GetValue<string>()];
        }

        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(response.ToJsonString()));

        foreach (var call in calls)
        {
            call![2] = ids[call[2]!.GetValue<string>()];
            var args = call[1]!.AsObject();
            foreach (var key in args.Where(p => p.Value is null).Select(p => p.Key).ToList())
            {
                args.Remove(key); // Stalwart treats an explicit null like an absent argument.
            }

            foreach (var (_, value) in args)
            {
                if (value is JsonObject reference && reference["resultOf"] is not null)
                {
                    reference["resultOf"] = ids[reference["resultOf"]!.GetValue<string>()];
                }
            }
        }

        return calls;
    }

    private static JmapTestHost Host() => new(sessionPath: "/.well-known/jmap");

    private static void ShouldMatchRecording(JmapTestHost host, JsonArray recorded)
    {
        var sent = host.ApiBodies().ShouldHaveSingleItem()["methodCalls"]!;
        JsonNode.DeepEquals(sent, recorded).ShouldBeTrue($"sent {sent.ToJsonString()}\nrecorded {recorded.ToJsonString()}");
    }

    [Fact]
    public async Task Session_resolves_alice_from_primary_accounts()
    {
        await using var host = Host();
        Arrange(host, "02-mailbox-get");
        var client = host.Client();

        var account = (await client.ResolveAccountAsync(new MailboxRef(JmapTestHost.AliceAddress), Ct)).Value;

        account.AccountId.ShouldBe("c");
        account.IsPrimary.ShouldBeTrue();
        account.Session.ApiUrl.ShouldBe(new Uri($"{host.Server.Url}/jmap/"));
        account.Session.State.ShouldBe("9a59b346");
        account.Session.Limits.MaxObjectsInGet.ShouldBe(500);
        (await client.ResolveAccountAsync(new MailboxRef("bob@nightware.localhost"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Mailbox_get_matches_and_maps_default_roles()
    {
        await using var host = Host();
        var recorded = Arrange(host, "02-mailbox-get");
        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.MailboxGet, new GetRequest("c"));

        var mailboxes = (await host.Client().SendAsync(request, Ct)).Bind(r => r.Get(get)).Value;

        ShouldMatchRecording(host, recorded);
        foreach (var folder in new[] { WellKnownFolder.Inbox, WellKnownFolder.Drafts, WellKnownFolder.SentItems, WellKnownFolder.JunkEmail, WellKnownFolder.DeletedItems })
        {
            MailboxRoles.Find(mailboxes.List, folder).ShouldNotBeNull(folder.ToString());
        }

        var inbox = MailboxRoles.Find(mailboxes.List, WellKnownFolder.Inbox)!;
        inbox.Id.ShouldBe("a");
        inbox.ParentId.ShouldBeNull();
        inbox.MyRights!.MayReadItems.ShouldBeTrue();
        // A sessionState equal to the session's state keeps the cached session.
        host.Requests("/jmap/session", "GET").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Email_query_matches()
    {
        await using var host = Host();
        var recorded = Arrange(host, "03-email-query");
        var request = new JmapRequestBuilder();
        var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest("c")
        {
            Filter = new EmailFilter { InMailbox = "a" },
            Sort = [new Comparator("receivedAt", IsAscending: false)],
            Position = 0,
            Limit = 25,
            CalculateTotal = true,
        });

        var result = (await host.Client().SendAsync(request, Ct)).Bind(r => r.Get(query)).Value;

        ShouldMatchRecording(host, recorded);
        result.Ids.ShouldBe(["qaaaaae", "maaaaad", "iaaaaac", "eaaaaab"]);
        result.Total.ShouldBe(4);
        result.CanCalculateChanges.ShouldBeTrue();
    }

    [Fact]
    public async Task Email_get_with_bodies_matches_and_reads_every_field()
    {
        await using var host = Host();
        var recorded = Arrange(host, "04-email-get");
        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.EmailGet, new EmailGetRequest("c")
        {
            Ids = ["qaaaaae", "maaaaad", "iaaaaac", "eaaaaab"],
            Properties =
            [
                "id", "blobId", "threadId", "mailboxIds", "keywords", "size", "receivedAt", "messageId", "inReplyTo", "references",
                "sender", "from", "to", "cc", "bcc", "replyTo", "subject", "sentAt", "hasAttachment", "preview",
                "bodyStructure", "bodyValues", "textBody", "htmlBody", "attachments", "headers",
            ],
            FetchTextBodyValues = true,
            FetchHtmlBodyValues = true,
            MaxBodyValueBytes = 262144,
        });

        var emails = (await host.Client().SendAsync(request, Ct)).Bind(r => r.Get(get)).Value;

        ShouldMatchRecording(host, recorded);
        emails.List.Count.ShouldBe(4);
        var first = emails.List[0];
        first.Id.ShouldBe("qaaaaae");
        first.ThreadId.ShouldBe("e");
        first.MailboxIds!.Keys.ShouldBe(["a"]);
        first.MessageId.ShouldHaveSingleItem().ShouldEndWith("@mail.nightware.localhost");
        first.ReceivedAt.ShouldBe(new DateTimeOffset(2026, 11, 14, 21, 25, 54, TimeSpan.Zero));
        first.BodyStructure!.PartId.ShouldBe("0");
        first.TextBody.ShouldNotBeNull().ShouldNotBeEmpty();
        first.BodyValues.ShouldNotBeNull().ShouldContainKey(first.TextBody[0].PartId!);
        first.Headers.ShouldNotBeNull().ShouldContain(h => string.Equals(h.Name, "Message-ID", StringComparison.OrdinalIgnoreCase));
        MailItemId.For(first).ToString().ShouldBe("e.qaaaaae");
    }

    [Fact]
    public async Task Query_get_thread_back_references_match()
    {
        await using var host = Host();
        var recorded = Arrange(host, "05-query-get-thread-batched");
        var request = new JmapRequestBuilder();
        var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest("c")
        {
            Sort = [new Comparator("receivedAt", IsAscending: false)],
            CollapseThreads = true,
            Limit = 10,
        });
        var emails = request.Add(JmapMethods.EmailGet, new EmailGetRequest("c")
        {
            Properties = ["threadId", "subject", "from", "receivedAt", "preview", "keywords"],
        }, query.Ref("ids", JmapPaths.Ids));
        var threads = request.Add(JmapMethods.ThreadGet, new GetRequest("c"), emails.Ref("ids", JmapPaths.ListThreadIds));

        var response = (await host.Client().SendAsync(request, Ct)).Value;

        // Our serialiser writes "#ids" after the typed properties; DeepEquals ignores member order.
        ShouldMatchRecording(host, recorded);
        response.Get(emails).Value.List[0].Subject.ShouldBe("Lunch?");
        response.Get(threads).Value.List.Select(t => t.EmailIds!.Single()).ShouldBe(response.Get(query).Value.Ids);
    }

    [Fact]
    public async Task Email_changes_matches()
    {
        await using var host = Host();
        var recorded = Arrange(host, "06-email-changes");
        var request = new JmapRequestBuilder();
        var changes = request.Add(JmapMethods.EmailChanges, new ChangesRequest("c", "saq") { MaxChanges = 100 });

        var result = (await host.Client().SendAsync(request, Ct)).Bind(r => r.Get(changes)).Value;

        ShouldMatchRecording(host, recorded);
        result.OldState.ShouldBe("saq");
        result.NewState.ShouldBe("say");
        result.Created.ShouldBe(["maaaaad", "qaaaaae"]);
        result.UpdatedProperties.ShouldBeNull();
        result.HasMoreChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task Email_set_keyword_patch_matches()
    {
        await using var host = Host();
        var recorded = Arrange(host, "07-email-set-seen");
        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.EmailSet, new SetRequest<Email>("c")
        {
            Update = new Dictionary<string, PatchObject> { ["qaaaaae"] = new Patch().SetEntry("keywords", Keywords.Seen).Build() },
        });

        var result = (await host.Client().SendAsync(request, Ct)).Bind(r => r.Get(set)).Value;

        ShouldMatchRecording(host, recorded);
        result.Updated("qaaaaae").IsSuccess.ShouldBeTrue();
        result.NewState.ShouldBe("sa2");
    }

    [Fact]
    public async Task Mailbox_changes_matches()
    {
        await using var host = Host();
        var recorded = Arrange(host, "08-mailbox-changes");
        var request = new JmapRequestBuilder();
        var changes = request.Add(JmapMethods.MailboxChanges, new ChangesRequest("c", "say"));

        var result = (await host.Client().SendAsync(request, Ct)).Bind(r => r.Get(changes)).Value;

        ShouldMatchRecording(host, recorded);
        result.Updated.ShouldBe(["a"]);
        result.UpdatedProperties.ShouldBe(["totalEmails", "unreadEmails", "totalThreads", "unreadThreads"]);
    }

    [Fact]
    public async Task Not_found_ids_and_account_not_found_errors_map()
    {
        await using var host = Host();
        var recorded = Arrange(host, "09-errors-notfound-and-accountnotfound");
        var request = new JmapRequestBuilder();
        var emails = request.Add(JmapMethods.EmailGet, new EmailGetRequest("c") { Ids = ["does-not-exist"] });
        var mailboxes = request.Add(JmapMethods.MailboxGet, new GetRequest("no-such-account"));

        var response = (await host.Client().SendAsync(request, Ct)).Value;

        ShouldMatchRecording(host, recorded);
        response.Get(emails).Value.NotFound.ShouldBe(["does-not-exist"]);
        var error = response.Get(mailboxes).Error!;
        error.Kind.ShouldBe(ErrorKind.NotFound);
        error.Message.ShouldContain("accountNotFound");
    }
}
