using Nightware.Adapters.Dav.Calls;
using Nightware.Adapters.Dav.OnlineMeetings;
using Nightware.Application.Calls;
using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Calls;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Adapters.Dav.Tests.Talk;

/// <summary>Phase 26's ports through the real registration: Talk when configured, 501 otherwise.</summary>
public sealed class TalkRegistrationPhase26Tests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Configured_the_ports_are_talks()
    {
        using var talk = new TalkAdapterHarness();

        talk.Get<IOnlineMeetingStore>().ShouldBeOfType<NextcloudOnlineMeetings>();
        talk.Get<IMeetingArtifacts>().ShouldBeOfType<TalkMeetingArtifacts>();
        talk.Get<ICallService>().ShouldBeOfType<TalkCalls>();
    }

    [Fact]
    public async Task Unconfigured_every_operation_is_unsupported()
    {
        using var talk = new TalkAdapterHarness(configured: false);
        var meetings = talk.Get<IOnlineMeetingStore>();
        var artifacts = talk.Get<IMeetingArtifacts>();
        var calls = talk.Get<ICallService>();

        meetings.ShouldBeOfType<UnconfiguredOnlineMeetingStore>();
        calls.ShouldBeOfType<UnconfiguredCalls>();
        var failures = new[]
        {
            (await meetings.CreateAsync(new NewOnlineMeeting(null, null, null, []), Ct)).Error,
            (await meetings.GetAsync("t.abcd1234", Ct)).Error,
            (await meetings.FindByJoinUrlAsync("https://x/call/abcd1234", Ct)).Error,
            (await meetings.UpdateAsync("t.abcd1234", new OnlineMeetingChange(false, null, null, null), Ct)).Error,
            (await meetings.DeleteAsync("t.abcd1234", Ct)).Error,
            (await artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Error,
            (await artifacts.OpenAsync("t.abcd1234", MeetingArtifactKind.Recording, "1", Ct)).Error,
            (await calls.PlaceAsync(new NewCall(["bob"], ["audio"], null), Ct)).Error,
            (await calls.GetAsync("c", Ct)).Error,
            (await calls.HangUpAsync("c", Ct)).Error,
        };
        failures.ShouldAllBe(e => e!.Kind == ErrorKind.Unsupported);
        talk.Server.LogEntries.ShouldBeEmpty();
    }
}
