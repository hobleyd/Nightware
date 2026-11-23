using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Adapters.Dav.OnlineMeetings;

/// <summary>Online meetings without <c>Talk:PublicBaseUrl</c>: every operation answers 501, as phase 22's off-switch leaves events without rooms.</summary>
internal sealed class UnconfiguredOnlineMeetingStore : IOnlineMeetingStore, IMeetingArtifacts
{
    public static readonly UnconfiguredOnlineMeetingStore Instance = new();

    private static Error Off => Error.Unsupported("Online meetings are not configured on this server (Talk:PublicBaseUrl).");

    public Task<Result<OnlineMeeting>> CreateAsync(NewOnlineMeeting meeting, CancellationToken cancellationToken) => Task.FromResult<Result<OnlineMeeting>>(Off);

    public Task<Result<OnlineMeeting>> GetAsync(string meetingId, CancellationToken cancellationToken) => Task.FromResult<Result<OnlineMeeting>>(Off);

    public Task<Result<OnlineMeeting>> FindByJoinUrlAsync(string joinWebUrl, CancellationToken cancellationToken) => Task.FromResult<Result<OnlineMeeting>>(Off);

    public Task<Result<OnlineMeeting>> UpdateAsync(string meetingId, OnlineMeetingChange change, CancellationToken cancellationToken) => Task.FromResult<Result<OnlineMeeting>>(Off);

    public Task<Result> DeleteAsync(string meetingId, CancellationToken cancellationToken) => Task.FromResult<Result>(Off);

    public Task<Result<IReadOnlyList<MeetingArtifact>>> ListAsync(string meetingId, MeetingArtifactKind kind, CancellationToken cancellationToken) =>
        Task.FromResult<Result<IReadOnlyList<MeetingArtifact>>>(Off);

    public Task<Result<BlobContent>> OpenAsync(string meetingId, MeetingArtifactKind kind, string artifactId, CancellationToken cancellationToken) =>
        Task.FromResult<Result<BlobContent>>(Off);
}
