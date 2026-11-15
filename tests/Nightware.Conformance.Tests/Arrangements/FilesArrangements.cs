using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Discovery;
using Nightware.Application.Files;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Files;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 7: documents linked from mail. The fixtures' link is <c>https://files.example.com/s/AbCdEfGh</c>, a
/// Nextcloud-shaped public share, so <c>files.example.com</c> is configured as this server's file site.
/// </summary>
public sealed class FilesArrangements : IConformanceArrangements
{
    private static readonly SharedItem Budget = new("01BYE5RZ6QN3ZWBTUFOFD3GSPGOHDJD36K", "Budget.xlsx", 24576)
    {
        MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        WebUrl = "https://files.example.com/s/AbCdEfGh",
    };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("files.shares.drive-item", static fakes =>
            fakes.For<ISharedFileReader>()
                .GetItemAsync(Arg.Is<ShareLink>(l => l.SitePath == "s/AbCdEfGh"), Arg.Any<CancellationToken>())
                .Returns(Budget))
        {
            Services = FileSite,
        };

        yield return new ConformanceArrangement("files.shares.drive-item-content", static fakes =>
        {
            fakes.For<ISharedFileReader>()
                .GetItemAsync(Arg.Is<ShareLink>(l => l.SitePath == "s/AbCdEfGh"), Arg.Any<CancellationToken>())
                .Returns(Budget);
            fakes.For<IDocumentConverter>().CanConvertToPdf(Budget.Name, Budget.MimeType).Returns(true);
        })
        {
            Services = FileSite,
        };
    }

    private static void FileSite(IServiceCollection services) =>
        services.PostConfigure<DiscoveryOptions>(static o => o.FilesBaseUrls = ["https://files.example.com"]);
}
