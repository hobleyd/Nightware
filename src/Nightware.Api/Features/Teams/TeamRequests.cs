using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Nightware.Api.Features.Chats;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Query;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Api.Features.Teams;

/// <summary>Reads what team and channel requests carry: a new team, a team or channel change, a member's roles.</summary>
internal static partial class TeamRequests
{
    public const string TemplateBind = "template@odata.bind";

    /// <summary>A <c>/users/{x}/joinedTeams</c> route is the caller's own teams only: the façade reads Nextcloud as the caller.</summary>
    public static Error? OwnTeams(Mailbox mailbox, IUserContext caller)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(caller);
        return mailbox.IsMe || string.Equals(mailbox.Ref.Address, caller.Principal.Mail, StringComparison.OrdinalIgnoreCase)
            ? null
            : Error.Forbidden("Only your own teams can be listed.");
    }

    /// <summary>Graph's default page of channel messages (<c>$top</c> is up to 50).</summary>
    public const int DefaultMessagePage = 20;

    /// <summary>
    /// A list served whole (teams, members, channels): <c>$select</c> only. <c>$filter</c>, <c>$search</c>,
    /// <c>$orderby</c>, <c>$skip</c>, <c>$expand</c> and <c>$count</c> are refused rather than ignored.
    /// </summary>
    public static Error? WholeList(ItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Filter is not null || query.Search is not null || query.OrderBy.Count > 0 || query.Skip is not null || query.Expand.Count > 0 || query.Count
            ? Error.Validation("This list takes $select only.")
            : null;
    }

    /// <summary>
    /// A <c>POST /teams</c> body: the <c>standard</c> template (no other is offered), <c>displayName</c>,
    /// <c>description</c>, <c>visibility</c> and <c>members</c>.
    /// </summary>
    public static Result<TeamCreation> Creation(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("A team needs a body.");
        }

        var template = ChatRequests.Text(body, TemplateBind);
        var match = template is null ? Match.Empty : TemplateOfBind().Match(template);
        if (!match.Success)
        {
            return Error.Validation($"A team needs '{TemplateBind}' naming a template: …/teamsTemplates('standard').", TemplateBind);
        }

        if (!string.Equals(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, "standard", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("Only the 'standard' team template is offered.", TemplateBind);
        }

        var visibility = Visibility(body);
        if (!visibility.IsSuccess)
        {
            return visibility.Error;
        }

        var members = new List<ChatMemberDraft>();
        if (body["members"] is JsonArray array)
        {
            foreach (var node in array)
            {
                var member = ChatRequests.Member(node as JsonObject);
                if (!member.IsSuccess)
                {
                    return member.Error;
                }

                members.Add(member.Value);
            }
        }

        return new TeamCreation(ChatRequests.Text(body, "displayName") ?? string.Empty, ChatRequests.Text(body, "description"), visibility.Value, members);
    }

    /// <summary>A <c>PATCH /teams/{id}</c> body: <c>displayName</c>, <c>description</c>, <c>visibility</c>.</summary>
    public static Result<TeamUpdate> Update(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("A team change needs a body.");
        }

        var visibility = Visibility(body);
        return visibility.IsSuccess
            ? new TeamUpdate(ChatRequests.Text(body, "displayName"), ChatRequests.Text(body, "description"), visibility.Value)
            : visibility.Error;
    }

    /// <summary>A member's <c>roles</c>: <c>["owner"]</c> makes an owner, <c>[]</c> an ordinary member.</summary>
    public static Result<bool> IsOwner(JsonObject? body)
    {
        if (body?["roles"] is not JsonArray roles)
        {
            return Error.Validation("A member change needs its 'roles'.", "roles");
        }

        var names = roles.Select(r => r is JsonValue v && v.TryGetValue<string>(out var role) ? role : string.Empty).ToList();
        if (names.Any(r => !r.Equals(ChatMember.OwnerRole, StringComparison.OrdinalIgnoreCase)))
        {
            return Error.Validation("A member's roles are [] or [\"owner\"].", "roles");
        }

        return names.Count > 0;
    }

    /// <summary>A <c>POST …/channels</c> body: <c>displayName</c>, <c>description</c>; only a <c>standard</c> channel.</summary>
    public static Result<ChannelCreation> ChannelCreation(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("A channel needs a body.");
        }

        if (ChatRequests.Text(body, "membershipType") is { } type && !type.Equals(Channel.StandardMembershipType, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("Only standard channels are offered; private and shared channels are not.", "membershipType");
        }

        return new ChannelCreation(ChatRequests.Text(body, "displayName") ?? string.Empty, ChatRequests.Text(body, "description"));
    }

    /// <summary>A <c>PATCH …/channels/{id}</c> body: <c>displayName</c>, <c>description</c>.</summary>
    public static Result<ChannelUpdate> ChannelUpdate(JsonObject? body) =>
        body is null
            ? Error.Validation("A channel change needs a body.")
            : new ChannelUpdate(ChatRequests.Text(body, "displayName"), ChatRequests.Text(body, "description"));

    private static Result<TeamVisibility?> Visibility(JsonObject body) => ChatRequests.Text(body, "visibility")?.ToLowerInvariant() switch
    {
        null => (TeamVisibility?)null,
        "public" => TeamVisibility.Public,
        "private" => TeamVisibility.Private,
        _ => Error.Validation("visibility must be 'public' or 'private'.", "visibility"),
    };

    [GeneratedRegex(@"teamsTemplates(?:\('([^']+)'\)|/([^/?#()']+))/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TemplateOfBind();
}
