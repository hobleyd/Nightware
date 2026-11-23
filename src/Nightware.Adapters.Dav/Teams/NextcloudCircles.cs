using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;
using Nightware.Adapters.Dav.Talk;

namespace Nightware.Adapters.Dav.Teams;

/// <summary>A circle member's level (<c>Member::LEVEL_*</c>).</summary>
internal static class CircleLevel
{
    public const int Member = 1;
    public const int Moderator = 4;
    public const int Admin = 8;
    public const int Owner = 9;

    /// <summary>Graph's <c>owner</c> role: a circle admin or its owner (ADR 0009 D5).</summary>
    public static bool IsOwnerRole(int level) => level >= Admin;
}

/// <summary>A circle's config flags (<c>Circle::CFG_*</c>) that matter here.</summary>
internal static class CircleConfig
{
    public const int Single = 1;
    public const int Personal = 2;
    public const int System = 4;
    public const int Visible = 8;
    public const int Open = 16;
    public const int Hidden = 1024;
    public const int Backend = 2048;

    /// <summary>Visible and open to join: Graph's <c>public</c> (ADR 0009 D5).</summary>
    public const int Public = Visible | Open;

    /// <summary>Circles that aren't teams people made: a user's own single circle, personal, system and hidden ones.</summary>
    public const int NotATeam = Single | Personal | System | Hidden | Backend;

    public static TeamVisibility VisibilityOf(int config) => (config & Public) == Public ? TeamVisibility.Public : TeamVisibility.Private;

    public static int With(int config, TeamVisibility visibility) => visibility == TeamVisibility.Public ? config | Public : config & ~Public;
}

/// <summary>A circle member (<c>Member</c> in the circles app): a user (<c>userType</c> 1), a group, a mail address, ….</summary>
internal sealed record CircleMember(string Id, string UserId, int UserType, int Level, string? DisplayName)
{
    public const int UserTypeUser = 1;

    public bool IsUser => UserType == UserTypeUser;

    public static CircleMember Parse(JsonElement element) => new(
        TalkJson.String(element, "id") ?? string.Empty,
        TalkJson.String(element, "userId") ?? string.Empty,
        (int)(TalkJson.Long(element, "userType") ?? 0),
        (int)(TalkJson.Long(element, "level") ?? 0),
        TalkJson.String(element, "displayName"));
}

/// <summary>A circle as its member sees it; <see cref="Caller"/> is the caller's own membership (<c>initiator</c>).</summary>
internal sealed record Circle(string Id, string Name)
{
    public string Description { get; init; } = string.Empty;

    public int Config { get; init; }

    public DateTimeOffset? Creation { get; init; }

    public string? Url { get; init; }

    public CircleMember? Caller { get; init; }

    public bool IsTeam => (Config & CircleConfig.NotATeam) == 0;

    public static Circle Parse(JsonElement element) => new(TalkJson.String(element, "id") ?? string.Empty, TalkJson.String(element, "displayName") ?? TalkJson.String(element, "name") ?? string.Empty)
    {
        Description = TalkJson.String(element, "description") ?? string.Empty,
        Config = (int)(TalkJson.Long(element, "config") ?? 0),
        Creation = TalkJson.Long(element, "creation") is > 0 and var created ? TalkJson.Instant(created) : null,
        Url = TalkJson.String(element, "url"),
        Caller = element.TryGetProperty("initiator", out var initiator) && initiator.ValueKind == JsonValueKind.Object ? CircleMember.Parse(initiator) : null,
    };
}

/// <summary>
/// Nextcloud Teams (the circles app) over OCS as the caller (<c>apps/circles</c>), and the core team resources
/// (<c>teams/{id}/resources</c>). Every circle and member id is checked before it goes into a path. A circle the caller
/// isn't in is looked up in the caller's own list first: the app answers an unknown circle with a throttled 403.
/// </summary>
internal sealed class NextcloudCircles(IDavClient dav)
{
    internal const string CirclesPath = "apps/circles/circles";
    internal const string TeamsPath = "apps/circles/teams";
    internal const string ResourcesPath = "teams";

    /// <summary>The caller's teams: the circles they are a member of, without single, personal, system or hidden ones.</summary>
    public async Task<Result<IReadOnlyList<Circle>>> ListAsync(CancellationToken cancellationToken)
    {
        var listed = await dav.OcsGetAsync(CirclesPath, null, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        return listed.Value.ValueKind == JsonValueKind.Array
            ? listed.Value.EnumerateArray().Select(Circle.Parse).Where(c => Team.IsTeamId(c.Id) && c.IsTeam && c.Caller is { Level: > 0 }).ToList()
            : Unexpected("team list");
    }

    /// <summary>One of the caller's teams, or NotFound.</summary>
    public async Task<Result<Circle>> GetAsync(string circleId, CancellationToken cancellationToken)
    {
        if (!Team.IsTeamId(circleId))
        {
            return NoTeam();
        }

        var circles = await ListAsync(cancellationToken).ConfigureAwait(false);
        if (!circles.IsSuccess)
        {
            return circles.Error;
        }

        return circles.Value.FirstOrDefault(c => c.Id == circleId) is { } circle ? circle : NoTeam();
    }

    /// <summary>Creates a circle (with its team folder); the caller is its owner.</summary>
    public async Task<Result<Circle>> CreateAsync(string name, CancellationToken cancellationToken)
    {
        var created = await dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Post, CirclesPath)
        {
            Json = new JsonObject { ["name"] = name, ["createTeamFolder"] = true },
        }, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        return created.Value.Data.ValueKind == JsonValueKind.Object && Circle.Parse(created.Value.Data) is { } circle && Team.IsTeamId(circle.Id)
            ? circle
            : Unexpected("team");
    }

    public Task<Result> DestroyAsync(string circleId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, $"{CirclesPath}/{circleId}", null, cancellationToken);

    public Task<Result> RenameAsync(string circleId, string name, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, $"{CirclesPath}/{circleId}/name", new JsonObject { ["value"] = name }, cancellationToken);

    public Task<Result> DescribeAsync(string circleId, string description, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, $"{CirclesPath}/{circleId}/description", new JsonObject { ["value"] = description }, cancellationToken);

    public Task<Result> ConfigureAsync(string circleId, int config, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, $"{CirclesPath}/{circleId}/config", new JsonObject { ["value"] = config }, cancellationToken);

    public async Task<Result<IReadOnlyList<CircleMember>>> MembersAsync(string circleId, CancellationToken cancellationToken)
    {
        if (!Team.IsTeamId(circleId))
        {
            return NoTeam();
        }

        var listed = await dav.OcsGetAsync($"{CirclesPath}/{circleId}/members", null, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return NotFoundAs(listed.Error);
        }

        return listed.Value.ValueKind == JsonValueKind.Array
            ? listed.Value.EnumerateArray().Select(CircleMember.Parse).Where(m => IsMemberId(m.Id)).ToList()
            : Unexpected("member list");
    }

    /// <summary>Adds a Nextcloud user as an ordinary member.</summary>
    public async Task<Result<CircleMember>> AddUserAsync(string circleId, string accountName, CancellationToken cancellationToken)
    {
        if (!Team.IsTeamId(circleId))
        {
            return NoTeam();
        }

        var added = await dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Post, $"{CirclesPath}/{circleId}/members")
        {
            Json = new JsonObject { ["userId"] = accountName, ["type"] = CircleMember.UserTypeUser },
        }, cancellationToken).ConfigureAwait(false);
        if (!added.IsSuccess)
        {
            return NotFoundAs(added.Error);
        }

        return added.Value.Data.ValueKind == JsonValueKind.Object && CircleMember.Parse(added.Value.Data) is { } member && IsMemberId(member.Id)
            ? member
            : Unexpected("member");
    }

    /// <summary>The caller leaves the circle.</summary>
    public Task<Result> LeaveAsync(string circleId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, $"{CirclesPath}/{circleId}/leave", null, cancellationToken);

    public Task<Result> SetLevelAsync(string circleId, string memberId, int level, CancellationToken cancellationToken) =>
        IsMemberId(memberId)
            ? SendAsync(HttpMethod.Put, $"{CirclesPath}/{circleId}/members/{memberId}/level", new JsonObject { ["level"] = level }, cancellationToken)
            : Task.FromResult<Result>(NoMember());

    public Task<Result> RemoveMemberAsync(string circleId, string memberId, CancellationToken cancellationToken) =>
        IsMemberId(memberId)
            ? SendAsync(HttpMethod.Delete, $"{CirclesPath}/{circleId}/members/{memberId}", null, cancellationToken)
            : Task.FromResult<Result>(NoMember());

    /// <summary>
    /// Where the team's folder is mounted in each member's files (<c>teams/{id}/folder</c>'s <c>mountPoint</c>). It keeps
    /// the name the team had when it was made: renaming a team doesn't move it. Null when the team has no folder.
    /// </summary>
    public async Task<Result<string?>> FolderMountPointAsync(string circleId, CancellationToken cancellationToken)
    {
        if (!Team.IsTeamId(circleId))
        {
            return NoTeam();
        }

        var folder = await dav.OcsGetAsync($"{TeamsPath}/{circleId}/folder", null, cancellationToken).ConfigureAwait(false);
        if (!folder.IsSuccess)
        {
            return folder.Error.Kind == ErrorKind.NotFound ? Result<string?>.Success(null) : folder.Error;
        }

        return TalkJson.String(folder.Value, "mountPoint") is { Length: > 0 } mountPoint ? mountPoint : Result<string?>.Success(null);
    }

    /// <summary>The team's Talk rooms (its <c>talk</c> resources), by token.</summary>
    public async Task<Result<IReadOnlyList<string>>> RoomTokensAsync(string circleId, CancellationToken cancellationToken)
    {
        if (!Team.IsTeamId(circleId))
        {
            return NoTeam();
        }

        var resources = await dav.OcsGetAsync($"{ResourcesPath}/{circleId}/resources", null, cancellationToken).ConfigureAwait(false);
        if (!resources.IsSuccess)
        {
            return NotFoundAs(resources.Error);
        }

        var tokens = new List<string>();
        if (resources.Value.ValueKind == JsonValueKind.Object && resources.Value.TryGetProperty("resources", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var resource in list.EnumerateArray())
            {
                if (resource.TryGetProperty("provider", out var provider) && TalkJson.String(provider, "id") == TalkChannelRooms.TalkProvider
                    && TalkJson.String(resource, "id") is { } token && TalkIds.IsToken(token))
                {
                    tokens.Add(token);
                }
            }
        }

        return tokens;
    }

    internal static bool IsMemberId(string? id) => Team.IsTeamId(id);

    internal static Error NoTeam() => Error.NotFound("The team was not found, or you are not a member of it.");

    internal static Error NoMember() => Error.NotFound("The member was not found in this team.");

    private async Task<Result> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken)
    {
        var sent = await dav.OcsExchangeAsync(new OcsRequest(method, path) { Json = body }, cancellationToken).ConfigureAwait(false);
        return sent.IsSuccess ? Result.Success() : NotFoundAs(sent.Error);
    }

    private static Error NotFoundAs(Error error) => error.Kind == ErrorKind.NotFound ? NoTeam() : error;

    private static Error Unexpected(string what) => Error.BackendUnavailable($"Nextcloud Teams returned an unexpected {what}.");
}
