using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Talk;
using Nightware.Application.Calendar.OnlineMeetings;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar.Talk;

/// <summary>
/// <see cref="IOnlineMeetings"/> on Nextcloud Talk's OCS API (<c>apps/spreed/api/v4/room</c>), as the caller:
/// <list type="bullet">
/// <item>A room is public (type 3), so attendees outside the server join by its link as guests; Nextcloud users
/// join as themselves. It is named after the event.</item>
/// <item>A single meeting's room is bound to the event's span (object type <c>event</c>, id
/// <c>start#end</c> in Unix seconds), so Talk expires it itself once the meeting is long over
/// (<c>retention_event_rooms</c>, 28 days by default). A series' room is unbound and stays until closed.</item>
/// <item>Talk cannot re-time a bound room; a meeting moved later is unbound instead (it stays until closed).</item>
/// </list>
/// A failure is logged and returned; the event use cases carry on without the room.
/// </summary>
internal sealed partial class NextcloudTalkMeetings(IDavClient dav, IOptions<TalkOptions> options, ILogger<NextcloudTalkMeetings> logger) : IOnlineMeetings
{
    internal const string RoomsPath = TalkRooms.RoomsPath;

    public bool IsEnabled => true;

    public async Task<Result<string>> OpenAsync(OnlineMeetingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["roomType"] = TalkRooms.PublicRoom.ToString(CultureInfo.InvariantCulture),
            ["roomName"] = TalkRooms.RoomName(options.Value, request.Subject),
        };
        if (request.End is { } end)
        {
            form["objectType"] = TalkRooms.EventObject;
            form["objectId"] = TalkRooms.SpanObjectId(request.Start, end);
        }

        var created = await dav.OcsSendAsync(HttpMethod.Post, RoomsPath, form, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            LogFailed(logger, "open", created.Error.Kind, created.Error.Message);
            return created.Error;
        }

        if (created.Value.ValueKind != JsonValueKind.Object
            || !created.Value.TryGetProperty("token", out var tokenProperty)
            || tokenProperty.GetString() is not { } token
            || !TalkRooms.IsToken(token))
        {
            LogFailed(logger, "open", ErrorKind.BackendUnavailable, "Talk returned no room token.");
            return Error.BackendUnavailable("Nextcloud Talk returned no room token.");
        }

        return TalkRooms.JoinUrl(options.Value, token);
    }

    public async Task<Result> KeepAsync(string joinUrl, CancellationToken cancellationToken)
    {
        if (TokenOf(joinUrl) is not { } token)
        {
            return Result.Success();
        }

        var unbound = await dav.OcsSendAsync(HttpMethod.Delete, $"{RoomsPath}/{token}/object", null, cancellationToken).ConfigureAwait(false);

        // 400: the room is bound to nothing (already kept, or a series' room); 404: it is gone.
        if (unbound.IsSuccess || unbound.Error.Kind is ErrorKind.Validation or ErrorKind.NotFound)
        {
            return Result.Success();
        }

        LogFailed(logger, "keep", unbound.Error.Kind, unbound.Error.Message);
        return unbound.Error;
    }

    public async Task<Result> CloseAsync(string joinUrl, CancellationToken cancellationToken)
    {
        if (TokenOf(joinUrl) is not { } token)
        {
            return Result.Success();
        }

        var deleted = await dav.OcsSendAsync(HttpMethod.Delete, $"{RoomsPath}/{token}", null, cancellationToken).ConfigureAwait(false);
        if (deleted.IsSuccess || deleted.Error.Kind == ErrorKind.NotFound)
        {
            return Result.Success();
        }

        LogFailed(logger, "close", deleted.Error.Kind, deleted.Error.Message);
        return deleted.Error;
    }

    /// <summary>The room token of a join URL this server issued (<c>{PublicBaseUrl}call/{token}</c>); null for any other URL.</summary>
    internal string? TokenOf(string? joinUrl) => TalkRooms.TokenOf(options.Value, joinUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Talk could not {Action} a meeting room: {Kind} {Reason}")]
    private static partial void LogFailed(ILogger logger, string action, ErrorKind kind, string reason);
}
