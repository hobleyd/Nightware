using Nightware.Adapters.Dav.Files.Drive;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

using static Nightware.Adapters.Dav.Tests.Files.Drive.DriveAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Files.Drive;

public sealed class NextcloudDriveDeltaTests : IDisposable
{
    private readonly DriveAdapterHarness _nc = new();

    public void Dispose() => _nc.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDriveDelta Delta => _nc.Get<IDriveDelta>();

    private static DeltaToken Token(long activity, int? offset = null) => new(new DriveDeltaToken(activity, offset).Encode());

    private void ServeNewest(long id) =>
        _nc.Server.Given(Request.Create().WithPath(Activity).UsingGet().WithParam("sort", "desc"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("X-Activity-First-Known", id.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithHeader("Content-Type", "application/json").WithBody("""{"ocs":{"meta":{"statuscode":200},"data":[]}}"""));

    [Fact]
    public void Is_registered_by_the_dav_adapter() => Delta.ShouldBeOfType<NextcloudDriveDelta>();

    [Fact]
    public async Task Latest_is_only_a_token_at_the_newest_activity()
    {
        ServeNewest(2233);

        var page = (await Delta.ChangesAsync(null, 50, latest: true, Ct)).Value;

        page.Entries.ShouldBeEmpty();
        DriveDeltaToken.Decode(page.Final!.Value.Value).ShouldBe(new DriveDeltaToken(2233, null));
        _nc.Requests("GET").Single().RequestMessage!.Headers!["OCS-APIRequest"].ShouldBe(["true"]);
    }

    [Fact]
    public async Task An_initial_sync_starts_with_the_root_and_pages_by_offset_from_the_activity_before_it()
    {
        ServeNewest(2233);
        _nc.ServeAliceHome();
        _nc.Serve("SEARCH", DavRoot, 207, Recorded("search-by-id.xml"), bodyContains: "<d:literal>%</d:literal>");

        var page = (await Delta.ChangesAsync(null, 1, latest: false, Ct)).Value;

        page.Entries.Select(e => ((DeltaEntry<DriveItem>.Upsert)e).Item.Id).ShouldBe(["123", "1748"]);
        // The search asked for one more than the page; it got exactly one, so the listing is done.
        DriveDeltaToken.Decode(page.Final!.Value.Value).ShouldBe(new DriveDeltaToken(2233, null));
        _nc.Requests("SEARCH").Single().RequestMessage!.Body!.ShouldContain("<d:nresults>2</d:nresults>");
    }

    [Fact]
    public async Task A_listing_with_more_items_hands_out_a_next_page_and_skips_the_root_after_the_first()
    {
        _nc.ServeAliceHome();
        var two = Recorded("folder.propfind.xml"); // three responses (the folder, a.txt, sub): more than a page of two
        _nc.Serve("SEARCH", DavRoot, 207, two, bodyContains: "<d:literal>%</d:literal>");

        var page = (await Delta.ChangesAsync(Token(2233, 2), 2, latest: false, Ct)).Value;

        page.Entries.Count.ShouldBe(2);
        page.Entries.Select(e => ((DeltaEntry<DriveItem>.Upsert)e).Item.IsRoot).ShouldAllBe(isRoot => !isRoot);
        DriveDeltaToken.Decode(page.NextPage!.Value.Value).ShouldBe(new DriveDeltaToken(2233, 4));
    }

    [Fact]
    public async Task A_follow_up_returns_items_as_they_are_now_and_the_vanished_as_deleted()
    {
        _nc.ServeAliceHome();
        _nc.Server.Given(Request.Create().WithPath(Activity).UsingGet().WithParam("since", "2231"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("X-Activity-Last-Given", "2233")
                .WithHeader("Content-Type", "application/json").WithBody(Recorded("activities.json")));
        _nc.Serve("SEARCH", DavRoot, 207, Recorded("search-by-id.xml"), bodyContains: "<d:or>");

        var page = (await Delta.ChangesAsync(Token(2231), 50, latest: false, Ct)).Value;

        var items = page.Entries.Select(e => ((DeltaEntry<DriveItem>.Upsert)e).Item).ToList();
        items.Select(i => i.Id).ShouldBe(["1746", "1747", "1748", "1749"]);
        items.Single(i => i.Id == "1748").IsDeleted.ShouldBeFalse();
        items.Where(i => i.Id != "1748").ShouldAllBe(i => i.IsDeleted);
        DriveDeltaToken.Decode(page.Final!.Value.Value).ShouldBe(new DriveDeltaToken(2233, null));
    }

    [Fact]
    public async Task Nothing_new_is_a_304_and_the_same_token()
    {
        _nc.Server.Given(Request.Create().WithPath(Activity).UsingGet()).RespondWith(Response.Create().WithStatusCode(304));

        var page = (await Delta.ChangesAsync(Token(2233), 50, latest: false, Ct)).Value;

        page.Entries.ShouldBeEmpty();
        DriveDeltaToken.Decode(page.Final!.Value.Value).ShouldBe(new DriveDeltaToken(2233, null));
    }

    [Fact]
    public async Task Without_the_activity_app_delta_is_not_supported()
    {
        _nc.Server.Given(Request.Create().WithPath(Activity).UsingGet()).RespondWith(Response.Create().WithStatusCode(404));

        var error = (await Delta.ChangesAsync(Token(1), 50, latest: false, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Unsupported);
        error.Code.ShouldBe(DriveErrors.NotSupportedCode);
    }

    [Theory]
    [InlineData("not-base64!")]
    [InlineData("eyJhIjotMX0")]
    public async Task A_token_that_isnt_one_needs_a_resync(string token)
    {
        (await Delta.ChangesAsync(new DeltaToken(token), 50, latest: false, Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
    }

    [Fact]
    public void Activities_name_their_object_and_every_member_of_objects_once()
    {
        var parsed = NextcloudDriveDelta.Parse(Recorded("activities.json")).Value;

        parsed.Select(a => a.Id).ShouldBe([2232L, 2233L]);
        parsed[0].ObjectIds.ShouldBe([1746L, 1747L, 1748L, 1749L]);
        parsed[1].ObjectIds.ShouldBe([1749L]);
        NextcloudDriveDelta.Parse("{}").Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        NextcloudDriveDelta.Parse("nope").Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}
