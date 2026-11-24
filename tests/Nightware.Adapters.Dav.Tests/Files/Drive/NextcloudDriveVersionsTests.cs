using Nightware.Adapters.Dav.Files.Drive;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

using static Nightware.Adapters.Dav.Tests.Files.Drive.DriveAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Files.Drive;

public sealed class NextcloudDriveVersionsTests : IDisposable
{
    private const string Versions = DavRoot + "versions/alice/versions/1748/";
    private static readonly DriveItemRef File = DriveItemRef.ById("1748");
    private readonly DriveAdapterHarness _nc = new();

    public NextcloudDriveVersionsTests() => _nc.ServeAliceHome();

    public void Dispose() => _nc.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDriveVersions Store => _nc.Get<IDriveVersions>();

    [Fact]
    public void Is_registered_by_the_dav_adapter() => Store.ShouldBeOfType<NextcloudDriveVersions>();

    [Fact]
    public async Task A_files_versions_are_listed_newest_first_from_its_versions_folder()
    {
        _nc.Serve("PROPFIND", Versions, 207, Recorded("versions.propfind.xml"), "1");

        var versions = (await Store.ListAsync(File, Ct)).Value;

        versions.Select(static v => v.Id).ShouldBe(["1795498535", "1795498534"]);
        var current = versions[0];
        current.Size.ShouldBe(15);
        current.LastModifiedDateTime.ShouldBe(new DateTimeOffset(2026, 11, 24, 5, 35, 35, TimeSpan.Zero));
        current.ModifiedBy.ShouldBe("alice");
        current.Label.ShouldBe("checked in: probe");
        versions[1].Label.ShouldBeNull();
    }

    [Fact]
    public async Task After_a_restore_the_version_named_by_the_files_time_is_current_and_listed_first()
    {
        // Restoring 1795498534 gave the file that version's time; the listing itself doesn't change.
        _nc.Serve("SEARCH", DavRoot, 207,
            Recorded("search-by-id.xml").Replace("<oc:fileid>1748<", "<oc:fileid>1749<", StringComparison.Ordinal)
                .Replace("Sun, 22 Nov 2026 10:51:54 GMT", "Tue, 24 Nov 2026 05:35:34 GMT", StringComparison.Ordinal),
            bodyContains: "<d:literal>1749</d:literal>");
        _nc.Serve("PROPFIND", DavRoot + "versions/alice/versions/1749/", 207, Recorded("versions.propfind.xml").Replace("/1748/", "/1749/", StringComparison.Ordinal), "1");

        var versions = (await Store.ListAsync(DriveItemRef.ById("1749"), Ct)).Value;

        versions.Select(static v => v.Id).ShouldBe(["1795498534", "1795498535"]);
    }

    [Fact]
    public async Task A_folder_has_no_versions()
    {
        (await Store.ListAsync(new DriveItemRef(null, ["nwfix"]), Ct)).Error!.Code.ShouldBe(DriveErrors.NotSupportedCode);
        _nc.Requests("PROPFIND", Versions).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_version_is_read_from_its_own_resource()
    {
        _nc.Serve("GET", Versions + "1795498534", 200, "hello v1", contentType: "text/plain");

        var content = (await Store.OpenContentAsync(File, "1795498534", Ct)).Value;

        await using var stream = await content.Open(Ct);
        (await new StreamReader(stream).ReadToEndAsync(Ct)).ShouldBe("hello v1");
    }

    [Fact]
    public async Task Restoring_moves_the_version_to_the_restore_target()
    {
        _nc.Status("MOVE", Versions + "1795498534", 201);

        (await Store.RestoreAsync(File, "1795498534", Ct)).IsSuccess.ShouldBeTrue();

        var move = _nc.Requests("MOVE").ShouldHaveSingleItem().RequestMessage!;
        move.Headers!["Destination"].Single().ShouldEndWith("/remote.php/dav/versions/alice/restore/target");
    }

    [Theory]
    [InlineData("")]
    [InlineData("../1")]
    [InlineData("abc")]
    [InlineData("12345678901234567890")]
    public async Task A_version_id_that_is_no_timestamp_is_not_found_without_asking(string id)
    {
        (await Store.OpenContentAsync(File, id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Store.RestoreAsync(File, id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _nc.Requests("GET").ShouldBeEmpty();
        _nc.Requests("MOVE").ShouldBeEmpty();
    }

    [Fact]
    public async Task Checking_out_locks_the_file_for_the_caller()
    {
        _nc.Status("LOCK", Home + "nwfix/a.txt", 200);

        (await Store.CheckOutAsync(File, Ct)).IsSuccess.ShouldBeTrue();

        _nc.Requests("LOCK").ShouldHaveSingleItem().RequestMessage!.Headers!["X-User-Lock"].Single().ShouldBe("1");
    }

    [Fact]
    public async Task A_file_someone_else_checked_out_is_locked()
    {
        _nc.Status("LOCK", Home + "nwfix/a.txt", 423);

        var error = (await Store.CheckOutAsync(File, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Conflict);
        error.Code.ShouldBe(NextcloudDriveVersions.LockedCode);
    }

    [Fact]
    public async Task Checking_in_with_a_comment_unlocks_and_labels_the_current_version()
    {
        _nc.Status("UNLOCK", Home + "nwfix/a.txt", 200);
        _nc.Serve("PROPFIND", Versions, 207, Recorded("versions.propfind.xml"), "1");
        _nc.Serve("PROPPATCH", Versions + "1795498535", 207, "<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"/>");

        (await Store.CheckInAsync(File, "ready for review", Ct)).IsSuccess.ShouldBeTrue();

        _nc.Requests("PROPPATCH").ShouldHaveSingleItem().RequestMessage!.Body!.ShouldContain("ready for review");
    }

    [Fact]
    public async Task Checking_in_without_a_comment_only_unlocks()
    {
        _nc.Status("UNLOCK", Home + "nwfix/a.txt", 200);

        (await Store.CheckInAsync(File, null, Ct)).IsSuccess.ShouldBeTrue();

        _nc.Requests("PROPFIND", Versions).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(412, ErrorKind.Validation)]
    [InlineData(423, ErrorKind.Forbidden)]
    public async Task Checking_in_what_isnt_checked_out_to_the_caller_is_refused(int status, ErrorKind kind)
    {
        _nc.Status("UNLOCK", Home + "nwfix/a.txt", status);

        (await Store.CheckInAsync(File, null, Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Without_files_lock_checking_out_is_not_supported()
    {
        // files_lock off: Nextcloud answers nc:lock as an unknown property (404 propstat).
        _nc.Serve("SEARCH", DavRoot, 207,
            Recorded("search-by-id.xml").Replace("<oc:fileid>1748<", "<oc:fileid>1750<", StringComparison.Ordinal)
                .Replace("</d:multistatus>", string.Empty, StringComparison.Ordinal)
                .Replace("</d:response>", "<d:propstat><d:prop><nc:lock/></d:prop><d:status>HTTP/1.1 404 Not Found</d:status></d:propstat></d:response></d:multistatus>", StringComparison.Ordinal),
            bodyContains: "<d:literal>1750</d:literal>");

        var error = (await Store.CheckOutAsync(DriveItemRef.ById("1750"), Ct)).Error!;

        (error.Kind, error.Code).ShouldBe((ErrorKind.Unsupported, DriveErrors.NotSupportedCode));
        _nc.Requests("LOCK").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_folder_is_never_checked_out()
    {
        (await Store.CheckOutAsync(new DriveItemRef(null, ["nwfix"]), Ct)).Error!.Code.ShouldBe(DriveErrors.NotSupportedCode);
        _nc.Requests("LOCK").ShouldBeEmpty();
    }
}
