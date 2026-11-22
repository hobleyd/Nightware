using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Core;
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
    internal const string RoomsPath = "apps/spreed/api/v4/room";
    private const int PublicRoom = 3;
    private const int MaxRoomName = 255;

    public bool IsEnabled => true;

    public async Task<Result<string>> OpenAsync(OnlineMeetingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["roomType"] = PublicRoom.ToString(CultureInfo.InvariantCulture),
            ["roomName"] = RoomName(request.Subject),
        };
        if (request.End is { } end)
        {
            form["objectType"] = "event";
            form["objectId"] = string.Create(CultureInfo.InvariantCulture, $"{request.Start.ToUnixTimeSeconds()}#{Math.Max(end.ToUnixTimeSeconds(), request.Start.ToUnixTimeSeconds())}");
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
            || !TokenPattern().IsMatch(token))
        {
            LogFailed(logger, "open", ErrorKind.BackendUnavailable, "Talk returned no room token.");
            return Error.BackendUnavailable("Nextcloud Talk returned no room token.");
        }

        return JoinUrl(token);
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
    internal string? TokenOf(string? joinUrl)
    {
        var prefix = CallPrefix();
        if (joinUrl is null || !joinUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = joinUrl[prefix.Length..].TrimEnd('/');
        return TokenPattern().IsMatch(token) ? token : null;
    }

    private string JoinUrl(string token) => CallPrefix() + token;

    private string CallPrefix()
    {
        var root = options.Value.PublicBaseUrl!.AbsoluteUri;
        return (root.EndsWith('/') ? root : root + "/") + "call/";
    }

    private string RoomName(string? subject)
    {
        var name = string.IsNullOrWhiteSpace(subject) ? options.Value.UntitledRoomName : subject.Trim();
        return name.Length <= MaxRoomName ? name : name[..MaxRoomName];
    }

    // Talk's own route requirement for a token.
    [GeneratedRegex("^[a-z0-9]{4,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Talk could not {Action} a meeting room: {Kind} {Reason}")]
    private static partial void LogFailed(ILogger logger, string action, ErrorKind kind, string reason);
}
