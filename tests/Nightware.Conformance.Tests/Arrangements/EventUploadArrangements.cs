using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Auth;
using Nightware.Application.Calendar.Calendars;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 19: an event attachment's $value, and event attachment upload sessions (shaped as the mail captures).</summary>
public sealed class EventUploadArrangements : IConformanceArrangements
{
    private const long Size = 3670016;
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EventId Event = new("AAMk-id-0005=");
    private static readonly EventUploadFile File = new("capture.bin", "application/octet-stream", Size);

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("calendar.events.attachments.value", static fakes =>
            fakes.For<IEventAttachmentStore>().GetContentAsync(Me, Event, new AttachmentId("AAMk-ref-0004="), Arg.Any<CancellationToken>())
                .Returns(_ => new BlobContent("text/plain", 18, _ => Task.FromResult<Stream>(new MemoryStream("Agenda: one item.\n"u8.ToArray())))));

        yield return new ConformanceArrangement("calendar.events.attachments.create-upload-session", static fakes =>
            fakes.For<IEventAttachmentUploads>().BeginAsync(Me, Event, File, Arg.Any<CancellationToken>())
                .Returns(new EventUploadState("AAMk-ref-0003=", File, 0, DateTimeOffset.UtcNow.AddHours(1))))
        {
            // The URL and its expiry are minted per request (the URL seals the caller's token): compared by shape.
            Normalise = static (expected, actual) =>
            {
                actual["uploadUrl"]!.GetValue<string>().ShouldStartWith(
                    $"{ConformanceHost.GraphBase}/users/me@example.com/events/AAMk-id-0005%3D/attachmentSessions/AAMk-ref-0003%3D/content?authtoken=");
                foreach (var body in new[] { expected, actual })
                {
                    body["uploadUrl"] = "<uploadUrl>";
                    body["expirationDateTime"] = "<expiry>";
                }
            },
        };

        yield return new ConformanceArrangement("calendar.events.attachments.upload-session-put", static fakes =>
            fakes.For<IEventAttachmentUploads>().AppendAsync(Me, Event, "AAMk-ref-0003=", 0, 1966080, Size, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new EventUploadProgress(1966080, null)))
        {
            Services = static services => services.AddSingleton<IRequestCredentialSource>(new FixedCredential()),
            Normalise = static (expected, actual) =>
            {
                expected["expirationDateTime"] = "<expiry>";
                actual["expirationDateTime"] = "<expiry>";
            },
        };

        yield return new ConformanceArrangement("calendar.events.attachments.upload-session-put-final", static fakes =>
            fakes.For<IEventAttachmentUploads>().AppendAsync(Me, Event, "AAMk-ref-0003=", 1966080, 1703936, Size, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
                .Returns(new EventUploadProgress(Size, new EventAttachment(new AttachmentId("AAMk-ref-0004="), "capture.bin", "application/octet-stream", Size))))
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
