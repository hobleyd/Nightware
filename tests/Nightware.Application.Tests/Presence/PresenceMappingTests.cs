using Nightware.Application.Presence;
using Nightware.Domain.Kernel;
using Nightware.Domain.Presence;

namespace Nightware.Application.Tests.Presence;

/// <summary>ADR 0009 D6's table, both ways.</summary>
public sealed class PresenceMappingTests
{
    private static UserPresence Read(UserStatus status, bool own = true) =>
        PresenceMapping.ToPresence("id-1", status, own, OutOfOfficeSettings.NotOutOfOffice);

    [Theory]
    [InlineData(UserStatusType.Online, null, "Available", "Available")]
    [InlineData(UserStatusType.Away, null, "Away", "Away")]
    [InlineData(UserStatusType.Away, "be-right-back", "BeRightBack", "BeRightBack")]
    [InlineData(UserStatusType.Online, "be-right-back", "BeRightBack", "BeRightBack")]
    [InlineData(UserStatusType.Busy, "call", "Busy", "InACall")]
    [InlineData(UserStatusType.Busy, "meeting", "Busy", "InAMeeting")]
    [InlineData(UserStatusType.Busy, null, "Busy", "Busy")]
    [InlineData(UserStatusType.Busy, "commuting", "Busy", "Busy")]
    [InlineData(UserStatusType.DoNotDisturb, "call", "DoNotDisturb", "DoNotDisturb")]
    [InlineData(UserStatusType.DoNotDisturb, null, "DoNotDisturb", "DoNotDisturb")]
    [InlineData(UserStatusType.Offline, null, "Offline", "Offline")]
    [InlineData(UserStatusType.Invisible, null, "Offline", "OffWork")]
    public void The_callers_own_status_reads_per_the_table(UserStatusType type, string? messageId, string availability, string activity)
    {
        var presence = Read(new UserStatus(type) { MessageId = messageId });

        presence.Availability.ShouldBe(availability);
        presence.Activity.ShouldBe(activity);
        presence.Id.ShouldBe("id-1");
    }

    [Fact]
    public void No_status_is_offline() =>
        Read(UserStatus.None, own: false).ShouldBe(new UserPresence("id-1", "Offline", "Offline"));

    [Fact]
    public void Invisible_is_off_work_only_for_the_caller() =>
        Read(new UserStatus(UserStatusType.Invisible), own: false).Activity.ShouldBe("Offline");

    [Theory]
    [InlineData(UserStatusType.Busy, PredefinedMessages.CallIcon, "InACall")]
    [InlineData(UserStatusType.Busy, PredefinedMessages.MeetingIcon, "InAMeeting")]
    [InlineData(UserStatusType.Away, PredefinedMessages.BeRightBackIcon, "BeRightBack")]
    public void Another_users_predefined_message_is_known_by_its_icon(UserStatusType type, string icon, string activity)
    {
        var presence = Read(new UserStatus(type) { Message = "localised", Icon = icon }, own: false);

        presence.Activity.ShouldBe(activity);
        presence.StatusMessage.ShouldBeNull();
    }

    [Fact]
    public void The_callers_own_custom_message_with_a_calendar_icon_is_no_meeting()
    {
        var presence = Read(new UserStatus(UserStatusType.Busy) { Message = "Dentist", Icon = PredefinedMessages.MeetingIcon });

        presence.Activity.ShouldBe("Busy");
        presence.StatusMessage!.Content.ShouldBe(PredefinedMessages.MeetingIcon + " Dentist");
    }

    [Fact]
    public void A_message_keeps_its_expiry_only_for_the_caller()
    {
        var clearAt = new DateTimeOffset(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
        var status = new UserStatus(UserStatusType.DoNotDisturb) { Message = " Focus time ", ClearAt = clearAt };

        Read(status).StatusMessage.ShouldBe(new PresenceStatusMessage("Focus time") { ExpiryDateTime = clearAt });
        Read(status, own: false).StatusMessage.ShouldBe(new PresenceStatusMessage("Focus time"));
    }

    [Fact]
    public void An_activity_message_is_not_a_status_message() =>
        Read(new UserStatus(UserStatusType.Busy) { MessageId = "call", Message = "In a call", Icon = PredefinedMessages.CallIcon }).StatusMessage.ShouldBeNull();

    [Fact]
    public void Out_of_office_is_carried()
    {
        var settings = new OutOfOfficeSettings(true, "Back Monday");

        PresenceMapping.ToPresence("id-1", UserStatus.None, true, settings).OutOfOffice.ShouldBe(settings);
    }

    [Theory]
    [InlineData("Available", "Available", UserStatusType.Online, null)]
    [InlineData("available", "AVAILABLE", UserStatusType.Online, null)]
    [InlineData("Away", "Away", UserStatusType.Away, null)]
    [InlineData("BeRightBack", "BeRightBack", UserStatusType.Away, "be-right-back")]
    [InlineData("Busy", "Busy", UserStatusType.Busy, null)]
    [InlineData("Busy", "InACall", UserStatusType.Busy, "call")]
    [InlineData("Busy", "InAConferenceCall", UserStatusType.Busy, "call")]
    [InlineData("Busy", "InAMeeting", UserStatusType.Busy, "meeting")]
    [InlineData("DoNotDisturb", "DoNotDisturb", UserStatusType.DoNotDisturb, null)]
    [InlineData("DoNotDisturb", "Presenting", UserStatusType.DoNotDisturb, null)]
    [InlineData("Offline", "OffWork", UserStatusType.Invisible, null)]
    public void A_graph_pair_sets_a_status_and_maybe_a_message(string availability, string activity, UserStatusType type, string? messageId) =>
        PresenceMapping.ToStatus(availability, activity).Value.ShouldBe(new StatusChange(type, messageId));

    [Theory]
    [InlineData("Available", "Busy")]
    [InlineData("Offline", "Offline")]
    [InlineData(null, "Available")]
    [InlineData("Available", " ")]
    public void Another_pair_is_refused(string? availability, string? activity) =>
        PresenceMapping.ToStatus(availability, activity).Error!.Kind.ShouldBe(ErrorKind.Validation);
}
