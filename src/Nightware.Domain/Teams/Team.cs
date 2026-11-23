using Nightware.Domain.Chats;

namespace Nightware.Domain.Teams;

/// <summary>Graph's <c>teamVisibilityType</c>: who can find and join the team.</summary>
public enum TeamVisibility
{
    Private,
    Public,
}

/// <summary>
/// A team (Graph <c>team</c>, ADR 0009 D5): a group of people the caller belongs to, with channels. Its id is the
/// backend's own (a Nextcloud team's circle id, 31 alphanumerics).
/// </summary>
public sealed record Team(string Id, string DisplayName)
{
    /// <summary>The length of a team id (a Nextcloud circle id).</summary>
    public const int IdLength = 31;

    public string? Description { get; init; }

    public TeamVisibility Visibility { get; init; } = TeamVisibility.Private;

    /// <summary>Null where the backend keeps no creation time.</summary>
    public DateTimeOffset? CreatedDateTime { get; init; }

    public string? WebUrl { get; init; }

    /// <summary>Whether <paramref name="id"/> has a team id's shape: it never collides with a directory group's UUID (ADR 0009 D2).</summary>
    public static bool IsTeamId(string? id) => id is { Length: IdLength } && id.All(char.IsAsciiLetterOrDigit);
}

/// <summary>A new team (Graph <c>POST /teams</c>, the <c>standard</c> template): its name, description, visibility and members other than the caller.</summary>
public sealed record TeamCreation(string DisplayName, string? Description, TeamVisibility? Visibility, IReadOnlyList<ChatMemberDraft> Members);

/// <summary>A <c>PATCH</c> of a team; null leaves a property as it is.</summary>
public sealed record TeamUpdate(string? DisplayName, string? Description, TeamVisibility? Visibility)
{
    public bool IsEmpty => DisplayName is null && Description is null && Visibility is null;
}

/// <summary>
/// The operation that created a team (Graph <c>teamsAsyncOperation</c>). Creation is done before Graph's <c>202</c> is
/// answered, so it has always succeeded; its id names the team, so it needs no storage (ADR 0009 D5).
/// </summary>
public sealed record TeamCreationOperation(string Id, string TeamId, DateTimeOffset? CreatedDateTime)
{
    private const string Prefix = "create.";

    public static string IdFor(string teamId)
    {
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        return Prefix + teamId;
    }

    /// <summary>The team an operation id names, or null for an id this façade didn't issue.</summary>
    public static string? TeamOf(string? operationId) =>
        operationId is not null && operationId.StartsWith(Prefix, StringComparison.Ordinal) && Team.IsTeamId(operationId[Prefix.Length..])
            ? operationId[Prefix.Length..]
            : null;
}

/// <summary>
/// A standard channel of a team (Graph <c>channel</c>, ADR 0009 D5). Its id is the backend's conversation id (a Talk room
/// token). Every channel is <c>standard</c>: everyone in the team is in it.
/// </summary>
public sealed record Channel(string Id, string DisplayName)
{
    public const string StandardMembershipType = "standard";

    /// <summary>Graph's limit on a channel's name.</summary>
    public const int MaxNameLength = 50;

    /// <summary>Characters Graph refuses in a channel's name (it also names the channel's folder).</summary>
    public const string ForbiddenNameCharacters = "~#%&*{}+/\\:<>?|'\"";

    public string? Description { get; init; }

    /// <summary>Null where the backend keeps no creation time.</summary>
    public DateTimeOffset? CreatedDateTime { get; init; }

    public string? WebUrl { get; init; }

    /// <summary>The team's primary channel (General): it can't be renamed or deleted.</summary>
    public bool IsPrimary { get; init; }
}

/// <summary>A new channel: its name and description.</summary>
public sealed record ChannelCreation(string DisplayName, string? Description);

/// <summary>A <c>PATCH</c> of a channel; null leaves a property as it is.</summary>
public sealed record ChannelUpdate(string? DisplayName, string? Description)
{
    public bool IsEmpty => DisplayName is null && Description is null;
}
