using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.Administration.AutoReplies;

/// <summary>
/// A mailbox's automatic replies, set by an administrator rather than its owner: the same store and mapping the
/// façade's <c>mailboxSettings</c> uses (internal and external messages, audience, schedule), acting with the
/// console's mail-server credential on the account. Forbidden where the console's mail role may not.
/// </summary>
public interface IMailboxAutoReplies
{
    Task<Result<AutoReplySetting>> GetAsync(string mailAccountId, MailAddress address, CancellationToken cancellationToken);

    /// <param name="internalDomains">Senders from these get the internal reply: the entity's domains.</param>
    Task<Result<AutoReplySetting>> SetAsync(string mailAccountId, MailAddress address, AutoReplySetting setting, IReadOnlyList<string> internalDomains, CancellationToken cancellationToken);
}

/// <summary>Where a mailbox's replies live: its mail account, address and the entity's domains.</summary>
internal sealed record AutoReplyTarget(string MailAccountId, MailAddress Address, IReadOnlyList<string> InternalDomains, string? EntityId);

/// <summary>A user's or a shared mailbox's automatic replies, read and written by an administrator who manages them. Writes are audited.</summary>
public sealed class MailboxAutoReplies(
    IAdminActor actor, UserAccess users, SharedMailboxAccess mailboxes, IEntityRegistry entities, ISharedMailAccounts shared, IMailboxAutoReplies replies, IAuditLog audit)
{
    public async Task<Result<AutoReplySetting>> GetForUserAsync(string userId, CancellationToken cancellationToken)
    {
        Result<AutoReplyTarget> target = await UserTargetAsync(userId, cancellationToken).ConfigureAwait(false);
        return target.IsSuccess ? await replies.GetAsync(target.Value.MailAccountId, target.Value.Address, cancellationToken).ConfigureAwait(false) : target.Error;
    }

    public async Task<Result<AutoReplySetting>> SetForUserAsync(string userId, AutoReplySetting setting, CancellationToken cancellationToken)
    {
        Result<AutoReplyTarget> target = await UserTargetAsync(userId, cancellationToken).ConfigureAwait(false);
        return target.IsSuccess ? await SetAsync(target.Value, setting, userId, null, cancellationToken).ConfigureAwait(false) : target.Error;
    }

    public async Task<Result<AutoReplySetting>> GetForMailboxAsync(string entityId, string mailboxId, CancellationToken cancellationToken)
    {
        Result<AutoReplyTarget> target = await MailboxTargetAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        return target.IsSuccess ? await replies.GetAsync(target.Value.MailAccountId, target.Value.Address, cancellationToken).ConfigureAwait(false) : target.Error;
    }

    public async Task<Result<AutoReplySetting>> SetForMailboxAsync(string entityId, string mailboxId, AutoReplySetting setting, CancellationToken cancellationToken)
    {
        Result<AutoReplyTarget> target = await MailboxTargetAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        return target.IsSuccess ? await SetAsync(target.Value, setting, null, mailboxId, cancellationToken).ConfigureAwait(false) : target.Error;
    }

    private async Task<Result<AutoReplySetting>> SetAsync(AutoReplyTarget target, AutoReplySetting setting, string? userId, string? mailboxId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (setting.Status == AutoReplyStatus.Scheduled && setting.ScheduledStartDateTime is { } start && setting.ScheduledEndDateTime is { } end
            && Common.Time.GraphTimeZone.ToInstant(end.DateTime, end.TimeZone) <= Common.Time.GraphTimeZone.ToInstant(start.DateTime, start.TimeZone))
        {
            return Error.Validation("The replies must end after they start.", "end");
        }

        Result<AutoReplySetting> written = await replies.SetAsync(target.MailAccountId, target.Address, setting, target.InternalDomains, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("mailbox.autoreply.set", actor.Current.UserId, written.IsSuccess)
        {
            TargetUserId = userId,
            EntityId = target.EntityId,
            Details = new Dictionary<string, string?> { ["address"] = target.Address.Value, ["mailboxId"] = mailboxId, ["status"] = setting.Status.ToString() },
        });
        return written;
    }

    private async Task<Result<AutoReplyTarget>> UserTargetAsync(string userId, CancellationToken cancellationToken)
    {
        Result<UserDetail> user = await users.LoadWithLinksAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return user.Error;
        }

        BackendLink mail = user.Value.For(BackendKind.Mail);
        if (mail.State != LinkState.Linked || mail.DownstreamId is null || !MailAddress.TryParse(user.Value.User.Identity.Email, out MailAddress? address))
        {
            return Error.Validation("The user has no mailbox, so has no automatic replies.");
        }

        return new AutoReplyTarget(mail.DownstreamId, address, await DomainsAsync(user.Value.User.EntityId, address, cancellationToken).ConfigureAwait(false), user.Value.User.EntityId);
    }

    private async Task<Result<AutoReplyTarget>> MailboxTargetAsync(string entityId, string mailboxId, CancellationToken cancellationToken)
    {
        Result<(Entity Entity, SharedMailbox Mailbox)> loaded = await mailboxes.LoadAsync(entityId, mailboxId, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        Result<SharedMailAccount> account = await shared.FindByAddressAsync(loaded.Value.Mailbox.Address, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error.Kind == ErrorKind.NotFound ? Error.Validation("The shared mailbox is not on the mail server yet; Repair it first.") : account.Error;
        }

        IReadOnlyList<string> domains = [.. loaded.Value.Entity.Domains.Select(static d => d.Name.ToLowerInvariant())];
        return new AutoReplyTarget(account.Value.Id, loaded.Value.Mailbox.Address, domains, entityId);
    }

    /// <summary>The entity's domains, or the mailbox's own when it belongs to none (or the entity can't be read).</summary>
    private async Task<IReadOnlyList<string>> DomainsAsync(string? entityId, MailAddress address, CancellationToken cancellationToken)
    {
        string own = address.Value[(address.Value.IndexOf('@', StringComparison.Ordinal) + 1)..].ToLowerInvariant();
        if (entityId is null || await entities.GetAsync(entityId, cancellationToken).ConfigureAwait(false) is not { IsSuccess: true } entity)
        {
            return [own];
        }

        return [.. entity.Value.Domains.Select(static d => d.Name.ToLowerInvariant()).Append(own).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }
}
