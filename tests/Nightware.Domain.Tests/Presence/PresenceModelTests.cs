using Nightware.Domain.Presence;

namespace Nightware.Domain.Tests.Presence;

public sealed class PresenceModelTests
{
    [Theory]
    [InlineData("call", true)]
    [InlineData("meeting", true)]
    [InlineData("be-right-back", true)]
    [InlineData("commuting", false)]
    [InlineData(null, false)]
    public void Only_the_call_meeting_and_be_right_back_messages_stand_for_an_activity(string? messageId, bool expected) =>
        PredefinedMessages.IsActivity(messageId).ShouldBe(expected);

    [Fact]
    public void No_status_is_offline_without_a_message() =>
        UserStatus.None.ShouldBe(new UserStatus(UserStatusType.Offline));

    [Fact]
    public void A_presence_is_not_out_of_office_unless_told() =>
        new UserPresence("id", Availability.Available, Activity.Available).OutOfOffice.ShouldBe(new OutOfOfficeSettings(false, null));
}
