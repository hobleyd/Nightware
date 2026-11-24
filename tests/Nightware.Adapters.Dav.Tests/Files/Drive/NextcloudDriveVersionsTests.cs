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
}
