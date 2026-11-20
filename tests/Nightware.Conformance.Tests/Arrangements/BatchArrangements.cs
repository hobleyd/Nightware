using System.Text.Json.Nodes;

using Nightware.Application.Directory;
using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 11 <c>$batch</c> (ADR 0006 D9), against captures from Microsoft 365. What is under test is the
/// batch itself: the 200 envelope, one response per request, a failure confined to its request, 424 for a
/// failed dependency. <see cref="Normalise"/> sets aside what is not batch behaviour (see each step).
/// </summary>
public sealed class BatchArrangements : IConformanceArrangements
{
    private const string Me = "me@example.com";
    private const string InboxId = "AAMk-id-0001=";
    private const string NoSuchFolder = "nightware-no-such-folder";

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("batch.post", static fakes =>
        {
            fakes.For<IUserDirectory>()
                .GetAsync("conformance-user", Arg.Any<CancellationToken>())
                .Returns(new DirectoryUser("conformance-user", "Me Example", Me) { Mail = Me });
            InboxAndNoSuchFolder(fakes);
        })
        {
            Normalise = Normalise,
        };

        yield return new ConformanceArrangement("batch.post-depends-on", InboxAndNoSuchFolder)
        {
            Normalise = Normalise,
        };
    }

    private static void InboxAndNoSuchFolder(PortFakes fakes)
    {
        var folders = fakes.For<IMailFolderReader>();
        folders.GetAsync(new MailboxRef(Me), new FolderRef.WellKnown(WellKnownFolder.Inbox), Arg.Any<CancellationToken>())
            .Returns(new MailFolder(new FolderId(InboxId), "Inbox", new FolderId("root"), 0, 0, 6));
        folders.GetAsync(new MailboxRef(Me), new FolderRef.ById(new FolderId(NoSuchFolder)), Arg.Any<CancellationToken>())
            .Returns(Error.NotFound("The folder does not exist."));
    }

    private static void Normalise(JsonNode expected, JsonNode actual)
    {
        foreach (var body in new[] { expected, actual })
        {
            // Graph answers in whatever order requests finish; the façade answers in request order.
            var responses = body["responses"]!.AsArray();
            var sorted = responses.OrderBy(static r => r!["id"]!.GetValue<string>(), StringComparer.Ordinal)
                .Select(static r => r!.DeepClone()).ToList();
            responses.Clear();
            foreach (var response in sorted)
            {
                var r = response!.AsObject();

                // Sub-response headers are Exchange's (Cache-Control, OData-Version, x-ms-resource-unit).
                r.Remove("headers");

                // Exchange calls an unknown folder id malformed (400 ErrorInvalidIdMalformed, with a
                // stack of internal exceptions); a JMAP id has no format to violate, so the façade says
                // not found. Either way the failure is that request's alone, which is what is compared.
                if (r["body"]?["error"] is JsonObject error && r["status"]!.GetValue<int>() is not 424)
                {
                    r["status"] = "failed";
                    error["code"] = "failed";
                    error["message"] = "failed";
                    error.Remove("innererror");
                    error.Remove("innerError");
                }

                // /me with $select: the façade keeps id, which odata-subset §2 allows; Graph drops it.
                if (r["body"]?["mail"] is not null)
                {
                    r["body"]!.AsObject().Remove("id");
                }

                responses.Add(r);
            }
        }
    }
}
