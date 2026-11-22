using System.Net;
using System.Text;
using Nightware.Api.Tests.Support;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Write;

/// <summary>Phase 19: <c>POST messages</c> and <c>POST sendMail</c> with a base64 MIME body (<c>text/plain</c>).</summary>
public sealed class MimeEndpointTests : MailWriteEndpointTestBase
{
    private const string Mime = "From: me@example.com\r\nTo: sam@example.com\r\nSubject: Hi\r\n\r\nHello\r\n";

    private static bool IsMime(ReadOnlyMemory<byte> bytes) => Encoding.ASCII.GetString(bytes.ToArray()) == Mime;

    private static string Base64(string text, bool wrapped = false)
    {
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes(text));
        return wrapped ? string.Join("\r\n", encoded.Chunk(16).Select(static c => new string(c))) + "\r\n" : encoded;
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task A_MIME_draft_is_201_with_the_saved_message(string prefix, string address)
    {
        Drafts.CreateFromMimeAsync(Box(address), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(new MessageId("d.abc"));
        Reader.GetAsync(Box(address), new MessageId("d.abc"), MessageFetch.Default, Arg.Any<CancellationToken>())
            .Returns(new MailMessage(new MessageId("d.abc")) { Subject = "Hi", IsDraft = true });

        var response = await PostTextAsync($"{prefix}/messages", Base64(Mime, wrapped: true));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.ReadObjectAsync();
        body["id"]!.GetValue<string>().ShouldBe("d.abc");
        body["subject"]!.GetValue<string>().ShouldBe("Hi");
        await Drafts.Received(1).CreateFromMimeAsync(Box(address), Arg.Is<ReadOnlyMemory<byte>>(m => IsMime(m)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task A_MIME_send_is_202(string prefix, string address)
    {
        Sender.SendMimeAsync(Box(address), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(Result.Success());

        var response = await PostTextAsync($"{prefix}/sendMail", Base64(Mime));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Sender.Received(1).SendMimeAsync(Box(address), Arg.Is<ReadOnlyMemory<byte>>(m => IsMime(m)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("/v1.0/me/messages")]
    [InlineData("/v1.0/me/sendMail")]
    public async Task A_body_that_isnt_base64_is_400_ErrorMimeContentInvalidBase64String(string path)
    {
        var response = await PostTextAsync(path, "From: raw mime, not base64");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("ErrorMimeContentInvalidBase64String");
        Drafts.ReceivedCalls().ShouldBeEmpty();
        Sender.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_empty_body_is_400()
    {
        (await PostTextAsync("/v1.0/me/sendMail", "  ")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_body_over_4MB_is_413()
    {
        var response = await PostTextAsync("/v1.0/me/sendMail", new string('A', 6 * 1024 * 1024));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        Sender.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_JSON_forms_still_reach_their_own_handlers()
    {
        Sender.SendAsync(default, null!, default, Ct).ReturnsForAnyArgs(Result.Success());

        (await SendAsync(HttpMethod.Post, "/v1.0/me/sendMail", """{"message":{"subject":"x"}}""")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await Sender.DidNotReceiveWithAnyArgs().SendMimeAsync(default, default, Ct);
    }

    [Fact]
    public async Task A_rejected_send_as_is_the_backends_answer()
    {
        Sender.SendMimeAsync(default, default, Ct).ReturnsForAnyArgs(Result.Failure(Error.Forbidden("not yours")));

        (await PostTextAsync("/v1.0/me/sendMail", Base64(Mime))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
