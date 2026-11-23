namespace Nightware.Domain.OnlineMeetings;

/// <summary>A person's part in a meeting (Graph <c>onlineMeetingRole</c>).</summary>
public enum MeetingRole
{
    Attendee,
    Presenter,
    CoOrganizer,
}

/// <summary>
/// A meeting's organizer or attendee. <see cref="UserId"/> is the directory id; null for someone the directory doesn't
/// know (a guest), who is named by <see cref="DisplayName"/> only.
/// </summary>
public sealed record MeetingParticipant(string? UserId, string? DisplayName, string? Upn, MeetingRole Role);

/// <summary>
/// An online meeting (ADR 0009 D7): a public Talk room. <see cref="Id"/> is the backend's opaque id,
/// <see cref="ChatThreadId"/> the meeting's chat (the room token). <see cref="Start"/> and <see cref="End"/> are known
/// only while the room is bound to its span.
/// </summary>
public sealed record OnlineMeeting(string Id, string JoinWebUrl, string ChatThreadId)
{
    public string? Subject { get; init; }

    public DateTimeOffset? Start { get; init; }

    public DateTimeOffset? End { get; init; }

    public DateTimeOffset? Created { get; init; }

    /// <summary>The code a participant can join with instead of the link (the room token).</summary>
    public string? JoinMeetingId { get; init; }

    public bool IsPasscodeRequired { get; init; }

    public MeetingParticipant? Organizer { get; init; }

    public IReadOnlyList<MeetingParticipant> Attendees { get; init; } = [];
}

/// <summary>
/// A meeting to create. <paramref name="Attendees"/> name directory users (id, UPN or address); a span needs both
/// <paramref name="Start"/> and <paramref name="End"/>.
/// </summary>
public sealed record NewOnlineMeeting(string? Subject, DateTimeOffset? Start, DateTimeOffset? End, IReadOnlyList<string> Attendees);

/// <summary>
/// A <c>PATCH</c> of a meeting: a new subject (when <see cref="ChangesSubject"/>), and a new span (both ends, or neither).
/// </summary>
public sealed record OnlineMeetingChange(bool ChangesSubject, string? Subject, DateTimeOffset? Start, DateTimeOffset? End)
{
    public bool ChangesSpan => Start is not null || End is not null;
}

/// <summary>A recording or a transcript of a meeting.</summary>
public enum MeetingArtifactKind
{
    Recording,
    Transcript,
}

/// <summary>
/// A meeting's recording or transcript (Graph <c>callRecording</c>, <c>callTranscript</c>). <see cref="ContentCorrelationId"/>
/// is shared by a recording and its transcript.
/// </summary>
public sealed record MeetingArtifact(string Id, MeetingArtifactKind Kind, DateTimeOffset? Created, DateTimeOffset? End)
{
    public string? ContentCorrelationId { get; init; }
}
