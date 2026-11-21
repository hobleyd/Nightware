using Nightware.Adapters.Jmap.Mail.Ids;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class JmapMailMessageReaderTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly MailboxRef Alice = new(JmapTestHost.AliceAddress);
    private static readonly FolderRef Inbox = new FolderRef.WellKnown(WellKnownFolder.Inbox);

    private static IMailMessageReader Reader(JmapTestHost host) => host.Services.CreateScope().ServiceProvider.GetRequiredService<IMailMessageReader>();

    private static JmapTestHost Host()
    {
        var host = new JmapTestHost();
        host.StubSession();
        host.StubMailboxes();
        return host;
    }

    private static void StubQuery(JmapTestHost host, string ids, int? total, params string[] emails) =>
        host.On("Email/query", $$"""
            [["Email/query",{"accountId":"c","queryState":"q1","canCalculateChanges":true,"position":0,"ids":[{{ids}}]{{(total is null ? "" : $",\"total\":{total}")}}},"c0"],
             ["Email/get",{{MailReadStubs.EmailGet(emails)}},"c1"]]
            """);

    [Fact]
    public async Task A_folder_page_queries_the_mailbox_newest_first_and_pages_by_offset()
    {
        await using var host = Host();
        // Email/get answers out of order; the query's order wins.
        StubQuery(host, "\"m2\",\"m1\"", 30, MailReadStubs.Email("m1", received: "2026-11-14T10:00:00Z"), MailReadStubs.Email("m2", received: "2026-11-14T11:00:00Z"));
        var query = new ItemQuery { Top = 2, Skip = 25, OrderBy = [new OrderBy("receivedDateTime", Nightware.Application.Common.Query.SortDirection.Descending)], Filter = new FilterNode.Compare("isRead", CompareOp.Eq, new FilterValue.Bool(false)) };

        var page = (await Reader(host).ListAsync(Alice, Inbox, query, Ct)).Value;

        page.Items.Select(m => m.Id.Value).ShouldBe(["e.m2", "e.m1"]);
        page.Items.ShouldAllBe(m => m.ParentFolderId == new FolderId("a"));
        page.SkipToken.ShouldBe("27");
        var calls = host.Calls("Email/query").ShouldHaveSingleItem();
        calls[0]!.ToJsonString().ShouldBe("""["Email/query",{"accountId":"c","filter":{"operator":"AND","conditions":[{"inMailbox":"a"},{"notKeyword":"$seen"}]},"sort":[{"property":"receivedAt","isAscending":false}],"position":25,"limit":2,"calculateTotal":true},"c0"]""");
        var get = calls[1]![1]!.AsObject();
        get["#ids"]!.ToJsonString().ShouldBe("""{"resultOf":"c0","name":"Email/query","path":"/ids"}""");
        get["properties"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(
            ["id", "blobId", "threadId", "mailboxIds", "keywords", "messageId", "receivedAt", "sentAt", "subject", "from", "sender", "to", "cc", "bcc", "replyTo",
             "preview", "hasAttachment", "bodyStructure", "header:Importance:asText", "header:X-Priority:asText"]);
        get["bodyProperties"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldContain("header:Content-Type");
    }

    [Fact]
    public async Task The_last_page_has_no_skip_token()
    {
        await using var host = Host();
        StubQuery(host, "\"m1\"", 26, MailReadStubs.Email("m1"));

        (await Reader(host).ListAsync(Alice, Inbox, new ItemQuery { Top = 25, SkipToken = "25" }, Ct)).Value.SkipToken.ShouldBeNull();
        host.Calls("Email/query")[0][0]![1]!["position"]!.GetValue<int>().ShouldBe(25);
    }

    [Fact]
    public async Task The_whole_mailbox_spans_every_folder_without_a_mailbox_lookup()
    {
        await using var host = Host();
        StubQuery(host, "\"m1\"", 1, MailReadStubs.Email("m1", mailbox: "b"));

        var page = (await Reader(host).ListAsync(Alice, null, new ItemQuery { Top = 25 }, Ct)).Value;

        page.Items.Single().ParentFolderId.ShouldBe(new FolderId("b"));
        host.Calls("Mailbox/get").ShouldBeEmpty();
        host.Calls("Email/query")[0][0]![1]!.AsObject().ContainsKey("filter").ShouldBeFalse();
    }

    [Fact]
    public async Task The_root_holds_no_mail_and_unknown_folders_are_not_found()
    {
        await using var host = Host();

        (await Reader(host).ListAsync(Alice, new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), ItemQuery.Default, Ct)).Value.Items.ShouldBeEmpty();
        (await Reader(host).ListAsync(Alice, new FolderRef.ById(new FolderId("nope")), ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        host.Calls("Email/query").ShouldBeEmpty();
    }

    [Fact]
    public async Task Untranslatable_queries_fail_before_any_request()
    {
        await using var host = Host();

        var result = await Reader(host).ListAsync(Alice, Inbox, new ItemQuery { Filter = new FilterNode.Compare("subject", CompareOp.Eq, new FilterValue.Text("x")) }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        host.ApiBodies().ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_is_mailbox_wide_and_pages_like_a_listing()
    {
        await using var host = Host();
        StubQuery(host, "\"m1\"", 120, MailReadStubs.Email("m1"));
        var search = new SearchQuery([new SearchTerm(SearchField.From, SearchOp.Contains, "alex@example.com"), new SearchTerm(SearchField.HasAttachments, SearchOp.Equals, "true")]);

        var page = (await Reader(host).ListAsync(Alice, null, new ItemQuery { Top = 50, Search = search }, Ct)).Value;

        page.Items.Count.ShouldBe(1);
        page.SkipToken.ShouldBe("1");
        host.Calls("Email/query")[0][0]![1]!.ToJsonString().ShouldBe(
            """{"accountId":"c","filter":{"operator":"AND","conditions":[{"from":"alex@example.com"},{"hasAttachment":true}]},"sort":[{"property":"receivedAt","isAscending":false}],"position":0,"limit":50,"calculateTotal":true}""");
    }

    [Fact]
    public async Task A_conversation_is_expanded_through_its_threads_minus_excluded_folders()
    {
        await using var host = Host();
        host.On("Thread/get", """[["Thread/get",{"accountId":"c","state":"t","list":[{"id":"t1","emailIds":["m1","m2","m3"]},{"id":"t2","emailIds":["m4"]}],"notFound":[]},"c0"]]""");
        host.On("Email/get", $$"""[["Email/get",{{MailReadStubs.EmailGet(
            MailReadStubs.Email("m1", thread: "t1", received: "2026-11-01T00:00:00Z"),
            MailReadStubs.Email("m2", mailbox: "b", thread: "t1"),
            MailReadStubs.Email("m3", mailbox: "e", thread: "t1", received: "2026-11-03T00:00:00Z"),
            MailReadStubs.Email("m4", thread: "t2", received: "2026-11-02T00:00:00Z"))}},"c0"]]""");
        var filter = new FilterNode.And(
            new FilterNode.In("conversationId", [new FilterValue.Text("t1"), new FilterValue.Text("t2"), new FilterValue.Text("AAQk=")]),
            new FilterNode.Compare("parentFolderId", CompareOp.Ne, new FilterValue.Text("b")));

        var page = (await Reader(host).ListAsync(Alice, null, new ItemQuery { Filter = filter, Top = 200 }, Ct)).Value;

        page.Items.Select(m => m.Id.Value).ShouldBe(["e.m3", "e.m4", "e.m1"]);
        page.SkipToken.ShouldBeNull();
        host.Calls("Thread/get").ShouldHaveSingleItem()[0]![1]!["ids"]!.ToJsonString().ShouldBe("""["t1","t2"]""");
        host.Calls("Email/get").ShouldHaveSingleItem()[0]![1]!["ids"]!.ToJsonString().ShouldBe("""["m1","m2","m3","m4"]""");
    }

    [Fact]
    public async Task A_conversation_expansion_is_capped_at_top()
    {
        await using var host = Host();
        host.On("Thread/get", """[["Thread/get",{"accountId":"c","state":"t","list":[{"id":"t1","emailIds":["m1","m2"]}],"notFound":[]},"c0"]]""");
        host.On("Email/get", $$"""[["Email/get",{{MailReadStubs.EmailGet(MailReadStubs.Email("m1", received: "2026-11-01T00:00:00Z"), MailReadStubs.Email("m2", received: "2026-11-02T00:00:00Z"))}},"c0"]]""");

        var page = (await Reader(host).ListAsync(Alice, null, new ItemQuery { Filter = new FilterNode.Compare("conversationId", CompareOp.Eq, new FilterValue.Text("t1")), Top = 1 }, Ct)).Value;

        page.Items.Select(m => m.Id.Value).ShouldBe(["e.m2"]);
    }

    [Fact]
    public async Task Ids_that_cannot_name_a_thread_are_answered_empty_without_a_request()
    {
        await using var host = Host();
        var filter = new FilterNode.Compare("conversationId", CompareOp.Eq, new FilterValue.Text("AAQkAGVm=="));

        (await Reader(host).ListAsync(Alice, null, new ItemQuery { Filter = filter }, Ct)).Value.Items.ShouldBeEmpty();
        host.ApiBodies().ShouldBeEmpty();
    }

    [Fact]
    public async Task Gets_one_message_with_bodies_and_without_headers_unless_selected()
    {
        await using var host = Host();
        host.On("Email/get", $$"""[["Email/get",{{MailReadStubs.EmailGet(MailReadStubs.Email("m1").Replace("\"preview\":\"Hi\"", "\"preview\":\"Hi\",\"textBody\":[{\"partId\":\"1\",\"type\":\"text/plain\"}],\"htmlBody\":[{\"partId\":\"1\",\"type\":\"text/plain\"}],\"attachments\":[],\"bodyValues\":{\"1\":{\"value\":\"Hi there\"}}", StringComparison.Ordinal))}},"c0"]]""");

        var message = (await Reader(host).GetAsync(Alice, new MessageId("e.m1"), MessageFetch.Default, Ct)).Value;
        await Reader(host).GetAsync(Alice, new MessageId("e.m1"), new MessageFetch(new HashSet<string> { "internetMessageHeaders", "body" }), Ct);

        message.Body.ShouldBe(new ItemBody(BodyType.Text, "Hi there"));
        var calls = host.Calls("Email/get");
        var get = calls[0][0]![1]!.AsObject();
        get["ids"]!.ToJsonString().ShouldBe("""["m1"]""");
        get["fetchHTMLBodyValues"]!.GetValue<bool>().ShouldBeTrue();
        get["fetchTextBodyValues"]!.GetValue<bool>().ShouldBeTrue();
        get["properties"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldNotContain("headers");
        calls[1][0]![1]!["properties"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldContain("headers");
    }

    [Fact]
    public async Task A_missing_message_is_not_found_and_a_malformed_id_is_a_validation_error()
    {
        await using var host = Host();
        host.On("Email/get", """[["Email/get",{"accountId":"c","state":"s","list":[],"notFound":["gone"]},"c0"]]""");

        (await Reader(host).GetAsync(Alice, new MessageId("e.gone"), MessageFetch.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Reader(host).GetAsync(Alice, new MessageId("AAMkAGVm="), MessageFetch.Default, Ct)).Error!.Code.ShouldBe("ErrorInvalidIdMalformed");
        (await Reader(host).GetRawAsync(Alice, new MessageId("e.gone"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Raw_mime_is_the_email_blob()
    {
        await using var host = Host();
        host.On("Email/get", """[["Email/get",{"accountId":"c","state":"s","list":[{"id":"m1","blobId":"Gm1"}],"notFound":[]},"c0"]]""");
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/Gm1/message.eml").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "message/rfc822").WithBody("Subject: x\r\n\r\nbody"));

        var blob = (await Reader(host).GetRawAsync(Alice, new MessageId("e.m1"), Ct)).Value;

        using var reader = new StreamReader(await blob.Open(Ct));
        (await reader.ReadToEndAsync(Ct)).ShouldBe("Subject: x\r\n\r\nbody");
        host.Calls("Email/get")[0][0]![1]!["properties"]!.ToJsonString().ShouldBe("""["id","blobId"]""");
        host.Requests("/jmap/download/c/Gm1/message.eml", "GET").ShouldHaveSingleItem().RequestMessage!.Query!["accept"].ShouldBe(["message/rfc822"]);
    }

    // Ids of WithAttachments' parts: (name, decoded size, ordinal among equal name and size).
    private static readonly AttachmentId Pdf = AttachmentItemId.Of("q2.pdf", 3, 0).ToAttachmentId();
    private static readonly AttachmentId AttachedMessage = AttachmentItemId.Of(null, 40, 0).ToAttachmentId();
    private static readonly AttachmentId Invitation = AttachmentItemId.Of(null, 9, 0).ToAttachmentId();

    private const string WithAttachments = """
        [["Email/get",{"accountId":"c","state":"s","list":[{"id":"m1","receivedAt":"2026-11-14T21:25:54Z",
          "bodyStructure":{"partId":null,"type":"multipart/mixed","subParts":[
            {"partId":"1","blobId":"G1","type":"text/plain","size":5},
            {"partId":"2","blobId":"G2","type":"application/pdf","name":"q2.pdf","size":3,"disposition":"attachment"},
            {"partId":"3","blobId":"G3","type":"message/rfc822","size":40,"disposition":"attachment"},
            {"partId":"4","blobId":"G4","type":"text/calendar","size":9,"header:Content-Type":"text/calendar; method=REQUEST"}]},
          "attachments":[
            {"partId":"2","blobId":"G2","type":"application/pdf","name":"q2.pdf","size":3,"disposition":"attachment"},
            {"partId":"3","blobId":"G3","type":"message/rfc822","size":40,"disposition":"attachment"}]}],"notFound":[]},"c0"]]
        """;

    [Fact]
    public async Task Lists_attachments_including_the_invitation_part()
    {
        await using var host = Host();
        host.On("Email/get", WithAttachments);

        var attachments = (await Reader(host).ListAttachmentsAsync(Alice, new MessageId("e.m1"), Ct)).Value;

        attachments.Select(a => (a.Id, a.Name, a.Kind)).ShouldBe(
        [
            (Pdf, "q2.pdf", AttachmentKind.File),
            (AttachedMessage, "Attached message", AttachmentKind.Item),
            (Invitation, "invite.ics", AttachmentKind.File),
        ]);
        host.Calls("Email/get")[0][0]![1]!["properties"]!.ToJsonString().ShouldBe("""["id","receivedAt","attachments","bodyStructure"]""");
    }

    [Fact]
    public async Task An_attached_message_is_named_after_its_subject()
    {
        await using var host = Host();
        host.On("Email/get", WithAttachments);
        host.On("Email/parse", """[["Email/parse",{"accountId":"c","parsed":{"G3":{"subject":" Q3 numbers "}}},"c0"]]""");

        var listed = (await Reader(host).ListAttachmentsAsync(Alice, new MessageId("e.m1"), Ct)).Value;
        var single = (await Reader(host).GetAttachmentAsync(Alice, new MessageId("e.m1"), AttachedMessage, includeContent: true, Ct)).Value;

        listed.Select(a => a.Name).ShouldBe(["q2.pdf", "Q3 numbers", "invite.ics"]);
        listed[1].Id.ShouldBe(AttachedMessage, "the id stays the part's, whatever the name");
        single.Name.ShouldBe("Q3 numbers");
        host.Calls("Email/parse")[0][0]![1]!.ToJsonString().ShouldBe("""{"accountId":"c","blobIds":["G3"],"properties":["subject"]}""");
    }

    [Fact]
    public async Task An_expanded_attached_message_is_named_after_its_subject_too()
    {
        await using var host = Host();
        host.On("Email/get", WithAttachments);
        host.On("Email/parse", """[["Email/parse",{"accountId":"c","parsed":{"G3":{"subject":"Q3 numbers"}}},"c0"]]""");

        var fetch = new MessageFetch(ExpandAttachments: new ExpandClause("attachments"));
        var message = (await Reader(host).GetAsync(Alice, new MessageId("e.m1"), fetch, Ct)).Value;

        message.Attachments.Single(a => a.Kind == AttachmentKind.Item).Name.ShouldBe("Q3 numbers");
    }

    [Fact]
    public async Task A_message_without_attached_messages_needs_no_parse()
    {
        await using var host = Host();
        host.On("Email/get", WithAttachments.Replace("\"message/rfc822\"", "\"application/zip\"", StringComparison.Ordinal));

        await Reader(host).ListAttachmentsAsync(Alice, new MessageId("e.m1"), Ct);

        host.Calls("Email/parse").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_attachment_carries_its_bytes_and_an_item_attachment_never_does()
    {
        await using var host = Host();
        host.On("Email/get", WithAttachments);
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/G2/q2.pdf").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody("PDF"));

        var file = (await Reader(host).GetAttachmentAsync(Alice, new MessageId("e.m1"), Pdf, includeContent: true, Ct)).Value;
        var item = (await Reader(host).GetAttachmentAsync(Alice, new MessageId("e.m1"), AttachedMessage, includeContent: true, Ct)).Value;
        var metadata = (await Reader(host).GetAttachmentAsync(Alice, new MessageId("e.m1"), Pdf, includeContent: false, Ct)).Value;

        Encoding.ASCII.GetString(file.ContentBytes!.Value.Span).ShouldBe("PDF");
        file.LastModifiedDateTime.ShouldBe(new DateTimeOffset(2026, 11, 14, 21, 25, 54, TimeSpan.Zero));
        item.Kind.ShouldBe(AttachmentKind.Item);
        item.ContentBytes.ShouldBeNull();
        metadata.ContentBytes.ShouldBeNull();
        host.Requests("/jmap/download/c/G2/q2.pdf", "GET").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_file_too_large_to_carry_inline_is_413_without_downloading_it()
    {
        await using var host = Host();
        host.On("Email/get", """
            [["Email/get",{"accountId":"c","state":"s","list":[{"id":"m1","receivedAt":"2026-11-14T21:25:54Z",
              "bodyStructure":{"partId":"1","blobId":"G9","type":"application/zip","name":"big.zip","size":60000000,"disposition":"attachment"},
              "attachments":[{"partId":"1","blobId":"G9","type":"application/zip","name":"big.zip","size":60000000,"disposition":"attachment"}]}],"notFound":[]},"c0"]]
            """);
        var big = AttachmentItemId.Of("big.zip", 60_000_000, 0).ToAttachmentId();

        var result = await Reader(host).GetAttachmentAsync(Alice, new MessageId("e.m1"), big, includeContent: true, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        host.Requests("/jmap/download/c/G9/big.zip", "GET").ShouldBeEmpty();
        (await Reader(host).GetAttachmentAsync(Alice, new MessageId("e.m1"), big, includeContent: false, Ct)).Value.Size.ShouldBe(60_000_000);
    }

    [Fact]
    public async Task Attachment_content_is_the_part_blob_and_an_attached_message_is_rfc822()
    {
        await using var host = Host();
        host.On("Email/get", WithAttachments);
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/G3/Attached message").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "message/rfc822").WithBody("Subject: inner"));
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/G4/invite.ics").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody("BEGIN:VCALENDAR"));

        var message = (await Reader(host).GetAttachmentContentAsync(Alice, new MessageId("e.m1"), AttachedMessage, Ct)).Value;
        var calendar = (await Reader(host).GetAttachmentContentAsync(Alice, new MessageId("e.m1"), Invitation, Ct)).Value;

        message.ContentType.ShouldBe("message/rfc822");
        host.Requests("/jmap/download/c/G3/Attached message", "GET").Single().RequestMessage!.Query!["accept"].ShouldBe(["message/rfc822"]);
        using var reader = new StreamReader(await calendar.Open(Ct));
        (await reader.ReadToEndAsync(Ct)).ShouldBe("BEGIN:VCALENDAR");
    }

    [Fact]
    public async Task Unknown_parts_and_containers_are_not_attachments()
    {
        await using var host = Host();
        host.On("Email/get", WithAttachments);

        var absent = AttachmentItemId.Of("q2.pdf", 3, ordinal: 1).ToAttachmentId();
        (await Reader(host).GetAttachmentAsync(Alice, new MessageId("e.m1"), absent, true, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Reader(host).GetAttachmentContentAsync(Alice, new MessageId("e.m1"), absent, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_malformed_attachment_id_is_400_without_a_request()
    {
        await using var host = Host();

        var result = await Reader(host).GetAttachmentContentAsync(Alice, new MessageId("e.m1"), new AttachmentId("2"), Ct);

        result.Error!.Code.ShouldBe("ErrorInvalidIdMalformed");
        host.Calls("Email/get").ShouldBeEmpty();
    }

    [Fact]
    public async Task Account_resolution_failures_are_passed_on()
    {
        await using var host = Host();
        var nobody = new MailboxRef("nobody@nightware.localhost");

        (await Reader(host).ListAsync(nobody, null, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Reader(host).GetAsync(nobody, new MessageId("e.m1"), MessageFetch.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Reader(host).ListAttachmentsAsync(nobody, new MessageId("e.m1"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Method_errors_are_mapped()
    {
        await using var host = Host();
        host.On("Email/query", """[["error",{"type":"unsupportedFilter"},"c0"],["error",{"type":"invalidResultReference"},"c1"]]""");
        host.On("Thread/get", """[["error",{"type":"serverUnavailable"},"c0"]]""");

        (await Reader(host).ListAsync(Alice, null, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        (await Reader(host).ListAsync(Alice, null, new ItemQuery { Filter = new FilterNode.Compare("conversationId", CompareOp.Eq, new FilterValue.Text("t1")) }, Ct))
            .Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}
