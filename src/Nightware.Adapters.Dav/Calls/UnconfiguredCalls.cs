using Nightware.Application.Calls;
using Nightware.Domain.Calls;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calls;

/// <summary>Calls without <c>Calls:SealingKey</c> or <c>Talk:PublicBaseUrl</c>: <c>/communications/calls</c> answers 501.</summary>
internal sealed class UnconfiguredCalls : ICallService
{
    public static readonly UnconfiguredCalls Instance = new();

    private static Error Off => Error.Unsupported("Calls are not configured on this server (Calls:SealingKey, Talk:PublicBaseUrl).");

    public Task<Result<Call>> PlaceAsync(NewCall call, CancellationToken cancellationToken) => Task.FromResult<Result<Call>>(Off);

    public Task<Result<Call>> GetAsync(string callId, CancellationToken cancellationToken) => Task.FromResult<Result<Call>>(Off);

    public Task<Result> HangUpAsync(string callId, CancellationToken cancellationToken) => Task.FromResult<Result>(Off);
}
