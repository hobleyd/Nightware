using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Time;
using Nightware.Application.Directory;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;
using Nightware.Domain.Presence;

namespace Nightware.Application.Presence;

/// <summary>
/// Reads one user's presence for <see cref="GetPresence"/> and <see cref="GetPresencesByUserId"/>. The caller's own
/// (however the route names them) is read from their private status, with the predefined message's id and
/// <c>invisible</c>; anyone else's from the public one (ADR 0009 D6).
/// </summary>
public sealed class PresenceReader(IUserStatusStore store, IUserAccountNames names, IAutoReplyStore autoReplies, IUserContext caller, TimeProvider time)
{
    /// <summary>The caller's presence.</summary>
    public async Task<Result<UserPresence>> OwnAsync(CancellationToken cancellationToken)
    {
        var status = await store.GetOwnAsync(cancellationToken).ConfigureAwait(false);
        if (!status.IsSuccess)
        {
            return status.Error;
        }

        var outOfOffice = await OutOfOfficeAsync(cancellationToken).ConfigureAwait(false);
        return PresenceMapping.ToPresence(caller.Principal.Id, status.Value, own: true, outOfOffice);
    }

    /// <summary>A user's presence by directory id or address; <see cref="ErrorKind.NotFound"/> for nobody in the directory.</summary>
    public async Task<Result<UserPresence>> OfAsync(string user, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        if (IsCaller(user))
        {
            return await OwnAsync(cancellationToken).ConfigureAwait(false);
        }

        var account = await names.AccountNameOfAsync(user, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var person = await names.UserOfAccountAsync(account.Value, cancellationToken).ConfigureAwait(false);
        if (!person.IsSuccess)
        {
            return person.Error;
        }

        if (string.Equals(person.Value.Id, caller.Principal.Id, StringComparison.OrdinalIgnoreCase))
        {
            return await OwnAsync(cancellationToken).ConfigureAwait(false);
        }

        var status = await store.GetAsync(account.Value, cancellationToken).ConfigureAwait(false);

        // Another user's auto-reply is in their mailbox, which the caller can't read: they are not reported out of office.
        return status.IsSuccess
            ? PresenceMapping.ToPresence(person.Value.Id, status.Value, own: false, OutOfOfficeSettings.NotOutOfOffice)
            : status.Error;
    }

    private bool IsCaller(string user)
    {
        var principal = caller.Principal;
        return string.Equals(user, principal.Id, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(principal.Mail) && string.Equals(user, principal.Mail, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The caller's automatic replies as <c>outOfOfficeSettings</c>. Presence comes from Nextcloud, so a mail server
    /// that can't answer leaves it "not out of office" rather than failing the read.
    /// </summary>
    private async Task<OutOfOfficeSettings> OutOfOfficeAsync(CancellationToken cancellationToken)
    {
        var mail = caller.Principal.Mail;
        if (string.IsNullOrWhiteSpace(mail))
        {
            return OutOfOfficeSettings.NotOutOfOffice;
        }

        var replies = await autoReplies.GetAsync(new MailboxRef(mail), cancellationToken).ConfigureAwait(false);
        return replies.IsSuccess && IsOn(replies.Value)
            ? new OutOfOfficeSettings(true, replies.Value.InternalReplyMessage)
            : OutOfOfficeSettings.NotOutOfOffice;
    }

    private bool IsOn(AutoReplySetting setting)
    {
        if (setting.Status == AutoReplyStatus.AlwaysEnabled)
        {
            return true;
        }

        if (setting is not { Status: AutoReplyStatus.Scheduled, ScheduledStartDateTime: { } start, ScheduledEndDateTime: { } end })
        {
            return false;
        }

        var now = time.GetUtcNow();
        return GraphTimeZone.ToInstant(start.DateTime, start.TimeZone) <= now && now < GraphTimeZone.ToInstant(end.DateTime, end.TimeZone);
    }
}
