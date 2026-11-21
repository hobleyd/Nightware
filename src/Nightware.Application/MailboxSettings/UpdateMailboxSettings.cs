using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Time;
using Nightware.Application.Directory;
using Nightware.Domain.Calendar;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.MailboxSettings;

/// <summary><c>PATCH mailboxSettings</c>: replaces the automatic replies and/or sets the time zone, then returns the result.</summary>
/// <remarks>
/// Authorisation follows <see cref="GetMailboxSettings"/>: <see cref="IMailboxSettingsAccess"/>,
/// acting as the caller, decides whether the mailbox may be opened before either store is touched
/// (the mail store still refuses a read-only delegate's write). The time zone lives in the directory, written by the
/// façade's service account, so it may only be changed for the caller's own mailbox — otherwise
/// any user could rewrite anyone's zone.
/// </remarks>
public sealed class UpdateMailboxSettings(IMailboxSettingsAccess access, IAutoReplyStore replies, IUserPreferencesStore preferences, IUserContext caller, IInternalDomains domains)
{
    public async Task<Result<MailboxSettingsSnapshot>> ExecuteAsync(MailboxRef mailbox, MailboxSettingsPatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (Validate(mailbox, patch) is { } invalid)
        {
            return invalid;
        }

        var allowed = await access.CheckAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!allowed.IsSuccess)
        {
            return allowed.Error!;
        }

        var written = patch.AutomaticReplies is { } setting
            ? await replies.SetAsync(mailbox, setting, await domains.ForAsync(mailbox, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false)
            : await replies.GetAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!written.IsSuccess)
        {
            return written.Error;
        }

        var stored = patch.TimeZone is { } zone
            ? MailboxSettingsComposer.OrNone(await preferences.SetAsync(
                mailbox, new UserPreferencesPatch { TimeZone = GraphTimeZone.ToIanaId(zone) }, cancellationToken).ConfigureAwait(false))
            : MailboxSettingsComposer.OrNone(await preferences.GetAsync(mailbox, cancellationToken).ConfigureAwait(false));
        return stored.Map(p => MailboxSettingsComposer.Compose(written.Value, p));
    }

    private Error? Validate(MailboxRef mailbox, MailboxSettingsPatch patch)
    {
        if (patch.IsEmpty)
        {
            return Error.Validation("The request changes nothing NightMail Server stores: only automaticRepliesSetting and timeZone are supported.");
        }

        if (patch.TimeZone is { } zone)
        {
            if (!GraphTimeZone.TryFind(zone, out _))
            {
                return Error.Validation($"'{zone}' is not a known time zone.", "timeZone");
            }

            if (!string.Equals(caller.Principal.Mail, mailbox.Address, StringComparison.OrdinalIgnoreCase))
            {
                return Error.Forbidden("Only the mailbox owner can change its time zone.");
            }
        }

        return patch.AutomaticReplies is { } replies ? ValidateSchedule(replies) : null;
    }

    private static Error? ValidateSchedule(AutoReplySetting replies)
    {
        foreach (var (value, target) in new[] { (replies.ScheduledStartDateTime, "scheduledStartDateTime"), (replies.ScheduledEndDateTime, "scheduledEndDateTime") })
        {
            if (value is not null && !GraphTimeZone.TryFind(value.TimeZone, out _))
            {
                return Error.Validation($"'{value.TimeZone}' is not a known time zone.", target);
            }
        }

        if (replies is { Status: AutoReplyStatus.Scheduled, ScheduledStartDateTime: { } start, ScheduledEndDateTime: { } end }
            && Instant(end) <= Instant(start))
        {
            return Error.Validation("The scheduled end must be after the scheduled start.", "scheduledEndDateTime");
        }

        return null;
    }

    private static DateTimeOffset Instant(DateTimeTimeZone value) => GraphTimeZone.ToInstant(value.DateTime, value.TimeZone);
}
