using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Talk;
using static Nightware.Adapters.Dav.Talk.TalkJson;
using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Adapters.Dav.OnlineMeetings;

/// <summary>
/// <see cref="IOnlineMeetingStore"/> on Talk's OCS API, as the caller (ADR 0009 D7):
/// <list type="bullet">
/// <item>A meeting is a public room (type 3), the kind phase 22 opens for calendar events, so a calendar event's room is a
/// meeting too. Its id is <c>t.</c> plus the token; any other room is NotFound.</item>
/// <item>A meeting with a span is bound to it (object <c>event</c>), so Talk expires the room once the meeting is long
/// over. Talk can't re-time a bound room and refuses to rename one: a changed span or subject unbinds it, and from then
/// on the meeting has no known span (and stays until deleted).</item>
/// <item>Attendees who are directory users become participants, so the meeting is in their chats. Others join by the link.</item>
/// <item>The room has no creation time; its first chat message (<c>conversation_created</c>) gives it, read without
/// moving the caller's read marker or clearing notifications.</item>
/// </list>
/// </summary>
internal sealed class NextcloudOnlineMeetings(IDavClient dav, TalkPeople people, IOptions<TalkOptions> options) : IOnlineMeetingStore
{
    public const string IdPrefix = "t.";

    // Talk's participantType: 1 owner, 2 moderator, 3 user, 4 guest, 5 user following a public link, 6 guest moderator.
    private const int Owner = 1;
    private const int Moderator = 2;
    private const int GuestModerator = 6;

    public async Task<Result<OnlineMeeting>> CreateAsync(NewOnlineMeeting meeting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        var users = new JsonArray();
        foreach (var attendee in meeting.Attendees)
        {
            var uid = await people.UidOfAsync(attendee, cancellationToken).ConfigureAwait(false);
            if (uid.IsSuccess)
            {
                users.Add(uid.Value);
            }
            else if (uid.Error.Kind != ErrorKind.NotFound)
            {
                return uid.Error;
            }
        }

        var body = new JsonObject
        {
            ["roomType"] = TalkRooms.PublicRoom,
            ["roomName"] = TalkRooms.RoomName(options.Value, meeting.Subject),
        };
        if (meeting is { Start: { } start, End: { } end })
        {
            body["objectType"] = TalkRooms.EventObject;
            body["objectId"] = TalkRooms.SpanObjectId(start, end);
        }

        if (users.Count > 0)
        {
            body["participants"] = new JsonObject { ["users"] = users };
        }

        var created = await dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Post, TalkRooms.RoomsPath) { Json = body }, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        return Str(created.Value.Data, "token") is { } token && TalkRooms.IsToken(token)
            ? await ReadAsync(token, cancellationToken).ConfigureAwait(false)
            : Error.BackendUnavailable("Nextcloud Talk returned no room token.");
    }

    public Task<Result<OnlineMeeting>> GetAsync(string meetingId, CancellationToken cancellationToken) =>
        TokenOfId(meetingId) is { } token ? ReadAsync(token, cancellationToken) : Task.FromResult<Result<OnlineMeeting>>(NoSuchMeeting());

    public Task<Result<OnlineMeeting>> FindByJoinUrlAsync(string joinWebUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(joinWebUrl);

        // Graph's own example sends the link percent-encoded inside the literal.
        var url = joinWebUrl.Trim();
        var token = TalkRooms.TokenOf(options.Value, url) ?? TalkRooms.TokenOf(options.Value, Uri.UnescapeDataString(url));
        return token is null ? Task.FromResult<Result<OnlineMeeting>>(NoSuchMeeting()) : ReadAsync(token, cancellationToken);
    }

    public async Task<Result<OnlineMeeting>> UpdateAsync(string meetingId, OnlineMeetingChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (TokenOfId(meetingId) is not { } token)
        {
            return NoSuchMeeting();
        }

        var room = await RoomAsync(token, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error;
        }

        var name = TalkRooms.RoomName(options.Value, change.Subject);
        var rename = change.ChangesSubject && name != Str(room.Value, "name");
        var span = TalkRooms.SpanOf(Str(room.Value, "objectType"), Str(room.Value, "objectId"));
        var retime = change is { Start: { } start, End: { } end } && span is { } bound && (bound.Start != Seconds(start) || bound.End != Seconds(end));

        // Talk can't re-time a bound room, and refuses to rename one (400 "event"): either change unbinds it first.
        if (span is not null && (rename || retime))
        {
            var unbound = await dav.OcsSendAsync(HttpMethod.Delete, $"{TalkRooms.RoomsPath}/{token}/object", null, cancellationToken).ConfigureAwait(false);
            if (!unbound.IsSuccess && unbound.Error.Kind != ErrorKind.Validation)
            {
                return unbound.Error;
            }
        }

        if (rename)
        {
            var renamed = await dav.OcsSendAsync(HttpMethod.Put, $"{TalkRooms.RoomsPath}/{token}", new Dictionary<string, string> { ["roomName"] = name }, cancellationToken)
                .ConfigureAwait(false);
            if (!renamed.IsSuccess)
            {
                return renamed.Error;
            }
        }

        return await ReadAsync(token, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> DeleteAsync(string meetingId, CancellationToken cancellationToken)
    {
        if (TokenOfId(meetingId) is not { } token)
        {
            return NoSuchMeeting();
        }

        var room = await RoomAsync(token, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error;
        }

        var deleted = await dav.OcsSendAsync(HttpMethod.Delete, $"{TalkRooms.RoomsPath}/{token}", null, cancellationToken).ConfigureAwait(false);
        return deleted.IsSuccess ? Result.Success() : deleted.Error;
    }

    /// <summary>The room token a meeting id names; null for an id this adapter didn't issue.</summary>
    internal static string? TokenOfId(string? meetingId) =>
        meetingId is not null && meetingId.StartsWith(IdPrefix, StringComparison.Ordinal) && TalkRooms.IsToken(meetingId[IdPrefix.Length..])
            ? meetingId[IdPrefix.Length..]
            : null;

    /// <summary>The meeting's room as the caller sees it: a public room the caller takes part in, otherwise NotFound.</summary>
    internal async Task<Result<JsonElement>> RoomAsync(string token, CancellationToken cancellationToken)
    {
        var room = await dav.OcsGetAsync($"{TalkRooms.RoomsPath}/{token}", null, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error.Kind is ErrorKind.NotFound or ErrorKind.Forbidden ? NoSuchMeeting() : room.Error;
        }

        // A public room shows itself to anyone signed in (participantType 0 until they join); only participants see the meeting.
        return Int(room.Value, "type") == TalkRooms.PublicRoom && Int(room.Value, "participantType") is >= Owner and <= 3
            ? room.Value
            : NoSuchMeeting();
    }

    private async Task<Result<OnlineMeeting>> ReadAsync(string token, CancellationToken cancellationToken)
    {
        var room = await RoomAsync(token, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error;
        }

        var participants = await dav.OcsGetAsync($"{TalkRooms.RoomsPath}/{token}/participants", null, cancellationToken).ConfigureAwait(false);
        if (!participants.IsSuccess)
        {
            return participants.Error;
        }

        MeetingParticipant? organizer = null;
        var attendees = new List<MeetingParticipant>();
        foreach (var participant in TalkJson.Items(participants.Value))
        {
            var person = await PersonAsync(participant, cancellationToken).ConfigureAwait(false);
            if (!person.IsSuccess)
            {
                return person.Error;
            }

            if (person.Value is null)
            {
                continue;
            }

            if (Int(participant, "participantType") == Owner && organizer is null)
            {
                organizer = person.Value with { Role = MeetingRole.Presenter };
            }
            else
            {
                attendees.Add(person.Value);
            }
        }

        var span = TalkRooms.SpanOf(Str(room.Value, "objectType"), Str(room.Value, "objectId"));
        return new OnlineMeeting(IdPrefix + token, TalkRooms.JoinUrl(options.Value, token), token)
        {
            Subject = Str(room.Value, "name"),
            Start = span?.Start,
            End = span?.End,
            Created = await CreatedAsync(token, cancellationToken).ConfigureAwait(false),
            JoinMeetingId = token,
            IsPasscodeRequired = TalkJson.Bool(room.Value, "hasPassword"),
            Organizer = organizer,
            Attendees = attendees,
        };
    }

    /// <summary>A participant as Graph names them: users through the directory, guests by name; groups, teams and the like are left out.</summary>
    private async Task<Result<MeetingParticipant?>> PersonAsync(JsonElement participant, CancellationToken cancellationToken)
    {
        var role = Int(participant, "participantType") is Moderator or GuestModerator ? MeetingRole.CoOrganizer : MeetingRole.Attendee;
        var name = Str(participant, "displayName");
        switch (Str(participant, "actorType"))
        {
            case "users" when Str(participant, "actorId") is { Length: > 0 } uid:
                var user = await people.UserOfAsync(uid, cancellationToken).ConfigureAwait(false);
                if (!user.IsSuccess)
                {
                    return user.Error;
                }

                return user.Value is { } known
                    ? new MeetingParticipant(known.Id, known.DisplayName, known.UserPrincipalName, role)
                    : new MeetingParticipant(null, name, null, role);
            case "guests" or "emails" or "federated_users":
                return new MeetingParticipant(null, string.IsNullOrEmpty(name) ? null : name, null, role);
            default:
                return Result<MeetingParticipant?>.Success(null);
        }
    }

    /// <summary>When the room was made: the time of its oldest chat message; null when the history no longer goes back that far.</summary>
    private async Task<DateTimeOffset?> CreatedAsync(string token, CancellationToken cancellationToken)
    {
        var first = await dav.OcsGetAsync($"{TalkRooms.ChatPath}/{token}", new Dictionary<string, string>
        {
            ["lookIntoFuture"] = "1",
            ["lastKnownMessageId"] = "0",
            ["limit"] = "1",
            ["timeout"] = "0",
            ["setReadMarker"] = "0",
            ["markNotificationsAsRead"] = "0",
        }, cancellationToken).ConfigureAwait(false);
        if (!first.IsSuccess || first.Value.ValueKind != JsonValueKind.Array || first.Value.GetArrayLength() == 0)
        {
            return null;
        }

        var message = first.Value[0];
        return Str(message, "systemMessage") == "conversation_created" && TalkJson.Long(message, "timestamp") is { } seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    private static DateTimeOffset Seconds(DateTimeOffset at) => DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds());

    private static Error NoSuchMeeting() => Error.NotFound("The online meeting does not exist, or you are not taking part in it.");
}
