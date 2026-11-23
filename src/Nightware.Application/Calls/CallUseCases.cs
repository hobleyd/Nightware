using Nightware.Domain.Calls;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calls;

/// <summary>Graph <c>POST /communications/calls</c>, delegated: rings the targets from the caller.</summary>
public sealed class PlaceCall(ICallService calls)
{
    /// <summary>Graph's modalities; only audio and video mean anything to Talk, the rest are accepted and kept.</summary>
    private static readonly HashSet<string> Modalities = new(["audio", "video", "videoBasedScreenSharing", "data"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A callback URI is kept in the call id; this bounds the id's size.</summary>
    public const int MaxCallbackUriLength = 1024;

    public async Task<Result<Call>> ExecuteAsync(NewCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Targets.Count == 0 || call.Targets.Any(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("A call needs at least one target, each with identity.user.id.", "targets");
        }

        if (call.Targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != call.Targets.Count)
        {
            return Error.Validation("A target is named twice.", "targets");
        }

        if (call.RequestedModalities.Any(m => !Modalities.Contains(m)))
        {
            return Error.Validation("requestedModalities takes audio, video, videoBasedScreenSharing and data.", "requestedModalities");
        }

        if (call.CallbackUri is { } callback
            && (callback.Length > MaxCallbackUriLength || !Uri.TryCreate(callback, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            return Error.Validation($"callbackUri must be an absolute https URL of at most {MaxCallbackUriLength} characters.", "callbackUri");
        }

        return await calls.PlaceAsync(call, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>GET /communications/calls/{id}</c>.</summary>
public sealed class GetCall(ICallService calls)
{
    public Task<Result<Call>> ExecuteAsync(string callId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        return calls.GetAsync(callId, cancellationToken);
    }
}

/// <summary>Graph <c>DELETE /communications/calls/{id}</c>.</summary>
public sealed class HangUpCall(ICallService calls)
{
    public Task<Result> ExecuteAsync(string callId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);
        return calls.HangUpAsync(callId, cancellationToken);
    }
}
