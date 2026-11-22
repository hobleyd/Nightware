using Nightware.Adapters.Dav.Files.Drive;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

using static Nightware.Adapters.Dav.Tests.Files.Drive.DriveAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Files.Drive;

public sealed class NextcloudDriveUploadsTests : IDisposable
{
    private const string Uploads = DavRoot + "uploads/alice/";
    private static readonly DriveItemRef Folder = new(null, ["nwfix"]);
    private readonly DriveAdapterHarness _nc = new();

    public NextcloudDriveUploadsTests() => _nc.ServeAliceHome();

    public void Dispose() => _nc.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDriveUploads Uploads_ => _nc.Get<IDriveUploads>();

    private static string StagingOf(string sessionId) => DriveUploadToken.Decode(sessionId, DateTimeOffset.UnixEpoch)!.UploadHref;

    private async Task<string> BeginAsync(string name, long size, ConflictBehavior conflict)
    {
        _nc.Server.Given(WireMock.RequestBuilders.Request.Create().UsingMethod("MKCOL").WithPath(p => p.StartsWith(Uploads, StringComparison.Ordinal)))
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(201));
        return (await Uploads_.BeginAsync(new DriveUploadFile(Folder, name, size, conflict), Ct)).Value.SessionId;
    }

    [Fact]
    public void Is_registered_by_the_dav_adapter() => Uploads_.ShouldBeOfType<NextcloudDriveUploads>();

    [Fact]
    public async Task A_session_stages_in_the_callers_uploads_and_its_token_names_the_target()
    {
        var session = await BeginAsync("big.bin", 10, ConflictBehavior.Fail);

        var token = DriveUploadToken.Decode(session, _nc.Time.GetUtcNow())!;
        token.Uid.ShouldBe("alice");
        token.UploadHref.ShouldStartWith("uploads/alice/nw-");
        token.ParentHref.ShouldBe("files/alice/nwfix/");
        token.Name.ShouldBe("big.bin");
        token.ExpiresAt.ShouldBe(_nc.Time.GetUtcNow() + NextcloudDriveUploads.SessionLifetime);
    }

    [Fact]
    public async Task A_session_onto_a_taken_name_with_fail_is_refused_before_any_bytes()
    {
        (await Uploads_.BeginAsync(new DriveUploadFile(Folder, "a.txt", 10, ConflictBehavior.Fail), Ct)).Error!.Code.ShouldBe(DriveErrors.NameAlreadyExistsCode);
        _nc.Requests("MKCOL").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_last_chunk_moves_the_joined_file_into_place()
    {
        var session = await BeginAsync("a.txt", 0, ConflictBehavior.Replace);
        var staging = DavRoot + StagingOf(session);
        _nc.Serve("PROPFIND", staging, 207, $"<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>{staging}</d:href><d:propstat><d:prop/><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>", "1");
        _nc.Status("PUT", staging + "000000000000000", 201);
        _nc.Status("MOVE", staging + ".file", 201);

        var progress = (await Uploads_.AppendAsync(session, 0, 5, 5, new MemoryStream("hello"u8.ToArray()), Ct)).Value;

        progress.Completed!.Id.ShouldBe("1748");
        var move = _nc.Requests("MOVE").Single().RequestMessage!;
        move.Headers!["Destination"].Single().ShouldEndWith("/files/alice/nwfix/a.txt");
        move.Headers["Overwrite"].ShouldBe(["T"]);
    }

    [Fact]
    public async Task A_chunk_out_of_order_or_of_another_total_is_refused()
    {
        var session = await BeginAsync("big.bin", 10, ConflictBehavior.Replace);
        var staging = DavRoot + StagingOf(session);
        _nc.Serve("PROPFIND", staging, 207, $"<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>{staging}000000000000000</d:href><d:propstat><d:prop><d:getcontentlength>4</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>", "1");

        (await Uploads_.AppendAsync(session, 0, 4, 10, Stream.Null, Ct)).Error!.Message.ShouldContain("next byte expected is 4");
        (await Uploads_.AppendAsync(session, 4, 4, 12, Stream.Null, Ct)).Error!.Target.ShouldBe("Content-Range");
        (await Uploads_.GetAsync(session, Ct)).Value.NextExpected.ShouldBe(4);
        _nc.Requests("PUT").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_chunk_shorter_than_its_range_is_removed()
    {
        var session = await BeginAsync("big.bin", 10, ConflictBehavior.Replace);
        var staging = DavRoot + StagingOf(session);
        _nc.Serve("PROPFIND", staging, 207, EmptyMultistatus, "1");
        _nc.Status("PUT", staging + "000000000000000", 201);
        _nc.Status("DELETE", staging + "000000000000000", 204);

        (await Uploads_.AppendAsync(session, 0, 4, 10, new MemoryStream([1, 2]), Ct)).Error!.Target.ShouldBe("Content-Range");
        _nc.Requests("DELETE").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Cancel_drops_the_staging_folder_and_a_forged_session_is_not_found()
    {
        var session = await BeginAsync("big.bin", 10, ConflictBehavior.Replace);
        _nc.Status("DELETE", DavRoot + StagingOf(session), 204);
        var forged = new DriveUploadToken("bob", "uploads/bob/nw-x/", "files/bob/", "x", 1, ConflictBehavior.Fail, _nc.Time.GetUtcNow().AddHours(1).ToUnixTimeSeconds()).Encode();

        (await Uploads_.CancelAsync(session, Ct)).IsSuccess.ShouldBeTrue();
        (await Uploads_.CancelAsync(forged, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Uploads_.GetAsync("garbage", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void An_expired_or_unsafe_token_does_not_decode()
    {
        var now = new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero);
        var later = now.AddHours(1).ToUnixTimeSeconds();

        DriveUploadToken.Decode(new DriveUploadToken("alice", "uploads/alice/x/", "files/alice/", "a", 1, ConflictBehavior.Fail, now.AddSeconds(-1).ToUnixTimeSeconds()).Encode(), now).ShouldBeNull();
        DriveUploadToken.Decode(new DriveUploadToken("alice", "uploads/alice/../x/", "files/alice/", "a", 1, ConflictBehavior.Fail, later).Encode(), now).ShouldBeNull();
        DriveUploadToken.Decode(new DriveUploadToken("alice", "uploads/alice/x/", "calendars/alice/", "a", 1, ConflictBehavior.Fail, later).Encode(), now).ShouldBeNull();
        DriveUploadToken.Decode(new DriveUploadToken("alice", "uploads/alice/x/", "files/alice/", "a/b", 1, ConflictBehavior.Fail, later).Encode(), now).ShouldBeNull();
        DriveUploadToken.Decode(new DriveUploadToken("alice", "uploads/alice/x/", "files/alice/", "a", 1, ConflictBehavior.Fail, later).Encode(), now).ShouldNotBeNull();
    }
}
