using System.Net;
using Nightware.Api.Tests.Support;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Write;

public sealed class MessageSendEndpointTests : MailWriteEndpointTestBase
{
    private const string Id = "e.orig";
    private const string Draft = "d.ZHJhZnRAZXhhbXBsZS5jb20";

    private void ArrangeOriginal(string address) =>
        Reader.GetAsync(Box(address), new MessageId(Id), Arg.Is<MessageFetch>(f => f.Select != null && f.Select.Contains("internetMessageHeaders")), Arg.Any<CancellationToken>())
            .Returns(new MailMessage(new MessageId(Id))
            {
                Subject = "Numbers",
                From = new EmailAddress("alex@example.com"),
                ToRecipients = [new EmailAddress(address)],
                CcRecipients = [new EmailAddress("sam@example.com")],
                InternetMessageId = "orig@example.com",
            });

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Send_draft_is_202_with_no_body(string prefix, string address)
    {
        Sender.SendDraftAsync(Box(address), new MessageId(Draft), Arg.Any<CancellationToken>()).Returns(Result.Success());

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/messages/{Draft}/send");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Send_draft_failure_is_mapped()
    {
        Sender.SendDraftAsync(default, default, Ct).ReturnsForAnyArgs(Result.Failure(new Error(ErrorKind.Forbidden, "ErrorSendAsDenied", "no")));

        var response = await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Draft}/send");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("ErrorSendAsDenied");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Send_mail_sends_the_message_with_its_attachments(string prefix, string address)
    {
        Sender.SendAsync(Box(address), Arg.Any<DraftContent>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Result.Success());

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/sendMail", """
            {"message":{"subject":"Q2","body":{"contentType":"HTML","content":"<p>x</p>"},
              "toRecipients":[{"emailAddress":{"address":"alex@example.com"}}],
              "bccRecipients":[{"emailAddress":{"address":"kim@example.com"}}],
              "attachments":[{"@odata.type":"#microsoft.graph.fileAttachment","name":"q2.csv","contentType":"text/csv","contentBytes":"cSxyZXZlbnVlCg=="}]}}
            """);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Sender.Received(1).SendAsync(Box(address), Arg.Is<DraftContent>(c =>
            c.Body == new ItemBody(BodyType.Html, "<p>x</p>")
            && c.BccRecipients.Single().Address == "kim@example.com"
            && c.Attachments.Single().Name == "q2.csv"), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_mail_honours_save_to_sent_items_false()
    {
        Sender.SendAsync(default, null!, default, Ct).ReturnsForAnyArgs(Result.Success());

        (await SendAsync(HttpMethod.Post, "/v1.0/me/sendMail", """{"message":{"subject":"x"},"saveToSentItems":false}""")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await Sender.Received(1).SendAsync(Arg.Any<MailboxRef>(), Arg.Any<DraftContent>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Send_mail_without_a_message_is_400()
    {
        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/sendMail", """{"saveToSentItems":true}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("invalidRequest");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Reply_sends_the_composed_body_with_threading(string prefix, string address)
    {
        ArrangeOriginal(address);
        Sender.SendAsync(Box(address), Arg.Any<DraftContent>(), true, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/messages/{Id}/reply",
            """{"message":{"body":{"contentType":"html","content":"<p>Thanks</p>"}}}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Sender.Received(1).SendAsync(Box(address), Arg.Is<DraftContent>(c =>
            c.Subject == "RE: Numbers"
            && c.Body == new ItemBody(BodyType.Html, "<p>Thanks</p>")
            && c.ToRecipients.Single().Address == "alex@example.com"
            && c.CcRecipients.Count == 0
            && c.InReplyTo == "orig@example.com"), true, Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Reply_all_keeps_the_others_but_not_the_mailbox(string prefix, string address)
    {
        ArrangeOriginal(address);
        Sender.SendAsync(Box(address), Arg.Any<DraftContent>(), true, Arg.Any<CancellationToken>()).Returns(Result.Success());

        (await SendAsync(HttpMethod.Post, $"{prefix}/messages/{Id}/replyAll", """{"message":{"body":{"contentType":"text","content":"ok"}}}"""))
            .StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await Sender.Received(1).SendAsync(Box(address), Arg.Is<DraftContent>(c =>
            c.ToRecipients.Single().Address == "alex@example.com"
            && c.CcRecipients.Single().Address == "sam@example.com"), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reply_with_a_comment_and_no_body_sends_the_comment_as_text()
    {
        ArrangeOriginal(TestHost.UserEmail);
        Sender.SendAsync(default, null!, default, Ct).ReturnsForAnyArgs(Result.Success());

        (await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Id}/reply", """{"comment":"Sounds good"}""")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await Sender.Received(1).SendAsync(Arg.Any<MailboxRef>(), Arg.Is<DraftContent>(c => c.Body == new ItemBody(BodyType.Text, "Sounds good")), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reply_to_a_missing_message_is_404()
    {
        Reader.GetAsync(default, default, null!, Ct).ReturnsForAnyArgs(Result<MailMessage>.Failure(Error.NotFound("gone")));

        (await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Id}/reply", """{"message":{}}""")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await Sender.DidNotReceiveWithAnyArgs().SendAsync(default, null!, default, Ct);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Forward_takes_top_level_recipients_and_the_original_attachments(string prefix, string address)
    {
        ArrangeOriginal(address);
        Sender.SendAsync(Box(address), Arg.Any<DraftContent>(), true, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/messages/{Id}/forward",
            """{"message":{"body":{"contentType":"html","content":"<p>FYI</p>"},"ccRecipients":[{"emailAddress":{"address":"kim@example.com"}}]},"toRecipients":[{"emailAddress":{"address":"sam@example.com"}}]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Sender.Received(1).SendAsync(Box(address), Arg.Is<DraftContent>(c =>
            c.Subject == "FW: Numbers"
            && c.ToRecipients.Single().Address == "sam@example.com"
            && c.CcRecipients.Single().Address == "kim@example.com"
            && c.AttachmentsFrom == new MessageId(Id)), true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Forward_with_a_recipient_without_address_is_400()
    {
        var response = await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Id}/forward", """{"toRecipients":[{"emailAddress":{}}]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Reader.DidNotReceiveWithAnyArgs().GetAsync(default, default, null!, Ct);
    }

    [Theory]
    [InlineData("createReply", false)]
    [InlineData("createReplyAll", true)]
    public async Task Create_reply_saves_a_draft_and_returns_it(string action, bool replyAll)
    {
        foreach (var (prefix, address) in new[] { ("/v1.0/me", TestHost.UserEmail), ($"/v1.0/users/{SharedAddress}", SharedAddress) })
        {
            ArrangeOriginal(address);
            Drafts.CreateAsync(Box(address), Arg.Any<DraftContent>(), Arg.Any<CancellationToken>()).Returns(new MessageId(Draft));
            Reader.GetAsync(Box(address), new MessageId(Draft), MessageFetch.Default, Arg.Any<CancellationToken>())
                .Returns(new MailMessage(new MessageId(Draft)) { IsDraft = true, Subject = "RE: Numbers" });

            var response = await SendAsync(HttpMethod.Post, $"{prefix}/messages/{Id}/{action}",
                """{"message":{"subject":"RE: Numbers","body":{"contentType":"text","content":"see attached"}}}""");

            response.StatusCode.ShouldBe(HttpStatusCode.Created);
            var body = await response.ReadObjectAsync();
            body["id"]!.GetValue<string>().ShouldBe(Draft);
            body["@odata.context"]!.GetValue<string>().ShouldEndWith($"{Context(address)}/messages/$entity");
            await Drafts.Received(1).CreateAsync(Box(address), Arg.Is<DraftContent>(c =>
                c.Subject == "RE: Numbers" && c.CcRecipients.Count == (replyAll ? 1 : 0) && c.InReplyTo == "orig@example.com"), Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task Create_reply_without_a_body_is_accepted()
    {
        ArrangeOriginal(TestHost.UserEmail);
        Drafts.CreateAsync(default, null!, Ct).ReturnsForAnyArgs(new MessageId(Draft));
        Reader.GetAsync(Arg.Any<MailboxRef>(), new MessageId(Draft), Arg.Any<MessageFetch>(), Arg.Any<CancellationToken>()).Returns(new MailMessage(new MessageId(Draft)));

        // An empty body still carries Content-Type: application/json, as every client request does.
        var response = await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Id}/createReply", string.Empty);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, text);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Create_forward_saves_a_draft_carrying_the_original_attachments(string prefix, string address)
    {
        ArrangeOriginal(address);
        Drafts.CreateAsync(Box(address), Arg.Any<DraftContent>(), Arg.Any<CancellationToken>()).Returns(new MessageId(Draft));
        Reader.GetAsync(Box(address), new MessageId(Draft), MessageFetch.Default, Arg.Any<CancellationToken>())
            .Returns(new MailMessage(new MessageId(Draft)) { IsDraft = true, HasAttachments = true });

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/messages/{Id}/createForward",
            """{"message":{"body":{"contentType":"text","content":"FYI"}},"toRecipients":[{"emailAddress":{"address":"sam@example.com"}}]}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.ReadObjectAsync())["hasAttachments"]!.GetValue<bool>().ShouldBeTrue();
        await Drafts.Received(1).CreateAsync(Box(address), Arg.Is<DraftContent>(c =>
            c.AttachmentsFrom == new MessageId(Id) && c.ToRecipients.Single().Address == "sam@example.com"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_forward_failure_is_mapped()
    {
        ArrangeOriginal(TestHost.UserEmail);
        Drafts.CreateAsync(default, null!, Ct).ReturnsForAnyArgs(Result<MessageId>.Failure(Error.Unsupported("no")));

        (await SendAsync(HttpMethod.Post, $"/v1.0/me/messages/{Id}/createForward", "{}")).StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
    }
}
