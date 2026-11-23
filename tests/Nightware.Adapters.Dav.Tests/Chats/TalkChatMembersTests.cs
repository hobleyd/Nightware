using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>Chat members from the recorded participants: alice (owner, attendee 17) and bob (attendee 19).</summary>
public sealed class TalkChatMembersTests : IDisposable
{
    private const string Token = "5opkrcg6";
    private readonly TalkTestServer _talk = new();

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Members_are_the_rooms_users_with_directory_ids_and_owner_roles()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Rooms + "/" + Token + "/participants", "participants.json");

        var members = await _talk.Members().ListAsync(Token, Ct);

        members.Value.Count.ShouldBe(2);
        var alice = members.Value[0];
        alice.Id.ShouldBe("m.17");
        alice.UserId.ShouldBe("id-alice");
        alice.Email.ShouldBe("alice@example.com");
        alice.DisplayName.ShouldBe("Alice Anderson");
        alice.Roles.ShouldBe([ChatMember.OwnerRole]);
        alice.VisibleHistoryStartDateTime.ShouldBe(DateTimeOffset.MinValue);
        members.Value[1].Roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task Guests_and_teams_are_not_members_and_an_unknown_user_has_no_id()
    {
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + Token + "/participants", 200, """
            [{"attendeeId":1,"actorType":"users","actorId":"carol","displayName":"Carol","participantType":3},
             {"attendeeId":2,"actorType":"guests","actorId":"g","displayName":"Guest","participantType":4},
             {"attendeeId":3,"actorType":"circles","actorId":"team","displayName":"Team","participantType":3}]
            """);

        var member = (await _talk.Members().ListAsync(Token, Ct)).Value.ShouldHaveSingleItem();

        member.UserId.ShouldBeNull();
        member.DisplayName.ShouldBe("Carol");
    }

    [Fact]
    public async Task A_member_is_got_by_membership_id_and_a_malformed_one_is_not_found()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Rooms + "/" + Token + "/participants", "participants.json");

        (await _talk.Members().GetAsync(Token, "m.19", Ct)).Value.UserId.ShouldBe("id-bob");
        (await _talk.Members().GetAsync(Token, "m.18", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        var before = _talk.Requests.Count;
        (await _talk.Members().GetAsync(Token, "19", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _talk.Members().GetAsync(Token, "m.x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Requests.Count.ShouldBe(before);
    }

    [Fact]
    public async Task Adding_a_member_reads_the_new_attendee_back_and_promotes_an_owner()
    {
        _talk.Reply("POST", TalkTestServer.Rooms + "/" + Token + "/participants", 200);
        _talk.ReplyFixture("GET", TalkTestServer.Rooms + "/" + Token + "/participants", "participants.json");
        _talk.Reply("POST", TalkTestServer.Rooms + "/" + Token + "/moderators", 200);

        var member = await _talk.Members().AddAsync(Token, new ChatMemberDraft("id-bob", true), Ct);

        member.Value.Id.ShouldBe("m.19");
        member.Value.Roles.ShouldBe([ChatMember.OwnerRole]);
        _talk.Requests[0].RequestMessage!.Body.ShouldBe("newParticipant=bob&source=users");
        _talk.Last.RequestMessage!.Body.ShouldBe("attendeeId=19");
    }

    [Fact]
    public async Task Adding_someone_unknown_or_whom_talk_does_not_list_fails()
    {
        (await _talk.Members().AddAsync(Token, new ChatMemberDraft("id-nobody", false), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _talk.Reply("POST", TalkTestServer.Rooms + "/" + Token + "/participants", 200);
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + Token + "/participants", 200, "[]");
        (await _talk.Members().AddAsync(Token, new ChatMemberDraft("id-bob", false), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Removing_another_member_removes_the_attendee_and_removing_oneself_leaves()
    {
        _talk.Reply("GET", TalkTestServer.Rooms + "/" + Token, 200, TalkTestServer.RoomData(Token));
        _talk.Server.Given(Request.Create().WithPath(TalkTestServer.Rooms + "/" + Token + "/attendees").UsingDelete().WithParam("attendeeId", "19"))
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"ocs":{"meta":{"status":"ok","statuscode":200,"message":""},"data":[]}}"""));
        _talk.Server.Given(Request.Create().WithPath(TalkTestServer.Rooms + "/" + Token + "/attendees").UsingDelete().WithParam("attendeeId", "99"))
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"ocs":{"meta":{"status":"failure","statuscode":404,"message":""},"data":[]}}"""));
        _talk.Reply("DELETE", TalkTestServer.Rooms + "/" + Token + "/participants/self", 200);

        (await _talk.Members().RemoveAsync(Token, "m.19", Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.Members().RemoveAsync(Token, "m.17", Ct)).IsSuccess.ShouldBeTrue();
        _talk.Last.RequestMessage!.Path.ShouldEndWith("/participants/self");
        (await _talk.Members().RemoveAsync(Token, "m.99", Ct)).Error!.Message.ShouldContain("member");
        (await _talk.Members().RemoveAsync(Token, "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}
