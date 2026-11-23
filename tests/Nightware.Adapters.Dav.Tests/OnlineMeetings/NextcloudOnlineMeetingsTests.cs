using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Tests.Talk;
using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Adapters.Dav.Tests.OnlineMeetings;

/// <summary>Online meetings as Talk rooms (ADR 0009 D7) against a WireMock Nextcloud.</summary>
public sealed class NextcloudOnlineMeetingsTests : IDisposable
{
    private const string Rooms = TalkAdapterHarness.Rooms;
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly TalkAdapterHarness _talk = new();

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IOnlineMeetingStore Store => _talk.Get<IOnlineMeetingStore>();

    [Fact]
    public async Task A_meeting_is_read_from_the_room_its_participants_and_its_first_message()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());

        var meeting = (await Store.GetAsync("t.abcd1234", Ct)).Value;

        meeting.Id.ShouldBe("t.abcd1234");
        meeting.JoinWebUrl.ShouldBe(TalkAdapterHarness.Public + "call/abcd1234");
        meeting.ChatThreadId.ShouldBe("abcd1234");
        meeting.JoinMeetingId.ShouldBe("abcd1234");
        meeting.Subject.ShouldBe("Q3 review");
        meeting.Start.ShouldBe(Start);
        meeting.End.ShouldBe(Start.AddHours(1));
        meeting.Created.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795427296));
        meeting.Organizer.ShouldBe(new MeetingParticipant("alice-id", "Alice Anderson", "alice@example.com", MeetingRole.Presenter));
        meeting.Attendees.ShouldBe([
            new MeetingParticipant("bob-id", "Bob Brown", "bob@example.com", MeetingRole.Attendee),
            new MeetingParticipant(null, "Carol Guest", null, MeetingRole.Attendee),
        ]);

        // Reading never moves alice's read marker or clears her notifications.
        var chat = _talk.Requests("GET", TalkAdapterHarness.Chat + "abcd1234").Single().RequestMessage!.Query!;
        chat["setReadMarker"].Single().ShouldBe("0");
        chat["markNotificationsAsRead"].Single().ShouldBe("0");
        chat["lookIntoFuture"].Single().ShouldBe("1");
    }

    [Fact]
    public async Task An_unbound_room_has_no_span_and_a_trimmed_history_no_creation_time()
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 200, TalkAdapterHarness.Room(objectType: "", objectId: ""));
        _talk.Ocs("GET", Rooms + "/abcd1234/participants", 200, "[]");
        _talk.Ocs("GET", TalkAdapterHarness.Chat + "abcd1234", 200, """[{"id":90,"timestamp":1795427296,"systemMessage":"","message":"hi"}]""");

        var meeting = (await Store.GetAsync("t.abcd1234", Ct)).Value;

        meeting.Start.ShouldBeNull();
        meeting.End.ShouldBeNull();
        meeting.Created.ShouldBeNull();
        meeting.Organizer.ShouldBeNull();
    }

    [Theory]
    [InlineData(2, 1)] // a group chat is no meeting
    [InlineData(3, 0)] // a public room alice doesn't take part in
    public async Task Other_rooms_are_not_meetings(int type, int participantType)
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 200, TalkAdapterHarness.Room(type: type, participantType: participantType));

        (await Store.GetAsync("t.abcd1234", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData("abcd1234")]
    [InlineData("t.UPPER")]
    [InlineData("t.ab")]
    public async Task Ids_this_adapter_did_not_issue_are_not_found_without_a_request(string id)
    {
        (await Store.GetAsync(id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Store.DeleteAsync(id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_room_is_not_found_and_an_outage_is_reported()
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 404, "null");
        _talk.Ocs("GET", Rooms + "/efgh5678", 503, "null");

        (await Store.GetAsync("t.abcd1234", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Store.GetAsync("t.efgh5678", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Create_opens_a_public_room_bound_to_the_span_with_the_known_attendees()
    {
        _talk.Ocs("POST", Rooms, 201, TalkAdapterHarness.Room());
        _talk.ServeMeeting(TalkAdapterHarness.Room());

        var meeting = await Store.CreateAsync(new NewOnlineMeeting("Q3 review", Start, Start.AddHours(1), ["bob-id", "stranger@example.com"]), Ct);

        meeting.Value.Id.ShouldBe("t.abcd1234");
        var body = JsonNode.Parse(_talk.Requests("POST", Rooms).Single().RequestMessage!.Body!)!;
        body.ToJsonString().ShouldBe("""{"roomType":3,"roomName":"Q3 review","objectType":"event","objectId":"1796115600#1796119200","participants":{"users":["bob"]}}""");
    }

    [Fact]
    public async Task Create_without_a_span_or_attendees_opens_an_unbound_untitled_room()
    {
        _talk.Ocs("POST", Rooms, 201, TalkAdapterHarness.Room());
        _talk.ServeMeeting(TalkAdapterHarness.Room(objectType: "", objectId: ""));

        (await Store.CreateAsync(new NewOnlineMeeting(null, null, null, []), Ct)).IsSuccess.ShouldBeTrue();

        _talk.Requests("POST", Rooms).Single().RequestMessage!.Body.ShouldBe("""{"roomType":3,"roomName":"Online meeting"}""");
    }

    [Fact]
    public async Task Create_fails_when_the_directory_or_talk_does()
    {
        _talk.Names.AccountNameOfAsync("flaky", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("directory down"));
        (await Store.CreateAsync(new NewOnlineMeeting(null, null, null, ["flaky"]), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        _talk.Ocs("POST", Rooms, 201, """{"token":"NOT A TOKEN"}""");
        (await Store.CreateAsync(new NewOnlineMeeting(null, null, null, []), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_rename_unbinds_the_room_first_because_talk_refuses_to_rename_a_bound_one()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());
        _talk.Ocs("DELETE", Rooms + "/abcd1234/object", 200, TalkAdapterHarness.Room(objectType: "", objectId: ""));
        _talk.Ocs("PUT", Rooms + "/abcd1234", 200, "null");

        (await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(true, "Q4 review", null, null), Ct)).IsSuccess.ShouldBeTrue();

        _talk.Requests("DELETE", Rooms + "/abcd1234/object").Count.ShouldBe(1);
        _talk.Requests("PUT", Rooms + "/abcd1234").Single().RequestMessage!.Body.ShouldBe("roomName=Q4+review");
        _talk.Server.LogEntries.Select(e => e.RequestMessage!.Method).ShouldBe(["GET", "DELETE", "PUT", "GET", "GET", "GET"]);
    }

    [Fact]
    public async Task A_changed_span_unbinds_and_the_same_span_or_subject_changes_nothing()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());
        _talk.Ocs("DELETE", Rooms + "/abcd1234/object", 200, "{}");

        await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(true, "Q3 review", Start, Start.AddHours(1)), Ct);
        _talk.Requests("DELETE").ShouldBeEmpty();
        _talk.Requests("PUT").ShouldBeEmpty();

        await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(false, null, Start, Start.AddHours(2)), Ct);
        _talk.Requests("DELETE", Rooms + "/abcd1234/object").Count.ShouldBe(1);
        _talk.Requests("PUT").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unbound_room_is_renamed_directly_and_a_failed_rename_is_reported()
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room(objectType: "", objectId: ""));
        _talk.Ocs("PUT", Rooms + "/abcd1234", 403, """{"error":"permissions"}""");

        var result = await Store.UpdateAsync("t.abcd1234", new OnlineMeetingChange(true, "Q4", Start, Start.AddHours(1)), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _talk.Requests("DELETE").ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_closes_the_room()
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 200, TalkAdapterHarness.Room());
        _talk.Ocs("DELETE", Rooms + "/abcd1234", 200, "null");

        (await Store.DeleteAsync("t.abcd1234", Ct)).IsSuccess.ShouldBeTrue();
        _talk.Requests("DELETE", Rooms + "/abcd1234").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Delete_of_a_room_that_is_not_a_meeting_touches_nothing()
    {
        _talk.Ocs("GET", Rooms + "/abcd1234", 200, TalkAdapterHarness.Room(type: 1));

        (await Store.DeleteAsync("t.abcd1234", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Requests("DELETE").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(TalkAdapterHarness.Public + "call/abcd1234")]
    [InlineData("HTTPS://CLOUD.EXAMPLE.COM/cloud/call/abcd1234/")]
    [InlineData("https%3A%2F%2Fcloud.example.com%2Fcloud%2Fcall%2Fabcd1234")]
    public async Task Find_reads_the_room_a_join_link_names(string url)
    {
        _talk.ServeMeeting(TalkAdapterHarness.Room());

        (await Store.FindByJoinUrlAsync(url, Ct)).Value.Id.ShouldBe("t.abcd1234");
    }

    [Fact]
    public async Task Find_of_another_link_is_not_found_without_a_request()
    {
        (await Store.FindByJoinUrlAsync("https://teams.microsoft.com/l/meetup-join/x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Server.LogEntries.ShouldBeEmpty();
    }
}
