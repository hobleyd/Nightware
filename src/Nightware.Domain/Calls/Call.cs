namespace Nightware.Domain.Calls;

/// <summary>A call's state, as Graph's <c>callState</c> names the ones a delegated call goes through.</summary>
public enum CallState
{
    /// <summary>The call is ringing: only the caller's own (façade) session is in it.</summary>
    Establishing,

    /// <summary>Someone else has joined.</summary>
    Established,

    /// <summary>The call has ended.</summary>
    Terminated,
}

/// <summary>A person a call rings, or the caller. <see cref="UserId"/> is the directory id; null for someone the directory doesn't know.</summary>
public sealed record CallParty(string? UserId, string? DisplayName);

/// <summary>
/// A call to place (ADR 0009 D7). <paramref name="Targets"/> name directory users (id, UPN or address).
/// <paramref name="CallbackUri"/> is kept and reported, never called.
/// </summary>
public sealed record NewCall(IReadOnlyList<string> Targets, IReadOnlyList<string> RequestedModalities, string? CallbackUri)
{
    /// <summary>The name of a new group room (several targets); the targets' names when null.</summary>
    public string? Subject { get; init; }
}

/// <summary>
/// A delegated call over Talk. <see cref="Id"/> is the backend's opaque (sealed) id, <see cref="ChatThreadId"/> the
/// room's token and <see cref="JoinWebUrl"/> its join link, which the caller's own client opens for media.
/// </summary>
public sealed record Call(string Id, CallState State, string ChatThreadId, string JoinWebUrl)
{
    public CallParty? Source { get; init; }

    public IReadOnlyList<CallParty> Targets { get; init; } = [];

    public IReadOnlyList<string> RequestedModalities { get; init; } = [];

    public string? CallbackUri { get; init; }
}
