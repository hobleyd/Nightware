using System.Net;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Read;

/// <summary>Every Phase 2 route through the real pipeline, in its /me and /users/{user} forms, with the read ports faked.</summary>
public sealed class MailReadEndpointsTests : IAsyncLifetime
{
    private const string Me = "me@example.com";
    private const string Shared = "shared@example.com";

    private const string Invitation =
        "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nBEGIN:VEVENT\r\nUID:u1@example.com\r\nSUMMARY:Review\r\n"
        + "DTSTART:20260610T090000Z\r\nDTEND:20260610T100000Z\r\nLOCATION:Boardroom\r\nORGANIZER:mailto:alex@example.com\r\n"
        + "ATTENDEE;PARTSTAT=NEEDS-ACTION:mailto:me@example.com\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private readonly IMailFolderReader _folders = Substitute.For<IMailFolderReader>();
    private readonly IMailMessageReader _messages = Substitute.For<IMailMessageReader>();
    private readonly IMailDeltaReader _delta = Substitute.For<IMailDeltaReader>();
    private TestHost _host = null!;

    public static TheoryData<string, string> Forms() => new()
    {
        { "/v1.0/me", Me },
        { "/v1.0/users/shared@example.com", Shared },
        { "/v1.0/users/shared%40example.com", Shared },
    };

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services.AddSingleton(_folders).AddSingleton(_messages).AddSingleton(_delta));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _host.AuthorizedClient();

    private static BlobContent Blob(string contentType, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new BlobContent(contentType, bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes)));
    }

    private static MailMessage Message(string id = "e.m1") => new(new MessageId(id))
    {
        Subject = "Hello",
        From = new EmailAddress("alex@example.com", "Alex"),
        ReceivedDateTime = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero),
        ParentFolderId = new FolderId("a"),
        ConversationId = "t1",
        BodyPreview = "Hi",
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Lists_top_level_folders_with_a_next_link(string prefix, string mailbox)
    {
        _folders.ListAsync(new MailboxRef(mailbox), null, Arg.Is<ItemQuery>(q => q.Top == 1), Arg.Any<CancellationToken>())
            .Returns(new Page<MailFolder>([new MailFolder(new FolderId("a"), "Inbox", new FolderId("msgfolderroot"), 1, 3, 4)], "1"));

        var body = await (await Client.GetAsync($"{prefix}/mailFolders?$select=id,displayName,parentFolderId&$top=1", Ct)).ReadObjectAsync();

        body["value"]!.ToJsonString().ShouldBe("""[{"id":"a","displayName":"Inbox","parentFolderId":"msgfolderroot"}]""");
        body["@odata.nextLink"]!.GetValue<string>().ShouldContain("$skiptoken=1");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Gets_a_folder_by_well_known_name(string prefix, string mailbox)
    {
        _folders.GetAsync(new MailboxRef(mailbox), new FolderRef.WellKnown(WellKnownFolder.Inbox), Arg.Any<CancellationToken>())
            .Returns(new MailFolder(new FolderId("a"), "Inbox", new FolderId("msgfolderroot"), 1, 3, 4));

        var response = await Client.GetAsync($"{prefix}/mailFolders/Inbox?$select=id", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.Select(p => p.Key).ShouldBe(["@odata.context", "id"]);
        body.Str("id").ShouldBe("a");
    }

    [Theory]
    [InlineData(ErrorKind.NotFound, HttpStatusCode.NotFound)]
    [InlineData(ErrorKind.Forbidden, HttpStatusCode.Forbidden)]
    public async Task The_shared_mailbox_probe_answers_404_or_403(ErrorKind kind, HttpStatusCode status)
    {
        _folders.GetAsync(new MailboxRef(Shared), new FolderRef.WellKnown(WellKnownFolder.Inbox), Arg.Any<CancellationToken>())
            .Returns(new Error(kind, "x", "no"));

        (await Client.GetAsync("/v1.0/users/shared%40example.com/mailFolders/inbox?$select=id", Ct)).StatusCode.ShouldBe(status);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Lists_child_folders_of_an_id(string prefix, string mailbox)
    {
        _folders.ListAsync(new MailboxRef(mailbox), new FolderRef.ById(new FolderId("a")), Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new Page<MailFolder>([new MailFolder(new FolderId("f"), "Projects", new FolderId("a"), 0, 1, 12)]));

        var body = await (await Client.GetAsync($"{prefix}/mailFolders/a/childFolders", Ct)).ReadObjectAsync();

        body["value"]![0]!.Str("displayName").ShouldBe("Projects");
        body.ContainsKey("@odata.nextLink").ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task A_folder_page_is_projected_and_offset_paged(string prefix, string mailbox)
    {
        _messages.ListAsync(new MailboxRef(mailbox), new FolderRef.WellKnown(WellKnownFolder.Inbox), Arg.Is<ItemQuery>(q => q.Top == 25 && q.Skip == 0), Arg.Any<CancellationToken>())
            .Returns(new Page<MailMessage>([Message()], "25"));

        var body = await (await Client.GetAsync($"{prefix}/mailFolders/inbox/messages?%24top=25&%24skip=0&%24select=id%2Csubject%2CparentFolderId&%24orderby=receivedDateTime+desc", Ct)).ReadObjectAsync();

        body["value"]!.ToJsonString().ShouldBe("""[{"id":"e.m1","subject":"Hello","parentFolderId":"a"}]""");
        body["@odata.nextLink"]!.GetValue<string>().ShouldContain("$skiptoken=25");
        body["@odata.nextLink"]!.GetValue<string>().ShouldNotContain("$skip=");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Lists_the_whole_mailbox_and_passes_filter_rejections_on_as_400(string prefix, string mailbox)
    {
        _messages.ListAsync(new MailboxRef(mailbox), null, Arg.Is<ItemQuery>(q => q.Search != null), Arg.Any<CancellationToken>())
            .Returns(new Page<MailMessage>([Message()]));
        _messages.ListAsync(new MailboxRef(mailbox), null, Arg.Is<ItemQuery>(q => q.Filter != null), Arg.Any<CancellationToken>())
            .Returns(Error.Validation("conversationId in is not supported"));

        var search = await Client.GetAsync($"{prefix}/messages?$search=%22from:alex%22&$top=50&$select=id", Ct);
        var filtered = await Client.GetAsync($"{prefix}/messages?$filter=conversationId%20in%20('t1','t2')&$select=id", Ct);

        (await search.ReadObjectAsync())["value"]!.ToJsonString().ShouldBe("""[{"id":"e.m1"}]""");
        filtered.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await filtered.ReadObjectAsync()).Str("error.code").ShouldBe("invalidRequest");
    }

    [Fact]
    public async Task Expand_on_a_message_collection_is_a_400()
    {
        (await Client.GetAsync("/v1.0/me/messages?$expand=attachments", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Delta_starts_on_the_well_known_folder_and_follows_its_own_links(string prefix, string mailbox)
    {
        var inbox = new FolderRef.WellKnown(WellKnownFolder.Inbox);
        _delta.DeltaAsync(new MailboxRef(mailbox), inbox, null, Arg.Is<DeltaRequest>(r => r.PageSize == 50), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.More([new DeltaEntry<MailMessage>.Upsert(Message())], new DeltaToken("p2")));
        _delta.DeltaAsync(new MailboxRef(mailbox), inbox, new DeltaToken("p2"), Arg.Is<DeltaRequest>(r => r.PageSize == 50 && r.Select!.Count == 2), Arg.Any<CancellationToken>())
            .Returns(DeltaPage<MailMessage>.Done([new DeltaEntry<MailMessage>.Removed("e.m9", RemovedReason.Changed)], new DeltaToken("s1")));

        var first = await (await Client.GetAsync($"{prefix}/mailFolders/inbox/messages/delta?$select=id,isRead&$top=50", Ct)).ReadObjectAsync();
        var second = await (await Client.GetAsync(first["@odata.nextLink"]!.GetValue<string>(), Ct)).ReadObjectAsync();

        first["value"]!.ToJsonString().ShouldBe("""[{"id":"e.m1","isRead":false}]""");
        second["value"]!.ToJsonString().ShouldBe("""[{"@odata.type":"#microsoft.graph.message","id":"e.m9","@removed":{"reason":"changed"}}]""");
        second["@odata.deltaLink"]!.GetValue<string>().ShouldContain("$deltatoken=");
    }

    [Fact]
    public async Task Delta_sync_state_loss_and_bad_links_reset_the_client()
    {
        _delta.DeltaAsync(default, default!, default, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Error.SyncStateGone("gone"));

        (await Client.GetAsync("/v1.0/me/mailFolders/inbox/messages/delta", Ct)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await Client.GetAsync("/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=junk", Ct)).StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task The_reading_pane_gets_an_invitation_as_an_event_message_in_utc(string prefix, string mailbox)
    {
        var message = Message() with
        {
            CalendarPart = new CalendarPart("REQUEST", new AttachmentId("4")),
            Body = new ItemBody(BodyType.Html, "<p>Agenda</p>"),
            Attachments = [new MailAttachment(new AttachmentId("2"), "logo.png", "image/png", 9, AttachmentKind.File) { IsInline = true, ContentId = "logo" }],
        };
        _messages.GetAsync(new MailboxRef(mailbox), new MessageId("e.m1"), Arg.Is<MessageFetch>(f => f.Select == null && f.ExpandAttachments!.NavigationProperty == "attachments"), Arg.Any<CancellationToken>())
            .Returns(message);
        _messages.GetAttachmentContentAsync(new MailboxRef(mailbox), new MessageId("e.m1"), new AttachmentId("4"), Arg.Any<CancellationToken>())
            .Returns(Blob("text/calendar", Invitation));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{prefix}/messages/e.m1?$expand=attachments($select=id,name,contentType,size,isInline)");
        request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");

        var response = await Client.SendAsync(request, Ct);

        response.Headers.GetValues("Preference-Applied").Single().ShouldBe("outlook.timezone=\"UTC\"");
        var body = await response.ReadObjectAsync();
        body["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.eventMessageRequest");
        body.Str("meetingMessageType").ShouldBe("meetingRequest");
        body["startDateTime"]!.ToJsonString().ShouldBe("""{"dateTime":"2026-06-10T09:00:00.0000000","timeZone":"UTC"}""");
        body.Str("location.displayName").ShouldBe("Boardroom");
        body["attachments"]!.ToJsonString().ShouldBe("""[{"@odata.type":"#microsoft.graph.fileAttachment","id":"2","name":"logo.png","contentType":"image/png","size":9,"isInline":true}]""");
    }

    [Fact]
    public async Task The_plain_text_probe_asks_for_text_and_returns_only_what_it_selected()
    {
        _messages.GetAsync(new MailboxRef(Me), new MessageId("e.m1"), Arg.Is<MessageFetch>(f => f.PreferredBodyType == BodyType.Text && f.Select!.Contains("internetMessageHeaders")), Arg.Any<CancellationToken>())
            .Returns(Message() with
            {
                Body = new ItemBody(BodyType.Text, "Hi"),
                InternetMessageHeaders = [new InternetHeader("Content-Type", "text/plain")],
                CalendarPart = new CalendarPart("REQUEST", new AttachmentId("4")),
            });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/me/messages/e.m1?$select=internetMessageHeaders,body");
        request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");

        var response = await Client.SendAsync(request, Ct);

        response.Headers.GetValues("Preference-Applied").Single().ShouldBe("outlook.body-content-type=\"text\"");
        (await response.ReadObjectAsync()).ToJsonString().ShouldEndWith(""","id":"e.m1","body":{"contentType":"text","content":"Hi"},"internetMessageHeaders":[{"name":"Content-Type","value":"text/plain"}]}""");
        await _messages.DidNotReceiveWithAnyArgs().GetAttachmentContentAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task Message_errors_and_unsupported_expansions_are_graph_errors()
    {
        _messages.GetAsync(default, default, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Error.NotFound("gone"));

        (await Client.GetAsync("/v1.0/me/messages/e.gone", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Client.GetAsync("/v1.0/me/messages/e.m1?$expand=attachments,extensions", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Mime_is_streamed_as_graph_labels_it(string prefix, string mailbox)
    {
        _messages.GetRawAsync(new MailboxRef(mailbox), new MessageId("e.m1"), Arg.Any<CancellationToken>()).Returns(Blob("message/rfc822", "Subject: x\r\n\r\nbody"));

        var response = await Client.GetAsync($"{prefix}/messages/e.m1/$value", Ct);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Subject: x\r\n\r\nbody");
    }

    [Fact]
    public async Task Mime_of_a_missing_message_is_a_404_envelope()
    {
        _messages.GetRawAsync(default, default, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Error.NotFound("gone"));

        var response = await Client.GetAsync("/v1.0/me/messages/e.gone/$value", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("ErrorItemNotFound");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Lists_attachments(string prefix, string mailbox)
    {
        _messages.ListAttachmentsAsync(new MailboxRef(mailbox), new MessageId("e.m1"), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<MailAttachment>>.Success([new MailAttachment(new AttachmentId("2"), "q2.pdf", "application/pdf", 3, AttachmentKind.File)]));

        var body = await (await Client.GetAsync($"{prefix}/messages/e.m1/attachments?$select=id,name,size", Ct)).ReadObjectAsync();

        body["value"]!.ToJsonString().ShouldBe("""[{"id":"2","name":"q2.pdf","size":3}]""");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Gets_an_attachment_with_its_bytes(string prefix, string mailbox)
    {
        _messages.GetAttachmentAsync(new MailboxRef(mailbox), new MessageId("e.m1"), new AttachmentId("2"), true, Arg.Any<CancellationToken>())
            .Returns(new MailAttachment(new AttachmentId("2"), "q2.pdf", "application/pdf", 3, AttachmentKind.File) { ContentBytes = "PDF"u8.ToArray() });

        var body = await (await Client.GetAsync($"{prefix}/messages/e.m1/attachments/2", Ct)).ReadObjectAsync();

        body["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.fileAttachment");
        body.Str("contentBytes").ShouldBe("UERG");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Streams_attachment_content_with_its_own_type(string prefix, string mailbox)
    {
        _messages.GetAttachmentContentAsync(new MailboxRef(mailbox), new MessageId("e.m1"), new AttachmentId("3"), Arg.Any<CancellationToken>())
            .Returns(Blob("message/rfc822", "Subject: inner"));

        var response = await Client.GetAsync($"{prefix}/messages/e.m1/attachments/3/$value", Ct);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("message/rfc822");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Subject: inner");
    }

    [Fact]
    public async Task Attachment_errors_are_graph_errors()
    {
        _messages.ListAttachmentsAsync(default, default, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Error.Forbidden("no"));
        _messages.GetAttachmentAsync(default, default, default, default, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Error.NotFound("no"));
        _messages.GetAttachmentContentAsync(default, default, default, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Error.NotFound("no"));

        (await Client.GetAsync("/v1.0/me/messages/e.m1/attachments", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Client.GetAsync("/v1.0/me/messages/e.m1/attachments/9", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Client.GetAsync("/v1.0/me/messages/e.m1/attachments/9/$value", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Folder_errors_are_graph_errors()
    {
        _folders.ListAsync(default, default, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Error.BackendUnavailable("down", 7));
        _messages.ListAsync(default, default, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Error.NotFound("no folder"));

        var folders = await Client.GetAsync("/v1.0/me/mailFolders", Ct);
        folders.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        folders.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(7));
        (await Client.GetAsync("/v1.0/me/mailFolders/a/childFolders", Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await Client.GetAsync("/v1.0/me/mailFolders/zz/messages", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Beta_serves_the_same_routes()
    {
        _folders.GetAsync(new MailboxRef(Me), new FolderRef.WellKnown(WellKnownFolder.Inbox), Arg.Any<CancellationToken>())
            .Returns(new MailFolder(new FolderId("a"), "Inbox", null, 0, 0, 0));

        (await Client.GetAsync("/beta/me/mailFolders/inbox", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Count_comes_before_the_value_on_a_listing_that_offers_it()
    {
        _messages.ListAsync(Arg.Any<MailboxRef>(), Arg.Any<FolderRef?>(), Arg.Is<Nightware.Application.Common.Query.ItemQuery>(q => q.Count), Arg.Any<CancellationToken>())
            .Returns(new Page<MailMessage>([Message()]) { Total = 41 });

        var response = await Client.GetAsync("/v1.0/me/messages?$count=true&$top=1&$select=id", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.ReadObjectAsync();
        body.Select(static p => p.Key).ToList().ShouldBe(["@odata.context", "@odata.count", "value"]);
        body["@odata.count"]!.GetValue<int>().ShouldBe(41);
    }

    [Fact]
    public async Task A_route_that_does_not_offer_count_still_refuses_it()
    {
        var response = await Client.GetAsync("/v1.0/me/mailFolders?$count=true", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        _folders.ReceivedCalls().ShouldBeEmpty();
    }
}
