using System.Text;
using System.Text.Json.Nodes;
using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 19: <c>POST messages</c> and <c>POST sendMail</c> in MIME form.</summary>
public sealed class MimeArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly MessageId Draft = new("d.bWltZS0wMDAxQGV4YW1wbGUuY29t");

    /// <summary>The fixture's message, decoded: the port gets the MIME bytes, not the base64.</summary>
    private static bool IsFixtureMime(ReadOnlyMemory<byte> mime) => Encoding.UTF8.GetString(mime.ToArray()).Contains("Subject: Sent as MIME", StringComparison.Ordinal);

    /// <summary>As the mail-write fixtures: the fixture pins down the members it names; the façade returns the whole message.</summary>
    private static readonly Action<JsonNode, JsonNode> FixtureMembersOnly = static (expected, actual) =>
    {
        if (expected is JsonObject e && actual is JsonObject a)
        {
            foreach (var key in a.Select(static p => p.Key).Where(k => !e.ContainsKey(k)).ToList())
            {
                a.Remove(key);
            }
        }
    };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("mail.messages.create-mime", static fakes =>
        {
            fakes.For<IDraftStore>().CreateFromMimeAsync(Me, Arg.Is<ReadOnlyMemory<byte>>(m => IsFixtureMime(m)), Arg.Any<CancellationToken>()).Returns(Draft);
            fakes.For<IMailMessageReader>().GetAsync(Me, Draft, MessageFetch.Default, Arg.Any<CancellationToken>())
                .Returns(new MailMessage(Draft) { Subject = "Sent as MIME", IsDraft = true });
        })
        {
            Normalise = FixtureMembersOnly,
        };

        yield return new ConformanceArrangement("mail.messages.send-mime", static fakes =>
            fakes.For<IMailSender>().SendMimeAsync(Me, Arg.Is<ReadOnlyMemory<byte>>(m => IsFixtureMime(m)), Arg.Any<CancellationToken>()).Returns(Result.Success()));
    }
}
