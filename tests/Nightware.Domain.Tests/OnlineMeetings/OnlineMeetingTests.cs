using Nightware.Domain.Calls;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Domain.Tests.OnlineMeetings;

public sealed class OnlineMeetingTests
{
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_change_names_a_span_when_either_end_is_given()
    {
        new OnlineMeetingChange(true, "x", null, null).ChangesSpan.ShouldBeFalse();
        new OnlineMeetingChange(false, null, Start, null).ChangesSpan.ShouldBeTrue();
        new OnlineMeetingChange(false, null, null, Start).ChangesSpan.ShouldBeTrue();
    }

    [Fact]
    public void A_meeting_starts_with_no_attendees_and_no_passcode()
    {
        var meeting = new OnlineMeeting("t.abcd1234", "https://cloud.example.com/call/abcd1234", "abcd1234");

        meeting.Attendees.ShouldBeEmpty();
        meeting.IsPasscodeRequired.ShouldBeFalse();
        meeting.Start.ShouldBeNull();
        new MeetingArtifact("1", MeetingArtifactKind.Recording, null, null).ContentCorrelationId.ShouldBeNull();
    }

    [Fact]
    public void A_call_starts_with_no_targets_or_modalities()
    {
        var call = new Call("c", CallState.Establishing, "abcd1234", "https://cloud.example.com/call/abcd1234");

        call.Targets.ShouldBeEmpty();
        call.RequestedModalities.ShouldBeEmpty();
        call.Source.ShouldBeNull();
        new NewCall(["bob"], ["audio"], null).Subject.ShouldBeNull();
    }
}
