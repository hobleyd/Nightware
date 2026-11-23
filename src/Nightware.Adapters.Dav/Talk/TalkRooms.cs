using System.Globalization;
using System.Text.RegularExpressions;

namespace Nightware.Adapters.Dav.Talk;

/// <summary>
/// What every Talk room user in the adapter shares (calendar events' rooms, phase 22; online meetings and calls,
/// phase 26): the OCS routes, Talk's token rule, the join link <c>{Talk:PublicBaseUrl}call/{token}</c>, a room's
/// name and the <c>event</c> object that binds a room to a span.
/// </summary>
internal static partial class TalkRooms
{
    /// <summary>Talk's conversation API (<c>apps/spreed/api/v4/room</c>), below the OCS root.</summary>
    public const string RoomsPath = "apps/spreed/api/v4/room";

    /// <summary>Talk's call API (<c>apps/spreed/api/v4/call</c>).</summary>
    public const string CallPath = "apps/spreed/api/v4/call";

    /// <summary>Talk's chat API (<c>apps/spreed/api/v1/chat</c>).</summary>
    public const string ChatPath = "apps/spreed/api/v1/chat";

    public const int OneToOneRoom = 1;
    public const int GroupRoom = 2;
    public const int PublicRoom = 3;

    /// <summary>The object type that binds a room to a meeting's span, so Talk expires it once the meeting is long over.</summary>
    public const string EventObject = "event";

    private const int MaxRoomName = 255;

    /// <summary>True for a string Talk accepts as a room token (its own route requirement).</summary>
    public static bool IsToken(string? token) => token is not null && TokenPattern().IsMatch(token);

    /// <summary>A room's join link: <c>{PublicBaseUrl}call/{token}</c>.</summary>
    public static string JoinUrl(TalkOptions options, string token) => CallPrefix(options) + token;

    /// <summary>The room token of a join link this server issued; null for any other URL.</summary>
    public static string? TokenOf(TalkOptions options, string? joinUrl)
    {
        var prefix = CallPrefix(options);
        if (joinUrl is null || !joinUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = joinUrl[prefix.Length..].TrimEnd('/');
        return IsToken(token) ? token : null;
    }

    /// <summary>A room's name from a subject: trimmed, at most 255 characters, the untitled name when blank.</summary>
    public static string RoomName(TalkOptions options, string? subject)
    {
        ArgumentNullException.ThrowIfNull(options);
        var name = string.IsNullOrWhiteSpace(subject) ? options.UntitledRoomName : subject.Trim();
        return name.Length <= MaxRoomName ? name : name[..MaxRoomName];
    }

    /// <summary>The <c>event</c> object id for a span: <c>start#end</c> in Unix seconds (an end before the start is the start).</summary>
    public static string SpanObjectId(DateTimeOffset start, DateTimeOffset end) =>
        string.Create(CultureInfo.InvariantCulture, $"{start.ToUnixTimeSeconds()}#{Math.Max(end.ToUnixTimeSeconds(), start.ToUnixTimeSeconds())}");

    /// <summary>The span an <c>event</c> object id names; null for anything else.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End)? SpanOf(string? objectType, string? objectId)
    {
        if (objectType != EventObject || objectId is null)
        {
            return null;
        }

        var parts = objectId.Split('#');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
            && start <= end && end <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? (DateTimeOffset.FromUnixTimeSeconds(start), DateTimeOffset.FromUnixTimeSeconds(end))
            : null;
    }

    private static string CallPrefix(TalkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = (options.PublicBaseUrl ?? throw new InvalidOperationException("Talk:PublicBaseUrl is not set.")).AbsoluteUri;
        return (root.EndsWith('/') ? root : root + "/") + "call/";
    }

    // Talk's own route requirement for a token.
    [GeneratedRegex("^[a-z0-9]{4,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
