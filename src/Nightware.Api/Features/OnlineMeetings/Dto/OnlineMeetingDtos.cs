using System.Text.Json.Serialization;

using Nightware.Domain.OnlineMeetings;

namespace Nightware.Api.Features.OnlineMeetings.Dto;

/// <summary>
/// Graph <c>onlineMeeting</c> (ADR 0009 D7), the members the contract lists. A meeting whose room is no longer bound to a
/// span has null <c>startDateTime</c>/<c>endDateTime</c>; <c>joinMeetingId</c> is the room token, which is what Talk joins by.
/// </summary>
public sealed record OnlineMeetingDto
{
    public required string Id { get; init; }

    public DateTimeOffset? CreationDateTime { get; init; }

    public DateTimeOffset? StartDateTime { get; init; }

    public DateTimeOffset? EndDateTime { get; init; }

    public required string JoinWebUrl { get; init; }

    public string? Subject { get; init; }

    public required ChatInfoDto ChatInfo { get; init; }

    public required MeetingParticipantsDto Participants { get; init; }

    public required JoinMeetingIdSettingsDto JoinMeetingIdSettings { get; init; }

    public static OnlineMeetingDto From(OnlineMeeting meeting)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        return new OnlineMeetingDto
        {
            Id = meeting.Id,
            CreationDateTime = meeting.Created,
            StartDateTime = meeting.Start,
            EndDateTime = meeting.End,
            JoinWebUrl = meeting.JoinWebUrl,
            Subject = meeting.Subject,
            ChatInfo = new ChatInfoDto { ThreadId = meeting.ChatThreadId },
            Participants = new MeetingParticipantsDto
            {
                Organizer = meeting.Organizer is { } organizer ? MeetingParticipantDto.From(organizer) : null,
                Attendees = [.. meeting.Attendees.Select(MeetingParticipantDto.From)],
            },
            JoinMeetingIdSettings = new JoinMeetingIdSettingsDto { JoinMeetingId = meeting.JoinMeetingId, IsPasscodeRequired = meeting.IsPasscodeRequired },
        };
    }
}

/// <summary>Graph <c>chatInfo</c>: the meeting's chat is the Talk room, whose id is its token (ADR 0009 D3).</summary>
public sealed record ChatInfoDto
{
    public required string ThreadId { get; init; }

    public string MessageId { get; init; } = "0";

    public string? ReplyChainMessageId { get; init; }
}

public sealed record MeetingParticipantsDto
{
    public MeetingParticipantDto? Organizer { get; init; }

    public IReadOnlyList<MeetingParticipantDto> Attendees { get; init; } = [];
}

/// <summary>Graph <c>meetingParticipantInfo</c>. Someone the directory doesn't know has a user identity with a name and no id.</summary>
public sealed record MeetingParticipantDto
{
    public required MeetingIdentitySetDto Identity { get; init; }

    public string? Upn { get; init; }

    public required string Role { get; init; }

    public static MeetingParticipantDto From(MeetingParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        return new MeetingParticipantDto
        {
            Identity = new MeetingIdentitySetDto { User = new MeetingIdentityDto { Id = participant.UserId, DisplayName = participant.DisplayName } },
            Upn = participant.Upn,
            Role = participant.Role switch
            {
                MeetingRole.Presenter => "presenter",
                MeetingRole.CoOrganizer => "coorganizer",
                _ => "attendee",
            },
        };
    }
}

public sealed record MeetingIdentitySetDto
{
    public required MeetingIdentityDto User { get; init; }
}

/// <summary>Graph <c>identity</c>: both members are always written, null when unknown, as Graph writes them.</summary>
public sealed record MeetingIdentityDto
{
    public string? Id { get; init; }

    public string? DisplayName { get; init; }
}

public sealed record JoinMeetingIdSettingsDto
{
    public bool IsPasscodeRequired { get; init; }

    public string? JoinMeetingId { get; init; }

    public string? Passcode { get; init; }
}

/// <summary>Graph <c>callRecording</c> and <c>callTranscript</c>: the same members, with the content URL named for each.</summary>
public sealed record MeetingArtifactDto
{
    public required string Id { get; init; }

    public required string MeetingId { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public DateTimeOffset? EndDateTime { get; init; }

    public string? ContentCorrelationId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecordingContentUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TranscriptContentUrl { get; init; }

    public required OrganizerIdentitySetDto MeetingOrganizer { get; init; }

    /// <param name="meetingUrl">The meeting's own URL, <c>{version root}/me/onlineMeetings/{id}</c>.</param>
    public static MeetingArtifactDto From(OnlineMeeting meeting, MeetingArtifact artifact, string meetingUrl)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentNullException.ThrowIfNull(artifact);
        var recording = artifact.Kind == MeetingArtifactKind.Recording;
        var content = $"{meetingUrl}/{(recording ? "recordings" : "transcripts")}/{Uri.EscapeDataString(artifact.Id)}/content";
        return new MeetingArtifactDto
        {
            Id = artifact.Id,
            MeetingId = meeting.Id,
            CreatedDateTime = artifact.Created,
            EndDateTime = artifact.End,
            ContentCorrelationId = artifact.ContentCorrelationId,
            RecordingContentUrl = recording ? content : null,
            TranscriptContentUrl = recording ? null : content,
            MeetingOrganizer = new OrganizerIdentitySetDto
            {
                User = meeting.Organizer is { } organizer ? new OrganizerIdentityDto { Id = organizer.UserId, DisplayName = organizer.DisplayName } : null,
            },
        };
    }
}

/// <summary>Graph <c>identitySet</c> as recordings carry the organizer: application and device are always null.</summary>
public sealed record OrganizerIdentitySetDto
{
    public object? Application { get; init; }

    public object? Device { get; init; }

    public OrganizerIdentityDto? User { get; init; }
}

public sealed record OrganizerIdentityDto
{
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public string UserIdentityType { get; init; } = "aadUser";
}
