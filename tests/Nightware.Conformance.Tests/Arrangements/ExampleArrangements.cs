using Microsoft.AspNetCore.Builder;

using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// The harness's worked example (<c>Examples/example.mail-folders.list.json</c>). A feature phase's
/// arrangement file looks like this minus <see cref="ConformanceArrangement.MapEndpoints"/>: the
/// route is the phase's real <c>Map*Endpoints</c>, and only the port fakes are arranged here.
/// </summary>
public sealed class ExampleArrangements : IConformanceArrangements
{
    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("example.mail-folders.list", static fakes =>
            fakes.For<IMailFolderReader>()
                .ListAsync(new MailboxRef("shared@example.com"), null, Arg.Is<ItemQuery>(q => q.Top == 2), Arg.Any<CancellationToken>())
                .Returns(new Page<MailFolder>(
                    [
                        new MailFolder(new FolderId("mbx-inbox"), "Inbox", new FolderId("mbx-root"), 1, 3, 120) { WellKnownName = WellKnownFolder.Inbox },
                        new MailFolder(new FolderId("mbx-trash"), "Deleted Items", new FolderId("mbx-root"), 0, 0, 7) { WellKnownName = WellKnownFolder.DeletedItems },
                    ],
                    SkipToken: "page-2")))
        {
            // Graph beta emits wellKnownName; the façade's v1.0 mailFolder DTO does not.
            IgnorePaths = ["value/*/wellKnownName"],
            MapEndpoints = static graph =>
            {
                foreach (var scope in graph.MapMailboxGroups())
                {
                    scope.MapGet("/conformanceExample/mailFolders", static async (
                        Mailbox mailbox, ODataQuery odata, IMailFolderReader folders, CancellationToken cancellationToken) =>
                        GraphResults.Match(
                            await folders.ListAsync(mailbox.Ref, null, odata.Query, cancellationToken),
                            page => GraphResults.Page(page, MessageDtoMapper.ToDto, $"{mailbox.ContextSegment}/mailFolders", odata.Query)));
                }
            },
        };
    }
}
