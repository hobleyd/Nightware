using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.OnlineMeetings;

/// <summary>
/// Where an event's online meeting happens (Graph <c>isOnlineMeeting</c>): a provider that opens a room per
/// event and hands back its join URL. Rooms are opened and closed as the caller.
/// </summary>
public interface IOnlineMeetings
{
    /// <summary>False when no provider is configured: events stay without a meeting room, and nothing is read to look for one.</summary>
    bool IsEnabled { get; }

    /// <summary>Opens a room for an event; returns its join URL.</summary>
    Task<Result<string>> OpenAsync(OnlineMeetingRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The meeting now runs past the end its room was opened for (or became a series): the room stays until it
    /// is closed. A join URL the provider did not issue is left alone.
    /// </summary>
    Task<Result> KeepAsync(string joinUrl, CancellationToken cancellationToken);

    /// <summary>Closes the room. A join URL the provider did not issue, or a room already gone, is left alone.</summary>
    Task<Result> CloseAsync(string joinUrl, CancellationToken cancellationToken);
}
