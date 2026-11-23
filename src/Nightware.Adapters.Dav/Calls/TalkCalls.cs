using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Talk;
using Nightware.Application.Calls;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Calls;
using Nightware.Domain.Kernel;

using static Nightware.Adapters.Dav.Talk.TalkJson;

namespace Nightware.Adapters.Dav.Calls;

/// <summary>
/// <see cref="ICallService"/> on Talk (ADR 0009 D7), as the caller:
/// <list type="bullet">
/// <item>The call happens in the caller's one-to-one room with a single target (Talk returns the existing one), or a new
/// group room with several. The façade joins it (<c>participants/active</c>, which sets Talk's session cookie), starts the
/// call silently and rings each target, so only they get Talk's call notification.</item>
/// <item>The façade's HTTP client keeps no cookies, so the session's two cookies (the instance's session cookie and
/// <c>oc_sessionPassphrase</c>; Nextcloud's long-lived <c>nc_token</c> login cookies are never kept) travel sealed in the
/// call id (<see cref="CallIdSealer"/>) with the token, a hash of the Talk session id and the call's start time.</item>
/// <item>The façade's session has no signaling connection: Talk drops it from the call's peers after about 30 seconds,
/// and reaps it (ending a call nobody joined; the targets get "missed call") once anything lists the room's participants
/// after 100 seconds without a ping. Reading a call therefore never lists participants.</item>
/// <item><c>GET</c> is <c>terminated</c> once the room has no call or a different one, <c>established</c> while anyone
/// other than the caller is in it, and <c>establishing</c> otherwise.</item>
/// </list>
/// </summary>
internal sealed partial class TalkCalls(
    IDavClient dav, TalkPeople people, IUserContext caller, CallIdSealer sealer, IOptions<TalkOptions> talk, ILogger<TalkCalls> logger) : ICallService
{
    private const string PassphraseCookie = "oc_sessionPassphrase";
    private const string SessionIdCookie = "nc_session_id";

    // Talk's in-call flags: 1 in call (no audio or video, since the façade sends no media).
    private const string InCall = "1";

    public async Task<Result<Call>> PlaceAsync(NewCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var uids = new List<string>();
        foreach (var target in call.Targets)
        {
            var uid = await people.UidOfAsync(target, cancellationToken).ConfigureAwait(false);
            if (!uid.IsSuccess)
            {
                return uid.Error.Kind == ErrorKind.NotFound ? Error.Validation($"The target '{target}' is not a user in the directory.", "targets") : uid.Error;
            }

            uids.Add(uid.Value);
        }

        var room = await RoomForAsync(call, uids, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error;
        }

        var (token, created) = room.Value;
        var placed = await StartAsync(token, uids, call, cancellationToken).ConfigureAwait(false);
        if (!placed.IsSuccess && created)
        {
            await BestEffortAsync("delete the call's room", dav.OcsSendAsync(HttpMethod.Delete, $"{TalkRooms.RoomsPath}/{token}", null, cancellationToken)).ConfigureAwait(false);
        }

        return placed;
    }

    public async Task<Result<Call>> GetAsync(string callId, CancellationToken cancellationToken)
    {
        if (Open(callId) is not { } ticket)
        {
            return NoSuchCall();
        }

        var state = await StateAsync(ticket, cancellationToken).ConfigureAwait(false);
        return state.IsSuccess ? await DescribeAsync(callId, ticket, state.Value, cancellationToken).ConfigureAwait(false) : state.Error;
    }

    public async Task<Result> HangUpAsync(string callId, CancellationToken cancellationToken)
    {
        if (Open(callId) is not { } ticket)
        {
            return NoSuchCall();
        }

        // all=true ends the call for everyone when the caller moderates (Talk ignores it otherwise); a single leaver
        // ends it too when nobody else is in it.
        var left = await LeaveCallAsync(ticket.Token, ticket.Cookie, cancellationToken).ConfigureAwait(false);
        if (!left.IsSuccess && left.Error.Kind != ErrorKind.NotFound)
        {
            return left.Error;
        }

        if (!left.IsSuccess && await StateAsync(ticket, cancellationToken).ConfigureAwait(false) is { IsSuccess: true, Value: not CallState.Terminated })
        {
            // Talk has already reaped the façade's session (a Talk client listing the room's participants does that), but
            // the call goes on, ringing or answered: end it from a fresh session.
            var fresh = await JoinAsync(ticket.Token, cancellationToken).ConfigureAwait(false);
            if (fresh.IsSuccess)
            {
                var ended = await LeaveCallAsync(ticket.Token, fresh.Value.Cookie, cancellationToken).ConfigureAwait(false);
                await LeaveRoomAsync(ticket.Token, fresh.Value.Cookie, cancellationToken).ConfigureAwait(false);
                if (!ended.IsSuccess && ended.Error.Kind != ErrorKind.NotFound)
                {
                    return ended.Error;
                }
            }
            else if (fresh.Error.Kind != ErrorKind.NotFound)
            {
                return fresh.Error;
            }
        }

        await LeaveRoomAsync(ticket.Token, ticket.Cookie, cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>The room to call in, and whether it was made for this call.</summary>
    private async Task<Result<(string Token, bool Created)>> RoomForAsync(NewCall call, List<string> uids, CancellationToken cancellationToken)
    {
        OcsRequest request;
        if (uids.Count == 1)
        {
            request = new OcsRequest(HttpMethod.Post, TalkRooms.RoomsPath)
            {
                Form = new Dictionary<string, string> { ["roomType"] = TalkRooms.OneToOneRoom.ToString(CultureInfo.InvariantCulture), ["invite"] = uids[0] },
            };
        }
        else
        {
            var names = new List<string>();
            foreach (var uid in uids)
            {
                var user = await people.UserOfAsync(uid, cancellationToken).ConfigureAwait(false);
                names.Add(user is { IsSuccess: true, Value: { } known } ? known.DisplayName : uid);
            }

            var subject = string.IsNullOrWhiteSpace(call.Subject) ? string.Join(", ", names) : call.Subject;
            request = new OcsRequest(HttpMethod.Post, TalkRooms.RoomsPath)
            {
                Json = new JsonObject
                {
                    ["roomType"] = TalkRooms.GroupRoom,
                    ["roomName"] = TalkRooms.RoomName(talk.Value, subject),
                    ["participants"] = new JsonObject { ["users"] = new JsonArray([.. uids.Select(u => JsonValue.Create(u))]) },
                },
            };
        }

        var room = await dav.OcsExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            // Talk refuses a one-to-one room with oneself as a bad request.
            return room.Error;
        }

        return Str(room.Value.Data, "token") is { } token && TalkRooms.IsToken(token)
            ? (token, uids.Count > 1 || room.Value.Status == 201)
            : Error.BackendUnavailable("Nextcloud Talk returned no room token.");
    }

    private async Task<Result<Call>> StartAsync(string token, List<string> uids, NewCall call, CancellationToken cancellationToken)
    {
        var session = await JoinAsync(token, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
        {
            return session.Error;
        }

        var started = await StartAndRingAsync(token, uids, session.Value, cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            await LeaveCallAsync(token, session.Value.Cookie, cancellationToken).ConfigureAwait(false);
            await LeaveRoomAsync(token, session.Value.Cookie, cancellationToken).ConfigureAwait(false);
            return started.Error;
        }

        var ticket = new CallTicket(token, session.Value.Cookie, SessionHash(session.Value.SessionId), started.Value, uids, [.. call.RequestedModalities], call.CallbackUri);
        var id = sealer.Seal(caller.Principal.Id, JsonSerializer.SerializeToUtf8Bytes(ticket, CallJsonContext.Default.CallTicket));
        return await DescribeAsync(id, ticket, CallState.Establishing, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts the call (silently) and rings each target; returns the call's start time.</summary>
    private async Task<Result<long>> StartAndRingAsync(string token, List<string> uids, TalkSession session, CancellationToken cancellationToken)
    {
        var joined = await dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Post, $"{TalkRooms.CallPath}/{token}")
        {
            Form = new Dictionary<string, string> { ["flags"] = InCall, ["silent"] = "true" },
            Headers = CookieHeader(session.Cookie),
        }, cancellationToken).ConfigureAwait(false);
        if (!joined.IsSuccess)
        {
            return joined.Error;
        }

        // The session is fresh, so listing the participants reaps nothing of ours.
        var participants = await dav.OcsGetAsync($"{TalkRooms.RoomsPath}/{token}/participants", null, cancellationToken).ConfigureAwait(false);
        if (!participants.IsSuccess)
        {
            return participants.Error;
        }

        foreach (var uid in uids)
        {
            var attendee = Items(participants.Value).FirstOrDefault(p => Str(p, "actorType") == "users" && Str(p, "actorId") == uid);
            if (Long(attendee, "attendeeId") is not { } attendeeId)
            {
                return Error.BackendUnavailable($"Nextcloud Talk did not list '{uid}' in the call's room.");
            }

            var rung = await dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Post, string.Create(CultureInfo.InvariantCulture, $"{TalkRooms.CallPath}/{token}/ring/{attendeeId}"))
            {
                Form = new Dictionary<string, string>(),
                Headers = CookieHeader(session.Cookie),
            }, cancellationToken).ConfigureAwait(false);
            if (!rung.IsSuccess)
            {
                return rung.Error;
            }
        }

        var room = await dav.OcsGetAsync($"{TalkRooms.RoomsPath}/{token}", null, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error;
        }

        return Long(room.Value, "callStartTime") is { } start and > 0 ? start : Error.BackendUnavailable("Nextcloud Talk did not start the call.");
    }

    private async Task<Result<CallState>> StateAsync(CallTicket ticket, CancellationToken cancellationToken)
    {
        var room = await dav.OcsGetAsync($"{TalkRooms.RoomsPath}/{ticket.Token}", null, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            // The room is gone (or the caller left it): so is the call.
            return room.Error.Kind is ErrorKind.NotFound or ErrorKind.Forbidden ? CallState.Terminated : room.Error;
        }

        if (!Bool(room.Value, "hasCall") || Long(room.Value, "callStartTime") != ticket.Started)
        {
            return CallState.Terminated;
        }

        var peers = await dav.OcsGetAsync($"{TalkRooms.CallPath}/{ticket.Token}", null, cancellationToken).ConfigureAwait(false);
        if (!peers.IsSuccess)
        {
            return peers.Error.Kind is ErrorKind.NotFound or ErrorKind.Forbidden ? CallState.Terminated : peers.Error;
        }

        // Someone else has joined: a peer that is neither the façade's session nor the caller (whose own Talk client
        // joining for media is not the call being answered).
        var callerType = Str(room.Value, "actorType");
        var callerId = Str(room.Value, "actorId");
        return Items(peers.Value).Any(p => Str(p, "sessionId") is { } session && SessionHash(session) != ticket.SessionHash
                && !(Str(p, "actorType") == callerType && Str(p, "actorId") == callerId))
            ? CallState.Established
            : CallState.Establishing;
    }

    private async Task<Result<Call>> DescribeAsync(string id, CallTicket ticket, CallState state, CancellationToken cancellationToken)
    {
        var targets = new List<CallParty>();
        foreach (var uid in ticket.Targets)
        {
            var user = await people.UserOfAsync(uid, cancellationToken).ConfigureAwait(false);
            if (!user.IsSuccess)
            {
                return user.Error;
            }

            targets.Add(user.Value is { } known ? new CallParty(known.Id, known.DisplayName) : new CallParty(null, uid));
        }

        return new Call(id, state, ticket.Token, TalkRooms.JoinUrl(talk.Value, ticket.Token))
        {
            Source = new CallParty(caller.Principal.Id, caller.Principal.DisplayName),
            Targets = targets,
            RequestedModalities = ticket.Modalities,
            CallbackUri = ticket.CallbackUri,
        };
    }

    /// <summary>Joins the room as the caller: a new Talk session, and the cookies that carry it.</summary>
    private async Task<Result<TalkSession>> JoinAsync(string token, CancellationToken cancellationToken)
    {
        var joined = await dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Post, $"{TalkRooms.RoomsPath}/{token}/participants/active")
        {
            Form = new Dictionary<string, string> { ["force"] = "true" },
        }, cancellationToken).ConfigureAwait(false);
        if (!joined.IsSuccess)
        {
            return joined.Error;
        }

        return Str(joined.Value.Data, "sessionId") is { Length: > 0 } sessionId && SessionCookie(joined.Value.SetCookies) is { } cookie
            ? new TalkSession(sessionId, cookie)
            : Error.BackendUnavailable("Nextcloud Talk opened no session for the call.");
    }

    private Task<Result<OcsResponse>> LeaveCallAsync(string token, string cookie, CancellationToken cancellationToken) =>
        dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Delete, $"{TalkRooms.CallPath}/{token}")
        {
            Query = new Dictionary<string, string> { ["all"] = "true" },
            Headers = CookieHeader(cookie),
        }, cancellationToken);

    private Task LeaveRoomAsync(string token, string cookie, CancellationToken cancellationToken) =>
        BestEffortAsync("leave the call's room", dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Delete, $"{TalkRooms.RoomsPath}/{token}/participants/active")
        {
            Headers = CookieHeader(cookie),
        }, cancellationToken));

    /// <summary>
    /// The PHP session's cookie pair, as a <c>Cookie</c> header: the instance's session cookie (named per instance, so found
    /// as the cookie whose value is <c>nc_session_id</c>'s) and its passphrase. Neither works without the other.
    /// </summary>
    internal static string? SessionCookie(IReadOnlyList<string> setCookies)
    {
        var cookies = setCookies
            .Select(c => c.Split(';', 2)[0].Split('=', 2))
            .Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0 && p[1] != "deleted")
            .Select(p => (Name: p[0].Trim(), Value: p[1].Trim()))
            .ToList();
        var passphrase = cookies.LastOrDefault(c => c.Name == PassphraseCookie);
        var sessionId = cookies.LastOrDefault(c => c.Name == SessionIdCookie).Value;
        var session = cookies.LastOrDefault(c => c.Name is not (PassphraseCookie or SessionIdCookie) && !c.Name.StartsWith("nc_", StringComparison.Ordinal)
            && (sessionId is null || c.Value == sessionId));
        return passphrase.Name is null || session.Name is null ? null : $"{session.Name}={session.Value}; {passphrase.Name}={passphrase.Value}";
    }

    internal static string SessionHash(string sessionId) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)).AsSpan(0, 16));

    private CallTicket? Open(string callId)
    {
        if (sealer.Open(caller.Principal.Id, callId) is not { } payload)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(payload, CallJsonContext.Default.CallTicket) is { V: CallTicket.Version } ticket && TalkRooms.IsToken(ticket.Token) ? ticket : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task BestEffortAsync<T>(string action, Task<Result<T>> attempt)
    {
        var result = await attempt.ConfigureAwait(false);
        if (!result.IsSuccess && result.Error.Kind != ErrorKind.NotFound)
        {
            LogBestEffort(logger, action, result.Error.Kind, result.Error.Message);
        }
    }

    private static Dictionary<string, string> CookieHeader(string cookie) => new() { ["Cookie"] = cookie };

    private static Error NoSuchCall() => Error.NotFound("The call does not exist, or it is not yours.");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Talk could not {Action}: {Kind} {Reason}")]
    private static partial void LogBestEffort(ILogger logger, string action, ErrorKind kind, string reason);

    private sealed record TalkSession(string SessionId, string Cookie);
}

/// <summary>What a call id seals: the room, the façade's Talk session, the call's start and what Graph reports back.</summary>
internal sealed record CallTicket(
    [property: JsonPropertyName("t")] string Token,
    [property: JsonPropertyName("c")] string Cookie,
    [property: JsonPropertyName("s")] string SessionHash,
    [property: JsonPropertyName("a")] long Started,
    [property: JsonPropertyName("u")] IReadOnlyList<string> Targets,
    [property: JsonPropertyName("m")] IReadOnlyList<string> Modalities,
    [property: JsonPropertyName("b")] string? CallbackUri)
{
    public const int Version = 1;

    [JsonPropertyName("v")]
    public int V { get; init; } = Version;
}

[JsonSerializable(typeof(CallTicket))]
internal sealed partial class CallJsonContext : JsonSerializerContext;
