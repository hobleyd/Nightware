using System.Xml.Linq;
using Nightware.Adapters.Dav.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Core;

public sealed class DavClientPropFindTests : IDisposable
{
    private readonly DavTestServer _dav = new();

    [Fact]
    public async Task PropFind_sends_depth_body_and_bearer_and_parses_multistatus()
    {
        _dav.Server
            .Given(Request.Create().WithPath("/remote.php/dav/calendars/alice/").UsingMethod("PROPFIND")
                .WithHeader("Depth", "1").WithHeader("Authorization", "Bearer " + DavTestServer.Token))
            .RespondWith(Response.Create().WithStatusCode(207).WithHeader("Content-Type", "application/xml; charset=utf-8")
                .WithBody(Fixture.Read("propfind-calendar-home.xml")));

        var result = await _dav.Client.PropFindAsync("calendars/alice/", DavDepth.One, [DavNames.DisplayName, DavNames.ResourceType], TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Responses.Count.ShouldBeGreaterThan(1);
        var body = XDocument.Parse(_dav.Server.LogEntries.Single().RequestMessage!.Body!);
        body.Root!.Name.ShouldBe(DavNs.Dav + "propfind");
        body.Descendants(DavNames.DisplayName).ShouldHaveSingleItem();
    }

    public void Dispose() => _dav.Dispose();
}
