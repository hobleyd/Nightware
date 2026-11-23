using System.Text.Json;

using Nightware.Adapters.Dav.Talk;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Talk;

public sealed class TalkRoomsTests
{
    private static readonly TalkOptions Options = new() { PublicBaseUrl = new Uri("https://cloud.example.com/cloud") };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Join_links_round_trip_and_other_links_have_no_token()
    {
        TalkRooms.JoinUrl(Options, "abcd1234").ShouldBe("https://cloud.example.com/cloud/call/abcd1234");
        TalkRooms.TokenOf(Options, "https://cloud.example.com/cloud/call/abcd1234/").ShouldBe("abcd1234");
        TalkRooms.TokenOf(Options, "https://cloud.example.com/cloud/call/ABCD").ShouldBeNull();
        TalkRooms.TokenOf(Options, "https://elsewhere.example.com/cloud/call/abcd1234").ShouldBeNull();
        TalkRooms.TokenOf(Options, null).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => TalkRooms.JoinUrl(new TalkOptions(), "abcd1234"));
    }

    [Fact]
    public void Room_names_are_trimmed_bounded_and_never_blank()
    {
        TalkRooms.RoomName(Options, "  Review ").ShouldBe("Review");
        TalkRooms.RoomName(Options, " ").ShouldBe("Online meeting");
        TalkRooms.RoomName(Options, new string('x', 300)).Length.ShouldBe(255);
    }

    [Fact]
    public void Spans_round_trip_through_the_event_object()
    {
        var start = new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
        var id = TalkRooms.SpanObjectId(start, start.AddHours(1));

        id.ShouldBe("1796115600#1796119200");
        TalkRooms.SpanOf("event", id).ShouldBe((start, start.AddHours(1)));
        TalkRooms.SpanObjectId(start, start.AddHours(-1)).ShouldBe("1796115600#1796115600");
        TalkRooms.SpanOf("", id).ShouldBeNull();
        TalkRooms.SpanOf("event", "2#1").ShouldBeNull();
        TalkRooms.SpanOf("event", "x#1").ShouldBeNull();
        TalkRooms.SpanOf("event", null).ShouldBeNull();
    }

    [Fact]
    public void Talk_json_is_read_leniently()
    {
        var data = JsonElement.Parse("""{"s":"x","n":5,"t":"7","b":true,"a":[1,2],"big":99999999999}""");

        TalkJson.String(data, "s").ShouldBe("x");
        TalkJson.String(data, "n").ShouldBeNull();
        TalkJson.Long(data, "n").ShouldBe(5);
        TalkJson.Long(data, "t").ShouldBe(7);
        TalkJson.Long(data, "s").ShouldBeNull();
        TalkJson.Int(data, "big").ShouldBeNull();
        TalkJson.Bool(data, "b").ShouldBeTrue();
        TalkJson.Bool(data, "s").ShouldBeFalse();
        TalkJson.Items(data.GetProperty("a")).Count().ShouldBe(2);
        TalkJson.Items(data).ShouldBeEmpty();
        TalkJson.String(data.GetProperty("a"), "s").ShouldBeNull();
        TalkJson.Instant(1795417561).ShouldBe(new DateTimeOffset(2026, 11, 23, 7, 6, 1, TimeSpan.Zero));
    }

    [Fact]
    public async Task People_unknown_to_the_directory_are_null_and_an_outage_fails()
    {
        var names = Substitute.For<IUserAccountNames>();
        names.UserOfAccountAsync("bob", Ct).Returns(new DirectoryUser("bob-id", "Bob", "bob@example.com"));
        names.UserOfAccountAsync("guest", Ct).Returns(Error.NotFound("no"));
        names.UserOfAccountAsync("down", Ct).Returns(Error.BackendUnavailable("down"));
        names.AccountNameOfAsync("bob-id", Ct).Returns("bob");
        var people = new TalkPeople(names);

        (await people.UserOfAsync("bob", Ct)).Value!.Id.ShouldBe("bob-id");
        (await people.UserOfAsync("guest", Ct)).Value.ShouldBeNull();
        (await people.UserOfAsync("down", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await people.UidOfAsync("bob-id", Ct)).Value.ShouldBe("bob");
    }
}
