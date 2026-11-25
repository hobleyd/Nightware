using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Dav.Chats;
using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Changes;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>The change stream's chat area (ADR 0007 D5) over alice's recorded room list.</summary>
public sealed class TalkChatChangeProbeTests : IDisposable
{
    private static readonly MailboxRef Alice = new("alice@example.com");
    private readonly TalkTestServer _talk = new();
    private readonly IUserContext _user = Substitute.For<IUserContext>();

    public TalkChatChangeProbeTests() =>
        _user.Principal.Returns(new Principal("id-alice", "alice@example.com", "Alice@Example.com", "Alice Anderson"));

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<TalkRoom> Recorded()
    {
        using var rooms = JsonDocument.Parse(Fixture.Read("Chats/rooms.json"));
        return [.. rooms.RootElement.GetProperty("ocs").GetProperty("data").EnumerateArray().Select(TalkRoom.Parse)];
    }

    [Fact]
    public async Task The_state_is_each_chat_and_channel_with_its_newest_message_read_marker_unread_count_and_name()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Rooms, "rooms.json");
        var probe = new TalkChatChangeProbe(_talk.Client, _user);

        var state = (await probe.StateAsync(Alice, Ct)).Value;

        probe.Area.ShouldBe(ChangeArea.Chat);
        state.ShouldBe(
            "5opkrcg6=80,80,0,17:p23 probe renamed,17:p23 probe renamed;" +
            "indwxaa7=50,48,1,7:General,7:General;" +
            "nge9dkks=53,25,0,3:bob,9:Bob Brown;" +
            "oygp6e5d=61,1,0,25:recording test 1795387932,25:recording test 1795387932;");
        // The changelog, the note to self and the sample room are not chats (ADR 0009 D3).
        state.ShouldNotContain("cznasz4i");
        state.ShouldNotContain("jxcmpbgy");
        state.ShouldNotContain("q43sgukv");
        TalkTestServer.Query(_talk.Last, "noStatusUpdate").ShouldBe("1");
    }

    [Fact]
    public void A_new_message_a_read_a_rename_and_a_new_chat_each_move_the_state()
    {
        var rooms = Recorded();
        var before = TalkChatChangeProbe.State(rooms);
        var bob = rooms.Single(r => r.Token == "nge9dkks");
        IReadOnlyList<TalkRoom> With(TalkRoom changed) => [.. rooms.Select(r => r.Token == changed.Token ? changed : r)];

        TalkChatChangeProbe.State(With(bob with { LastMessage = bob.LastMessage! with { Id = 54 } })).ShouldNotBe(before);
        TalkChatChangeProbe.State(With(bob with { LastReadMessage = 53 })).ShouldNotBe(before);
        TalkChatChangeProbe.State(With(bob with { UnreadMessages = 1 })).ShouldNotBe(before);
        TalkChatChangeProbe.State(With(bob with { DisplayName = "Bob B." })).ShouldNotBe(before);
        TalkChatChangeProbe.State([.. rooms, bob with { Token = "zzzzzzzz" }]).ShouldNotBe(before);
        TalkChatChangeProbe.State([.. rooms.Reverse()]).ShouldBe(before, "listing order does not matter");
    }

    [Fact]
    public async Task Another_mailbox_has_no_chats_and_talk_is_not_asked()
    {
        var probe = new TalkChatChangeProbe(_talk.Client, _user);

        (await probe.StateAsync(new MailboxRef("shared@example.com"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        _talk.Requests.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ErrorKind.BackendAuthFailed, ErrorKind.Forbidden)]
    [InlineData(ErrorKind.Unauthorized, ErrorKind.Forbidden)]
    [InlineData(ErrorKind.BackendUnavailable, ErrorKind.BackendUnavailable)]
    public async Task Talk_refusing_the_caller_drops_the_area_rather_than_ending_the_stream(ErrorKind talk, ErrorKind reported)
    {
        var dav = Substitute.For<IDavClient>();
        dav.OcsGetAsync(TalkChatClient.RoomsPath, Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new Error(talk, "Talk", "no"));
        var probe = new TalkChatChangeProbe(new TalkChatClient(dav), _user);

        (await probe.StateAsync(Alice, Ct)).Error!.Kind.ShouldBe(reported);
    }

    [Fact]
    public void The_chats_adapter_offers_the_probe_beside_the_others()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IDavClient>());
        services.AddDavChats(new ConfigurationBuilder().Build());
        services.AddDavChats(new ConfigurationBuilder().Build());

        services.Count(d => d.ServiceType == typeof(IChangeStateProbe)).ShouldBe(1);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetServices<IChangeStateProbe>().ShouldHaveSingleItem().ShouldBeOfType<TalkChatChangeProbe>();
    }

    [Fact]
    public async Task Without_a_signed_in_user_there_is_no_chat_area()
    {
        var probe = new TalkChatChangeProbe(_talk.Client, null);

        (await probe.StateAsync(Alice, Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        _talk.Requests.ShouldBeEmpty();
    }
}
