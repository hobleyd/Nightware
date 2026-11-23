using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Application.OnlineMeetings;

/// <summary>Graph <c>POST /me/onlineMeetings</c>.</summary>
public sealed class CreateOnlineMeeting(IOnlineMeetingStore store)
{
    public async Task<Result<OnlineMeeting>> ExecuteAsync(NewOnlineMeeting meeting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        if (OnlineMeetingRules.SpanError(meeting.Start, meeting.End) is { } invalid)
        {
            return invalid;
        }

        if (meeting.Attendees.Any(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("Every attendee needs identity.user.id or upn.", "participants");
        }

        return await store.CreateAsync(meeting, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>GET /me/onlineMeetings/{id}</c>.</summary>
public sealed class GetOnlineMeeting(IOnlineMeetingStore store)
{
    public Task<Result<OnlineMeeting>> ExecuteAsync(string meetingId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        return store.GetAsync(meetingId, cancellationToken);
    }
}

/// <summary>
/// Graph <c>GET /me/onlineMeetings?$filter=JoinWebUrl eq '…'</c>: a collection of the one meeting the link opens, or none.
/// </summary>
public sealed class FindOnlineMeetingByJoinUrl(IOnlineMeetingStore store)
{
    public async Task<Result<IReadOnlyList<OnlineMeeting>>> ExecuteAsync(string joinWebUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(joinWebUrl))
        {
            return Error.Validation("JoinWebUrl must name a meeting's join link.", "$filter");
        }

        var found = await store.FindByJoinUrlAsync(joinWebUrl, cancellationToken).ConfigureAwait(false);
        if (found.IsSuccess)
        {
            return Result<IReadOnlyList<OnlineMeeting>>.Success([found.Value]);
        }

        return found.Error.Kind == ErrorKind.NotFound ? Result<IReadOnlyList<OnlineMeeting>>.Success([]) : found.Error;
    }
}

/// <summary>Graph <c>PATCH /me/onlineMeetings/{id}</c>: the subject and the span.</summary>
public sealed class UpdateOnlineMeeting(IOnlineMeetingStore store)
{
    public async Task<Result<OnlineMeeting>> ExecuteAsync(string meetingId, OnlineMeetingChange change, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        ArgumentNullException.ThrowIfNull(change);
        if (change.ChangesSpan && (change.Start is null || change.End is null))
        {
            return Error.Validation("startDateTime and endDateTime are changed together.", "startDateTime");
        }

        if (OnlineMeetingRules.SpanError(change.Start, change.End) is { } invalid)
        {
            return invalid;
        }

        return await store.UpdateAsync(meetingId, change, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>DELETE /me/onlineMeetings/{id}</c>.</summary>
public sealed class DeleteOnlineMeeting(IOnlineMeetingStore store)
{
    public Task<Result> ExecuteAsync(string meetingId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        return store.DeleteAsync(meetingId, cancellationToken);
    }
}

/// <summary>A meeting with one of its recordings or transcripts (whose organizer and id Graph reports with it).</summary>
public sealed record MeetingArtifactOf(OnlineMeeting Meeting, MeetingArtifact Artifact);

/// <summary>
/// Graph <c>GET …/onlineMeetings/{id}/recordings</c> and <c>…/transcripts</c>. The meeting is read first, so a meeting the
/// caller can't see is NotFound rather than an empty list.
/// </summary>
public sealed class ListMeetingArtifacts(IOnlineMeetingStore store, IMeetingArtifacts artifacts)
{
    public async Task<Result<IReadOnlyList<MeetingArtifactOf>>> ExecuteAsync(string meetingId, MeetingArtifactKind kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        var meeting = await store.GetAsync(meetingId, cancellationToken).ConfigureAwait(false);
        if (!meeting.IsSuccess)
        {
            return meeting.Error;
        }

        var listed = await artifacts.ListAsync(meetingId, kind, cancellationToken).ConfigureAwait(false);
        return listed.IsSuccess
            ? Result<IReadOnlyList<MeetingArtifactOf>>.Success([.. listed.Value.Select(a => new MeetingArtifactOf(meeting.Value, a))])
            : listed.Error;
    }
}

/// <summary>Graph <c>GET …/recordings/{id}</c> and <c>…/transcripts/{id}</c>.</summary>
public sealed class GetMeetingArtifact(ListMeetingArtifacts list)
{
    public async Task<Result<MeetingArtifactOf>> ExecuteAsync(string meetingId, MeetingArtifactKind kind, string artifactId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        var listed = await list.ExecuteAsync(meetingId, kind, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        return listed.Value.FirstOrDefault(a => a.Artifact.Id == artifactId) is { } found
            ? found
            : Error.NotFound(kind == MeetingArtifactKind.Recording ? "The meeting has no such recording." : "The meeting has no such transcript.");
    }
}

/// <summary>Graph <c>GET …/recordings/{id}/content</c> and <c>…/transcripts/{id}/content</c> (WebVTT).</summary>
public sealed class DownloadMeetingArtifact(IOnlineMeetingStore store, IMeetingArtifacts artifacts)
{
    public async Task<Result<BlobContent>> ExecuteAsync(string meetingId, MeetingArtifactKind kind, string artifactId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meetingId);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        var meeting = await store.GetAsync(meetingId, cancellationToken).ConfigureAwait(false);
        return meeting.IsSuccess
            ? await artifacts.OpenAsync(meetingId, kind, artifactId, cancellationToken).ConfigureAwait(false)
            : meeting.Error;
    }
}

/// <summary>Checks shared by creating and changing a meeting.</summary>
internal static class OnlineMeetingRules
{
    public static Error? SpanError(DateTimeOffset? start, DateTimeOffset? end) =>
        (start, end) switch
        {
            (null, not null) or (not null, null) => Error.Validation("A meeting's span needs both startDateTime and endDateTime.", "startDateTime"),
            ({ } s, { } e) when e < s => Error.Validation("endDateTime must not be before startDateTime.", "endDateTime"),
            _ => null,
        };
}
