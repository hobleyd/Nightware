using Nightware.Application.Calendar.OnlineMeetings;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar.Talk;

/// <summary>
/// The meetings provider when Talk is not configured (<c>Talk:PublicBaseUrl</c>): no room is ever opened, so an
/// event asking to be online is saved as it is, and rooms are never looked for.
/// </summary>
internal sealed class UnconfiguredOnlineMeetings : IOnlineMeetings
{
    public bool IsEnabled => false;

    public Task<Result<string>> OpenAsync(OnlineMeetingRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Result<string>>(Error.Unsupported("Online meetings are not configured on this server (Talk:PublicBaseUrl)."));

    public Task<Result> KeepAsync(string joinUrl, CancellationToken cancellationToken) => Task.FromResult(Result.Success());

    public Task<Result> CloseAsync(string joinUrl, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
}
