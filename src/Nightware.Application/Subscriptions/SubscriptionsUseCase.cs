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
    ISubscriptionStore store, ISubscriberCredentials credentials, INotificationEndpoint endpoint, IUserContext caller, IMailboxResolver mailboxes, TimeProvider time)
{
    /// <summary>Graph raises any expiry sooner than this to it.</summary>
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(45);

    public const int MaxClientStateLength = 128;

    /// <summary>Graph's maximum lifetime per resource (subscription resource type, "Subscription lifetime").</summary>
    public static TimeSpan MaximumLifetime(SubscriptionResourceKind kind) =>
        kind == SubscriptionResourceKind.Tasks ? TimeSpan.FromMinutes(4230) : TimeSpan.FromMinutes(10080);

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

        var expiry = Expiry(request.ExpirationDateTime, target.Value.Kind);
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

        var subscription = new Subscription(
            Guid.NewGuid().ToString(), caller.Principal.Id, request.Resource!.Trim(), target.Value, changeType.Value, url.Value, expiry.Value)
        {
            ClientState = request.ClientState,
            LifecycleNotificationUrl = lifecycle,
            ApplicationId = request.ApplicationId,
        };
        return await store.CreateAsync(subscription, credential.Value, cancellationToken).ConfigureAwait(false);
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
            var expiry = Expiry(wanted, updated.Target.Kind);
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

    private Result<DateTimeOffset> Expiry(DateTimeOffset? wanted, SubscriptionResourceKind kind)
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
    /// The resources Nightware notifies on: <c>{me|users/{id}}/messages</c>, <c>…/mailFolders('{id}')/messages</c>
    /// (or <c>/mailFolders/{id}/messages</c>), <c>…/events</c>, <c>…/contacts</c> and <c>…/todo/lists/{id}/tasks</c>.
    /// </summary>
    internal async Task<Result<SubscriptionTarget>> ParseResourceAsync(string? resource, CancellationToken cancellationToken)
    {
        var match = ResourcePattern().Match((resource ?? string.Empty).Trim().Trim('/'));
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
            "MESSAGES" => new SubscriptionTarget(mailbox.Value, SubscriptionResourceKind.Messages, folder),
            "EVENTS" => new SubscriptionTarget(mailbox.Value, SubscriptionResourceKind.Events, null),
            "CONTACTS" => new SubscriptionTarget(mailbox.Value, SubscriptionResourceKind.Contacts, null),
            _ => new SubscriptionTarget(mailbox.Value, SubscriptionResourceKind.Tasks, list),
        };
    }

    [GeneratedRegex(@"^(?:me|users/(?<user>[^/]+))/(?:mailFolders(?:\('(?<folder>[^']+)'\)|/(?<folder>[^/]+))/(?<kind>messages)|(?<kind>messages|events|contacts)|todo/lists/(?<list>[^/]+)/(?<kind>tasks))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourcePattern();
}
