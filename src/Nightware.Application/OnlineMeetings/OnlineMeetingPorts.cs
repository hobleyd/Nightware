using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Application.OnlineMeetings;

/// <summary>
/// The caller's online meetings (ADR 0009 D7), as the caller. A meeting that doesn't exist, isn't a meeting or isn't
/// visible to the caller is <see cref="ErrorKind.NotFound"/>; without a meetings backend every call is
/// <see cref="ErrorKind.Unsupported"/>.
/// </summary>
public interface IOnlineMeetingStore
{
    Task<Result<OnlineMeeting>> CreateAsync(NewOnlineMeeting meeting, CancellationToken cancellationToken);

    Task<Result<OnlineMeeting>> GetAsync(string meetingId, CancellationToken cancellationToken);

    /// <summary>The meeting a join link opens, including the rooms of calendar events; NotFound for any other link.</summary>
    Task<Result<OnlineMeeting>> FindByJoinUrlAsync(string joinWebUrl, CancellationToken cancellationToken);

    Task<Result<OnlineMeeting>> UpdateAsync(string meetingId, OnlineMeetingChange change, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(string meetingId, CancellationToken cancellationToken);
}

/// <summary>
/// A meeting's recordings and transcripts: the files the backend's recording server and transcription made for the
/// meeting, as shared with its conversation (every participant's), plus any of the caller's own not shared yet
/// (ADR 0009 D7).
/// </summary>
public interface IMeetingArtifacts
{
    /// <summary>Newest first. A meeting nothing was recorded for has none.</summary>
    Task<Result<IReadOnlyList<MeetingArtifact>>> ListAsync(string meetingId, MeetingArtifactKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// The artifact's content: a recording as it was recorded, a transcript as WebVTT. An id that isn't one of the
    /// meeting's artifacts of that kind is NotFound. The returned stream holds a backend response open: dispose it.
    /// </summary>
    Task<Result<BlobContent>> OpenAsync(string meetingId, MeetingArtifactKind kind, string artifactId, CancellationToken cancellationToken);
}
