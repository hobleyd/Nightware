using Nightware.Domain.Calls;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calls;

/// <summary>
/// Delegated calls (ADR 0009 D7): placed as the caller in a room with the targets, ringing each of them. The call id
/// carries everything the backend needs, so nothing is stored; an id that isn't the caller's own call is
/// <see cref="ErrorKind.NotFound"/>. Without the backend configured every call is <see cref="ErrorKind.Unsupported"/>.
/// </summary>
public interface ICallService
{
    Task<Result<Call>> PlaceAsync(NewCall call, CancellationToken cancellationToken);

    Task<Result<Call>> GetAsync(string callId, CancellationToken cancellationToken);

    /// <summary>Leaves the call, ending it for everyone when the caller moderates its room. An ended call is a success.</summary>
    Task<Result> HangUpAsync(string callId, CancellationToken cancellationToken);
}
