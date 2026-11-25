using System.Text.RegularExpressions;

using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Application.Subscriptions;

/// <summary>A create request, as the client sent it.</summary>
public sealed record SubscriptionRequest(string? Resource, string? ChangeType, string? NotificationUrl, DateTimeOffset? ExpirationDateTime)
{
    public string? ClientState { get; init; }

    public string? LifecycleNotificationUrl { get; init; }

    public bool IncludeResourceData { get; init; }

    public string? ApplicationId { get; init; }
}

/// <summary>A PATCH: a new expiry (renewal) and/or notification URL. Either one also renews the credential.</summary>
public sealed record SubscriptionPatch(DateTimeOffset? ExpirationDateTime, string? NotificationUrl);

/// <summary>
/// <c>/subscriptions</c> (ADR 0006 D10): validates as Graph does, runs the validation handshake, then stores the
/// subscription with a refresh credential for the caller. Only the creator sees or changes a subscription.
/// </summary>
public sealed partial class SubscriptionsUseCase(
    ISubscriptionStore store, ISubscriberCredentials credentials, INotificationEndpoint endpoint, ChangeFeed feed, IUserContext caller, IMailboxResolver mailboxes, TimeProvider time,
    ISubscriptionItems items)
{
    /// <summary>Graph raises any expiry sooner than this to it.</summary>
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(45);

    public const int MaxClientStateLength = 128;

    /// <summary>Past this, a Teams subscription needs a <c>lifecycleNotificationUrl</c>, as Graph's does.</summary>
    public static readonly TimeSpan TeamsWithoutLifecycle = TimeSpan.FromHours(1);

    /// <summary>Graph's maximum lifetime per resource (subscription resource type, "Subscription lifetime").</summary>
    public static TimeSpan MaximumLifetime(SubscriptionResourceKind kind) => kind switch
    {
        SubscriptionResourceKind.Tasks => TimeSpan.FromMinutes(4230),
        _ when kind.IsTeams() => TimeSpan.FromMinutes(4320),
        _ => TimeSpan.FromMinutes(10080),
    };

    public async Task<Result<Subscription>> CreateAsync(SubscriptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IncludeResourceData)
        {
            return Error.Validation("Rich notifications (includeResourceData) are not supported.", "includeResourceData");
        }

        var changeType = ParseChangeType(request.ChangeType);
        if (!changeType.IsSuccess)
        {
            return changeType.Error;
        }

        var target = await ParseResourceAsync(request.Resource, cancellationToken).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        var url = HttpsUrl(request.NotificationUrl, "notificationUrl");
        if (!url.IsSuccess)
        {
            return url.Error;
        }

        Uri? lifecycle = null;
        if (request.LifecycleNotificationUrl is { Length: > 0 })
        {
            var parsed = HttpsUrl(request.LifecycleNotificationUrl, "lifecycleNotificationUrl");
            if (!parsed.IsSuccess)
            {
                return parsed.Error;
            }

            lifecycle = parsed.Value;
        }

        if (request.ClientState is { Length: > MaxClientStateLength })
        {
            return Error.Validation($"clientState may have at most {MaxClientStateLength} characters.", "clientState");
        }

        var expiry = Expiry(request.ExpirationDateTime, target.Value.Kind, lifecycle is not null);
        if (!expiry.IsSuccess)
        {
            return expiry.Error;
        }

        var validated = await endpoint.ValidateAsync(url.Value, cancellationToken).ConfigureAwait(false);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        if (lifecycle is not null)
        {
            var lifecycleValidated = await endpoint.ValidateAsync(lifecycle, cancellationToken).ConfigureAwait(false);
            if (!lifecycleValidated.IsSuccess)
            {
                return lifecycleValidated.Error;
            }
        }

        var credential = await credentials.IssueAsync(cancellationToken).ConfigureAwait(false);
        if (!credential.IsSuccess)
        {
            return credential.Error;
        }

        // The state as of now, read with the caller's own credential: what exists already is never announced.
        var baseline = await feed.BaselineAsync(target.Value, cancellationToken).ConfigureAwait(false);
        if (!baseline.IsSuccess)
        {
            return baseline.Error;
        }

        var subscription = new Subscription(
            Guid.NewGuid().ToString(), caller.Principal.Id, request.Resource!.Trim(), target.Value, changeType.Value, url.Value, expiry.Value)
        {
            ClientState = request.ClientState,
            LifecycleNotificationUrl = lifecycle,
            ApplicationId = request.ApplicationId,
            SyncState = baseline.Value.State,
        };
        var created = await store.CreateAsync(subscription, credential.Value, cancellationToken).ConfigureAwait(false);
        if (created.IsSuccess && !target.Value.Kind.IsTeams())
        {
            // Best effort: untracked, the subscription falls back to the feed's own created/updated guess.
            await items.ReplaceAsync(subscription.Id, baseline.Value.Ids, cancellationToken).ConfigureAwait(false);
        }

        return created;
    }

    public Task<Result<IReadOnlyList<Subscription>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAsync(caller.Principal.Id, cancellationToken);

    public Task<Result<Subscription>> GetAsync(string id, CancellationToken cancellationToken) =>
        store.GetAsync(caller.Principal.Id, id, cancellationToken);

    /// <summary>
    /// Renewal: a new expiry and/or notification URL. The caller's current token is exchanged afresh, so a PATCH
    /// also reauthorizes a subscription whose credential has lapsed.
    /// </summary>
    public async Task<Result<Subscription>> UpdateAsync(string id, SubscriptionPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var existing = await store.GetAsync(caller.Principal.Id, id, cancellationToken).ConfigureAwait(false);
        if (!existing.IsSuccess)
        {
            return existing.Error;
        }

        var updated = existing.Value;
        if (patch.ExpirationDateTime is { } wanted)
        {
            var expiry = Expiry(wanted, updated.Target.Kind, updated.LifecycleNotificationUrl is not null);
            if (!expiry.IsSuccess)
            {
                return expiry.Error;
            }

            updated = updated with { ExpirationDateTime = expiry.Value };
        }

        if (patch.NotificationUrl is not null)
        {
            var url = HttpsUrl(patch.NotificationUrl, "notificationUrl");
            if (!url.IsSuccess)
            {
                return url.Error;
            }

            var validated = await endpoint.ValidateAsync(url.Value, cancellationToken).ConfigureAwait(false);
            if (!validated.IsSuccess)
            {
                return validated.Error;
            }

            updated = updated with { NotificationUrl = url.Value };
        }

        var credential = await credentials.IssueAsync(cancellationToken).ConfigureAwait(false);
        return credential.IsSuccess ? await store.UpdateAsync(updated, credential.Value, cancellationToken).ConfigureAwait(false) : credential.Error;
    }

    /// <summary>Deleting discards the sealed credential with the row; Keycloak's session is left alone.</summary>
    public Task<Result> DeleteAsync(string id, CancellationToken cancellationToken) =>
        store.DeleteAsync(caller.Principal.Id, id, cancellationToken);

    private Result<DateTimeOffset> Expiry(DateTimeOffset? wanted, SubscriptionResourceKind kind, bool hasLifecycleUrl)
    {
        if (wanted is not { } value)
        {
            return Error.Validation("expirationDateTime is required.", "expirationDateTime");
        }

        var now = time.GetUtcNow();
        var max = MaximumLifetime(kind);
        if (value > now + max)
        {
            return Error.Validation($"Subscription expiration can only be {max.TotalMinutes} minutes in the future.", "expirationDateTime");
        }

        if (kind.IsTeams() && !hasLifecycleUrl && value > now + TeamsWithoutLifecycle)
        {
            return Error.Validation(
                "lifecycleNotificationUrl is a required property for subscription creation on this resource when the expirationDateTime value is set to greater than 1 hour.",
                "lifecycleNotificationUrl");
        }

        return value < now + MinimumLifetime ? now + MinimumLifetime : value;
    }

    internal static Result<SubscriptionChangeType> ParseChangeType(string? value)
    {
        var result = SubscriptionChangeType.None;
        foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<SubscriptionChangeType>(part, ignoreCase: true, out var one) || one == SubscriptionChangeType.None)
            {
                return Error.Validation($"'{part}' is not a change type: use created, updated and/or deleted.", "changeType");
            }

            result |= one;
        }

        return result == SubscriptionChangeType.None ? Error.Validation("changeType is required.", "changeType") : result;
    }

    private static Result<Uri> HttpsUrl(string? value, string target) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? uri
            : Error.Validation($"{target} must be an absolute HTTPS URL.", target);

    /// <summary>
    /// The resources Nightware notifies on: <c>{me|users/{id}}/mailFolders('{id}')/messages</c>
    /// (or <c>/mailFolders/{id}/messages</c>), <c>…/events</c>, <c>…/contacts</c> and <c>…/todo/lists/{id}/tasks</c>.
    /// </summary>
    internal async Task<Result<SubscriptionTarget>> ParseResourceAsync(string? resource, CancellationToken cancellationToken)
    {
        var path = (resource ?? string.Empty).Trim().Trim('/');
        if (path.Contains('?', StringComparison.Ordinal))
        {
            return Error.Validation("Query options on a subscription's resource ($filter, $search, notifyOnUserSpecificProperties) are not supported.", "resource");
        }

        var teams = TeamsResource(path);
        if (teams.Kind is { } teamsKind)
        {
            return await TeamsTargetAsync(teamsKind, teams.User, teams.Folder, cancellationToken).ConfigureAwait(false);
        }

        var match = ResourcePattern().Match(path);
        if (!match.Success)
        {
            return Error.Validation($"Resource '{resource}' does not support change notifications here.", "resource");
        }

        var user = match.Groups["user"].Success ? Uri.UnescapeDataString(match.Groups["user"].Value) : null;
        var mailbox = await mailboxes.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
        if (!mailbox.IsSuccess)
        {
            return mailbox.Error;
        }

        var folder = match.Groups["folder"].Success ? Uri.UnescapeDataString(match.Groups["folder"].Value) : null;
        var list = match.Groups["list"].Success ? Uri.UnescapeDataString(match.Groups["list"].Value) : null;
        return match.Groups["kind"].Value.ToUpperInvariant() switch
        {
            // Mail delta is per folder, so a subscription names one: me/messages (every folder) is not offered.
            "MESSAGES" when folder is null => Error.Unsupported("Subscribe to one folder's messages: {mailbox}/mailFolders('{id}')/messages.", "resource"),
            "MESSAGES" => new SubscriptionTarget(mailbox.Value, SubscriptionResourceKind.Messages, folder),
            "EVENTS" => new SubscriptionTarget(mailbox.Value, SubscriptionResourceKind.Events, null),
            "CONTACTS" => new SubscriptionTarget(mailbox.Value, SubscriptionResourceKind.Contacts, null),
            _ => new SubscriptionTarget(mailbox.Value, SubscriptionResourceKind.Tasks, list),
        };
    }

    /// <summary>
    /// The Teams resources (ADR 0009 D11): <c>chats/{id}/messages</c>, <c>{me|users/{id}}/chats/getAllMessages</c>,
    /// <c>teams/{id}/channels/{id}/messages</c>, <c>{me|users/{id}}/chats</c>, <c>chats/{id}</c> and
    /// <c>chats/{id}/members</c>, with ids as segments or <c>('…')</c>; null kind for anything else.
    /// </summary>
    internal static (SubscriptionResourceKind? Kind, string? User, string? Folder) TeamsResource(string path)
    {
        static string Id(Match m, string group) => Uri.UnescapeDataString(m.Groups[group].Value);
        if (ChatPattern().Match(path) is { Success: true } chat)
        {
            var kind = chat.Groups["part"].Value.ToUpperInvariant() switch
            {
                "MESSAGES" => SubscriptionResourceKind.ChatMessages,
                "MEMBERS" => SubscriptionResourceKind.ChatMembers,
                _ => SubscriptionResourceKind.Chat,
            };
            return (kind, null, Id(chat, "chat"));
        }

        if (UserChatsPattern().Match(path) is { Success: true } user)
        {
            var kind = user.Groups["all"].Success ? SubscriptionResourceKind.AllChatMessages : SubscriptionResourceKind.Chats;
            return (kind, user.Groups["user"].Success ? Id(user, "user") : null, null);
        }

        return ChannelPattern().Match(path) is { Success: true } channel
            ? (SubscriptionResourceKind.ChannelMessages, null, Id(channel, "team") + "/" + Id(channel, "channel"))
            : (null, null, null);
    }

    /// <summary>A Teams target: delegated only, so a named user must be the caller.</summary>
    private async Task<Result<SubscriptionTarget>> TeamsTargetAsync(SubscriptionResourceKind kind, string? user, string? folder, CancellationToken cancellationToken)
    {
        var own = await mailboxes.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        if (!own.IsSuccess)
        {
            return own.Error;
        }

        if (user is not null)
        {
            var named = await mailboxes.ResolveAsync(user, cancellationToken).ConfigureAwait(false);
            if (!named.IsSuccess)
            {
                return named.Error;
            }

            if (!string.Equals(named.Value.Address, own.Value.Address, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Forbidden("Chat change notifications are only for the signed-in user's own chats.");
            }
        }

        return new SubscriptionTarget(own.Value, kind, folder);
    }

    [GeneratedRegex(@"^chats(?!/getAll|\('getAll)(?:\('(?<chat>[^'/]+)'\)|/(?<chat>[^/()']+))(?:/(?<part>messages|members))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChatPattern();

    [GeneratedRegex(@"^(?:me|users/(?<user>[^/]+))/chats(?<all>/getAllMessages)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserChatsPattern();

    [GeneratedRegex(@"^teams(?:\('(?<team>[^'/]+)'\)|/(?<team>[^/()']+))/channels(?:\('(?<channel>[^'/]+)'\)|/(?<channel>[^/()']+))/messages$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChannelPattern();

    [GeneratedRegex(@"^(?:me|users/(?<user>[^/]+))/(?:mailFolders(?:\('(?<folder>[^']+)'\)|/(?<folder>[^/]+))/(?<kind>messages)|(?<kind>messages|events|contacts)|todo/lists/(?<list>[^/]+)/(?<kind>tasks))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourcePattern();
}
