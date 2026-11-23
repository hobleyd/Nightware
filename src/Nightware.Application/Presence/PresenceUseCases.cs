using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using Nightware.Domain.Presence;

namespace Nightware.Application.Presence;

/// <summary>Graph <c>GET /me/presence</c>, <c>/users/{id}/presence</c> and <c>/communications/presences/{id}</c>.</summary>
public sealed class GetPresence(PresenceReader reader)
{
    /// <param name="user">A directory id or address; null for the caller.</param>
    public Task<Result<UserPresence>> ExecuteAsync(string? user, CancellationToken cancellationToken) =>
        user is null ? reader.OwnAsync(cancellationToken) : reader.OfAsync(user, cancellationToken);
}

/// <summary>Graph <c>POST /communications/getPresencesByUserId</c>: one status read per id; ids nobody has are left out.</summary>
public sealed class GetPresencesByUserId(PresenceReader reader)
{
    /// <summary>Graph's own limit.</summary>
    public const int MaxIds = 650;

    // Each id is up to three requests (two of them cached); a few at a time, as the drive resolves parents.
    private const int Concurrency = 8;

    public async Task<Result<IReadOnlyList<UserPresence>>> ExecuteAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinct = ids.Where(static id => !string.IsNullOrWhiteSpace(id)).Select(static id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0)
        {
            return Error.Validation("ids must name at least one user.", "ids");
        }

        if (distinct.Count > MaxIds)
        {
            return Error.Validation($"ids can name at most {MaxIds} users.", "ids");
        }

        var results = new Result<UserPresence>[distinct.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, distinct.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = cancellationToken },
            async (i, ct) => results[i] = await reader.OfAsync(distinct[i], ct).ConfigureAwait(false)).ConfigureAwait(false);

        var presences = new List<UserPresence>(distinct.Count);
        foreach (var result in results)
        {
            if (result.IsSuccess)
            {
                presences.Add(result.Value);
            }
            else if (result.Error.Kind != ErrorKind.NotFound)
            {
                return result.Error;
            }
        }

        return presences;
    }
}

/// <summary>A <c>setPresence</c> or <c>setUserPreferredPresence</c> request.</summary>
/// <param name="ExpirationDuration">When the presence lapses; kept only when the pair sets a message (ADR 0009 D6).</param>
public sealed record PresenceChange(string? Availability, string? Activity, TimeSpan? ExpirationDuration)
{
    /// <summary><c>setPresence</c>'s application session id, which Graph requires and Nextcloud has no use for.</summary>
    public string? SessionId { get; init; }
}

/// <summary>Graph <c>POST …/presence/setPresence</c>: sets the user-defined status (one per user, not per application).</summary>
public sealed class SetPresence(UserStatusWriter writer)
{
    public static readonly TimeSpan MinExpiration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxExpiration = TimeSpan.FromHours(4);

    /// <param name="target">The user the route names; null for <c>/me</c>. Only the caller may set their presence.</param>
    public async Task<Result> ExecuteAsync(MailboxRef? target, PresenceChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (writer.RefuseOthers(target) is { } refused)
        {
            return refused;
        }

        if (string.IsNullOrWhiteSpace(change.SessionId))
        {
            return Error.Validation("sessionId is required.", "sessionId");
        }

        if (change.ExpirationDuration is { } duration && (duration < MinExpiration || duration > MaxExpiration))
        {
            return Error.Validation("expirationDuration must be between PT5M and PT4H.", "expirationDuration");
        }

        var status = PresenceMapping.ToStatus(change.Availability, change.Activity);
        return status.IsSuccess ? await writer.ApplyAsync(status.Value, change.ExpirationDuration, cancellationToken).ConfigureAwait(false) : status.Error;
    }
}

/// <summary>Graph <c>POST …/presence/setUserPreferredPresence</c>: the same user-defined status as <see cref="SetPresence"/>.</summary>
public sealed class SetUserPreferredPresence(UserStatusWriter writer)
{
    public async Task<Result> ExecuteAsync(MailboxRef? target, PresenceChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (writer.RefuseOthers(target) is { } refused)
        {
            return refused;
        }

        if (change.ExpirationDuration is { } duration && duration <= TimeSpan.Zero)
        {
            return Error.Validation("expirationDuration must be positive.", "expirationDuration");
        }

        var status = PresenceMapping.ToStatus(change.Availability, change.Activity);
        return status.IsSuccess ? await writer.ApplyAsync(status.Value, change.ExpirationDuration, cancellationToken).ConfigureAwait(false) : status.Error;
    }
}

/// <summary>Graph <c>POST …/presence/clearPresence</c>: back to online.</summary>
public sealed class ClearPresence(UserStatusWriter writer)
{
    public async Task<Result> ExecuteAsync(MailboxRef? target, string? sessionId, CancellationToken cancellationToken)
    {
        if (writer.RefuseOthers(target) is { } refused)
        {
            return refused;
        }

        return string.IsNullOrWhiteSpace(sessionId)
            ? Error.Validation("sessionId is required.", "sessionId")
            : await writer.ClearAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>POST …/presence/clearUserPreferredPresence</c>: back to online.</summary>
public sealed class ClearUserPreferredPresence(UserStatusWriter writer)
{
    public async Task<Result> ExecuteAsync(MailboxRef? target, CancellationToken cancellationToken) =>
        writer.RefuseOthers(target) is { } refused ? refused : await writer.ClearAsync(cancellationToken).ConfigureAwait(false);
}

/// <summary>A <c>setStatusMessage</c> request: plain text (HTML already reduced to text) and when it expires.</summary>
public sealed record StatusMessageChange(string? Content, DateTimeOffset? ExpiryDateTime);

/// <summary>
/// Graph <c>POST …/presence/setStatusMessage</c>: a custom Nextcloud message with <c>clearAt</c>; empty content clears
/// it. Nextcloud clears the status with an expired message, so the user then reads offline.
/// </summary>
public sealed class SetStatusMessage(IUserStatusStore store, UserStatusWriter writer, TimeProvider time)
{
    public async Task<Result> ExecuteAsync(MailboxRef? target, StatusMessageChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (writer.RefuseOthers(target) is { } refused)
        {
            return refused;
        }

        if (string.IsNullOrWhiteSpace(change.Content))
        {
            return await store.ClearMessageAsync(cancellationToken).ConfigureAwait(false);
        }

        if (change.ExpiryDateTime is { } expiry && expiry <= time.GetUtcNow())
        {
            return Error.Validation("expiryDateTime must be in the future.", "expiryDateTime");
        }

        return await store.SetCustomMessageAsync(change.Content.Trim(), change.ExpiryDateTime, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Writes the caller's status for the presence use cases (ADR 0009 D6).</summary>
public sealed class UserStatusWriter(IUserStatusStore store, IUserContext caller, TimeProvider time)
{
    /// <summary>Nextcloud lets a user set only their own status, as Graph's delegated permission does: anyone else is 403.</summary>
    public Error? RefuseOthers(MailboxRef? target) =>
        target is null || string.Equals(target.Value.Address, caller.Principal.Mail, StringComparison.OrdinalIgnoreCase)
            ? null
            : Error.Forbidden("Presence can be set only for the signed-in user.");

    /// <summary>
    /// Sets the status, then the pair's predefined message (expiring after <paramref name="expiration"/>), or clears an
    /// activity message an earlier pair set. A custom message stays.
    /// </summary>
    public async Task<Result> ApplyAsync(StatusChange change, TimeSpan? expiration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var current = await store.GetOwnAsync(cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return current.Error;
        }

        var set = await store.SetStatusAsync(change.Type, cancellationToken).ConfigureAwait(false);
        if (!set.IsSuccess)
        {
            return set;
        }

        if (change.MessageId is { } messageId)
        {
            // Nextcloud can expire a message (and the status with it), never a bare status.
            DateTimeOffset? clearAt = expiration is { } after ? time.GetUtcNow() + after : null;
            return await store.SetPredefinedMessageAsync(messageId, clearAt, cancellationToken).ConfigureAwait(false);
        }

        return PredefinedMessages.IsActivity(current.Value.MessageId)
            ? await store.ClearMessageAsync(cancellationToken).ConfigureAwait(false)
            : Result.Success();
    }

    /// <summary>Back to online, without an activity message the façade (or Talk, or the calendar) set.</summary>
    public Task<Result> ClearAsync(CancellationToken cancellationToken) =>
        ApplyAsync(new StatusChange(UserStatusType.Online, null), null, cancellationToken);
}
