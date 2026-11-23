using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Application.Tests.OnlineMeetings;

public sealed class OnlineMeetingUseCaseTests
{
    private const string Id = "t.abcd1234";
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly OnlineMeeting Meeting = new(Id, "https://cloud.example.com/call/abcd1234", "abcd1234") { Subject = "Review" };
    private static readonly MeetingArtifact Recording = new("42", MeetingArtifactKind.Recording, Start, Start.AddHours(1));

    private readonly IOnlineMeetingStore _store = Substitute.For<IOnlineMeetingStore>();
    private readonly IMeetingArtifacts _artifacts = Substitute.For<IMeetingArtifacts>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_passes_a_valid_meeting_to_the_store()
    {
        var asked = new NewOnlineMeeting("Review", Start, Start.AddHours(1), ["bob@example.com"]);
        _store.CreateAsync(asked, Ct).Returns(Meeting);

        (await new CreateOnlineMeeting(_store).ExecuteAsync(asked, Ct)).Value.ShouldBeSameAs(Meeting);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Create_refuses_half_a_span(bool start, bool end)
    {
        var result = await new CreateOnlineMeeting(_store).ExecuteAsync(new NewOnlineMeeting(null, start ? Start : null, end ? Start : null, []), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task Create_refuses_an_end_before_the_start_and_a_nameless_attendee()
    {
        (await new CreateOnlineMeeting(_store).ExecuteAsync(new NewOnlineMeeting(null, Start, Start.AddMinutes(-1), []), Ct)).Error!.Target.ShouldBe("endDateTime");
        (await new CreateOnlineMeeting(_store).ExecuteAsync(new NewOnlineMeeting(null, null, null, [" "]), Ct)).Error!.Target.ShouldBe("participants");
    }

    [Fact]
    public async Task Get_and_delete_pass_through()
    {
        _store.GetAsync(Id, Ct).Returns(Meeting);
        _store.DeleteAsync(Id, Ct).Returns(Result.Success());

        (await new GetOnlineMeeting(_store).ExecuteAsync(Id, Ct)).Value.ShouldBeSameAs(Meeting);
        (await new DeleteOnlineMeeting(_store).ExecuteAsync(Id, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Find_is_a_list_of_the_meeting_or_none()
    {
        _store.FindByJoinUrlAsync("https://cloud.example.com/call/abcd1234", Ct).Returns(Meeting);
        _store.FindByJoinUrlAsync("https://elsewhere.example.com/x", Ct).Returns(Error.NotFound("no"));
        _store.FindByJoinUrlAsync("https://broken.example.com/x", Ct).Returns(Error.BackendUnavailable("down"));
        var find = new FindOnlineMeetingByJoinUrl(_store);

        (await find.ExecuteAsync("https://cloud.example.com/call/abcd1234", Ct)).Value.ShouldBe([Meeting]);
        (await find.ExecuteAsync("https://elsewhere.example.com/x", Ct)).Value.ShouldBeEmpty();
        (await find.ExecuteAsync("https://broken.example.com/x", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await find.ExecuteAsync(" ", Ct)).Error!.Target.ShouldBe("$filter");
    }

    [Fact]
    public async Task Update_needs_both_ends_of_a_span()
    {
        var change = new OnlineMeetingChange(true, "New", Start, Start.AddHours(1));
        _store.UpdateAsync(Id, change, Ct).Returns(Meeting);
        var update = new UpdateOnlineMeeting(_store);

        (await update.ExecuteAsync(Id, change, Ct)).Value.ShouldBeSameAs(Meeting);
        (await update.ExecuteAsync(Id, new OnlineMeetingChange(false, null, Start, null), Ct)).Error!.Target.ShouldBe("startDateTime");
        (await update.ExecuteAsync(Id, new OnlineMeetingChange(false, null, Start, Start.AddHours(-1)), Ct)).Error!.Target.ShouldBe("endDateTime");
    }

    [Fact]
    public async Task Artifacts_are_listed_with_their_meeting_and_a_missing_meeting_is_not_found()
    {
        _store.GetAsync(Id, Ct).Returns(Meeting);
        _store.GetAsync("t.gone", Ct).Returns(Error.NotFound("gone"));
        _artifacts.ListAsync(Id, MeetingArtifactKind.Recording, Ct).Returns(Result<IReadOnlyList<MeetingArtifact>>.Success([Recording]));
        var list = new ListMeetingArtifacts(_store, _artifacts);

        var listed = await list.ExecuteAsync(Id, MeetingArtifactKind.Recording, Ct);
        listed.Value.ShouldBe([new MeetingArtifactOf(Meeting, Recording)]);
        (await list.ExecuteAsync("t.gone", MeetingArtifactKind.Recording, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        await _artifacts.DidNotReceive().ListAsync("t.gone", Arg.Any<MeetingArtifactKind>(), Ct);
    }

    [Fact]
    public async Task One_artifact_is_found_by_id()
    {
        _store.GetAsync(Id, Ct).Returns(Meeting);
        _artifacts.ListAsync(Id, MeetingArtifactKind.Transcript, Ct).Returns(Result<IReadOnlyList<MeetingArtifact>>.Success([Recording with { Kind = MeetingArtifactKind.Transcript }]));
        var get = new GetMeetingArtifact(new ListMeetingArtifacts(_store, _artifacts));

        (await get.ExecuteAsync(Id, MeetingArtifactKind.Transcript, "42", Ct)).Value.Artifact.Id.ShouldBe("42");
        (await get.ExecuteAsync(Id, MeetingArtifactKind.Transcript, "43", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Content_is_opened_once_the_meeting_is_found()
    {
        var blob = new BlobContent("audio/ogg", 1, _ => Task.FromResult<Stream>(new MemoryStream([1])));
        _store.GetAsync(Id, Ct).Returns(Meeting);
        _store.GetAsync("t.gone", Ct).Returns(Error.NotFound("gone"));
        _artifacts.OpenAsync(Id, MeetingArtifactKind.Recording, "42", Ct).Returns(blob);
        var download = new DownloadMeetingArtifact(_store, _artifacts);

        (await download.ExecuteAsync(Id, MeetingArtifactKind.Recording, "42", Ct)).Value.ShouldBeSameAs(blob);
        (await download.ExecuteAsync("t.gone", MeetingArtifactKind.Recording, "42", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}
