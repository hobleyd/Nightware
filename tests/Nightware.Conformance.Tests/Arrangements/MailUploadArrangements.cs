using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Auth;
using Nightware.Application.Mail;
using Nightware.Application.Mail.Write;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 12 draft attachment upload sessions (ADR 0006 D5), against captures from Microsoft 365.</summary>
public sealed class MailUploadArrangements : IConformanceArrangements
{
    private const long Size = 3670016;
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly DraftUploadFile File = new("capture.bin", "application/octet-stream", Size);

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("mail.attachments.create-upload-session", static fakes =>
            fakes.For<IDraftUploadStaging>()
                .BeginAsync(Me, new MessageId("AAMk-ref-0001="), File, Arg.Any<CancellationToken>())
                .Returns(new DraftUploadState("AAMk-ref-0003=", File, 0)))
        {
            // The URL and its expiry are minted per request (the URL seals the caller's token): compared by shape.
            Normalise = static (expected, actual) =>
            {
                actual["uploadUrl"]!.GetValue<string>().ShouldStartWith(
                    $"{ConformanceHost.GraphBase}/users/me@example.com/messages/AAMk-ref-0001%3D/attachmentSessions/AAMk-ref-0003%3D/content?authtoken=");
                foreach (var body in new[] { expected, actual })
                {
                    body["uploadUrl"] = "<uploadUrl>";
                    body["expirationDateTime"] = "<expiry>";
                }
            },
        };

        yield return new ConformanceArrangement("mail.attachments.upload-session-put", static fakes =>
            fakes.For<IDraftUploadStaging>()
                .AppendAsync(Me, new MessageId("AAMk-ref-0002="), "AAMk-ref-0003=", 0, 1966080, Size, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new DraftUploadState("AAMk-ref-0003=", File, 1966080)))
        {
            Services = static services => services.AddSingleton<IRequestCredentialSource>(new FixedCredential()),
            Normalise = static (expected, actual) =>
            {
                expected["expirationDateTime"] = "<expiry>";
                actual["expirationDateTime"] = "<expiry>";
            },
        };

        yield return new ConformanceArrangement("mail.attachments.upload-session-put-final", static fakes =>
        {
            fakes.For<IDraftUploadStaging>()
                .AppendAsync(Me, new MessageId("AAMk-ref-0002="), "AAMk-ref-0003=", 1966080, 1703936, Size, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new DraftUploadState("AAMk-ref-0003=", File, Size));
            fakes.For<IDraftUploadStaging>()
                .ReadAsync(Me, new MessageId("AAMk-ref-0002="), "AAMk-ref-0003=", Arg.Any<CancellationToken>())
                .Returns(new ReadOnlyMemory<byte>(new byte[Size]));
            fakes.For<IDraftStore>()
                .AddAttachmentAsync(Me, new MessageId("AAMk-ref-0002="), Arg.Is<NewAttachment>(a => a.Name == "capture.bin" && a.Content.Length == Size), Arg.Any<CancellationToken>())
                .Returns(new MailAttachment(new AttachmentId("AAMk-ref-0004="), "capture.bin", "application/octet-stream", Size, AttachmentKind.File));
        })
        {
            Services = static services => services.AddSingleton<IRequestCredentialSource>(new FixedCredential()),
        };
    }

    /// <summary>The fixture's URL carries no sealed token, so the credential is supplied as the upload URL would.</summary>
    private sealed class FixedCredential : IRequestCredentialSource
    {
        public bool TryResolve(HttpRequest request, [NotNullWhen(true)] out SealedTicket? ticket)
        {
            ticket = request.Path.Value!.Contains("/attachmentSessions/", StringComparison.Ordinal)
                ? new SealedTicket("conformance-upload-url-token", DateTimeOffset.MaxValue)
                : null;
            return ticket is not null;
        }
    }
}
