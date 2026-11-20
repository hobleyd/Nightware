using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Rules;
using Nightware.Application.Common.Time;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Adapters.Jmap.MailboxSettings;

/// <summary>
/// Automatic replies as the JMAP <c>VacationResponse</c> singleton (RFC 8621 §8).
/// </summary>
/// <remarks>
/// <para>Status: <c>disabled</c> ↔ <c>isEnabled: false</c>; <c>scheduled</c> ↔ enabled with
/// <c>fromDate</c>/<c>toDate</c>; <c>alwaysEnabled</c> ↔ enabled without dates. A <c>scheduled</c>
/// write without dates therefore reads back as <c>alwaysEnabled</c> — both mean "on".</para>
/// <para>VacationResponse has one message and replies to every sender, so Graph's
/// <c>externalAudience</c> and the internal/external split cannot be kept. The <b>external</b>
/// message is stored (the internal one only when there is no external one): it is the text written
/// for people outside the organisation, and outsiders are exactly whom Stalwart will also answer —
/// storing the internal text could send internal detail to strangers. On read both messages carry
/// the stored text and the audience is <c>all</c>, which is what the server actually does.</para>
/// <para>Once the mailbox has Inbox rules, Nightware's Sieve script holds the reply too (one script is active at
/// a time, ADR 0006 D5): while that script is active it is read and written there, with the same mapping.</para>
/// </remarks>
internal sealed class JmapAutoReplyStore(IJmapClient jmap, NightwareSieve sieve) : IAutoReplyStore
{
    private static readonly string[] Singleton = [VacationResponse.SingletonId];

    public async Task<Result<AutoReplySetting>> GetAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken, JmapCapabilities.VacationResponse).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var filter = await FilterAsync(account.Value, cancellationToken).ConfigureAwait(false);
        if (!filter.IsSuccess)
        {
            return filter.Error;
        }

        if (filter.Value is { Exists: true, IsActive: true } ours)
        {
            return Normalise(ours.State.AutoReply ?? new AutoReplySetting(AutoReplyStatus.Disabled));
        }

        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.VacationResponseGet, new GetRequest(account.Value.AccountId) { Ids = Singleton });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(get)).Map(ToDomain);
    }

    public async Task<Result<AutoReplySetting>> SetAsync(MailboxRef mailbox, AutoReplySetting setting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken, JmapCapabilities.VacationResponse).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        if (account.Value.IsReadOnly)
        {
            return Error.Forbidden($"The signed-in user cannot change the automatic replies of '{mailbox.Address}'.");
        }

        var filter = await FilterAsync(account.Value, cancellationToken).ConfigureAwait(false);
        if (!filter.IsSuccess)
        {
            return filter.Error;
        }

        if (filter.Value is { Exists: true } ours)
        {
            // Writing here also makes the script active again if vacation was switched on elsewhere.
            var normalised = Normalise(setting);
            var saved = await sieve.SaveAsync(account.Value, ours, ours.State with { AutoReply = normalised }, cancellationToken).ConfigureAwait(false);
            return saved.IsSuccess ? normalised : saved.Error;
        }

        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.VacationResponseSet, new SetRequest<VacationResponse>(account.Value.AccountId)
        {
            Update = new Dictionary<string, PatchObject> { [VacationResponse.SingletonId] = ToPatch(setting) },
        });
        var get = request.Add(JmapMethods.VacationResponseGet, new GetRequest(account.Value.AccountId) { Ids = Singleton });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var written = response.Value.Get(set);
        var updated = written.IsSuccess ? written.Value.Updated(VacationResponse.SingletonId) : written.ToResult();
        return updated.IsSuccess ? response.Value.Get(get).Map(ToDomain) : updated.Error;
    }

    /// <summary>Nightware's script, where the account has Sieve; none otherwise.</summary>
    private async Task<Result<LoadedFilter?>> FilterAsync(JmapAccount account, CancellationToken cancellationToken)
    {
        if (!account.Session.HasAccountCapability(account.AccountId, JmapCapabilities.Sieve))
        {
            return Result<LoadedFilter?>.Success(null);
        }

        var loaded = await sieve.LoadAsync(account, cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess ? Result<LoadedFilter?>.Success(loaded.Value) : loaded.Error;
    }

    /// <summary>What a setting reads back as once stored: the same mapping as <see cref="ToPatch"/> then <see cref="ToDomain"/>.</summary>
    internal static AutoReplySetting Normalise(AutoReplySetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (setting.Status == AutoReplyStatus.Disabled)
        {
            return new AutoReplySetting(AutoReplyStatus.Disabled);
        }

        var dated = setting.Status != AutoReplyStatus.AlwaysEnabled && (setting.ScheduledStartDateTime is not null || setting.ScheduledEndDateTime is not null);
        var message = !string.IsNullOrEmpty(setting.ExternalReplyMessage) ? setting.ExternalReplyMessage : setting.InternalReplyMessage ?? string.Empty;
        return new AutoReplySetting(dated ? AutoReplyStatus.Scheduled : AutoReplyStatus.AlwaysEnabled)
        {
            ExternalAudience = ExternalAudience.All,
            InternalReplyMessage = message,
            ExternalReplyMessage = message,
            ScheduledStartDateTime = dated ? Utc(setting.ScheduledStartDateTime) : null,
            ScheduledEndDateTime = dated ? Utc(setting.ScheduledEndDateTime) : null,
        };
    }

    private static DateTimeTimeZone? Utc(DateTimeTimeZone? value) =>
        value is null ? null : DateTimeTimeZone.Utc(GraphTimeZone.ToInstant(value.DateTime, value.TimeZone));

    internal static PatchObject ToPatch(AutoReplySetting setting)
    {
        var enabled = setting.Status != AutoReplyStatus.Disabled;
        var dated = setting.Status != AutoReplyStatus.AlwaysEnabled;
        var message = !string.IsNullOrEmpty(setting.ExternalReplyMessage) ? setting.ExternalReplyMessage : setting.InternalReplyMessage ?? string.Empty;
        return new Patch()
            .Set("isEnabled", JsonValue.Create(enabled))
            .Set("fromDate", dated ? UtcDate(setting.ScheduledStartDateTime) : null)
            .Set("toDate", dated ? UtcDate(setting.ScheduledEndDateTime) : null)
            .Set("htmlBody", JsonValue.Create(message))
            .Set("textBody", JsonValue.Create(HtmlToText.Convert(message)))
            .Build();
    }

    internal static AutoReplySetting ToDomain(GetResponse<VacationResponse> response)
    {
        var vacation = response.List.FirstOrDefault(v => v.Id == VacationResponse.SingletonId);
        if (vacation is null)
        {
            return new AutoReplySetting(AutoReplyStatus.Disabled);
        }

        var status = vacation.IsEnabled != true
            ? AutoReplyStatus.Disabled
            : vacation.FromDate is not null || vacation.ToDate is not null ? AutoReplyStatus.Scheduled : AutoReplyStatus.AlwaysEnabled;
        var message = vacation.HtmlBody ?? TextToHtml(vacation.TextBody);
        return new AutoReplySetting(status)
        {
            ExternalAudience = ExternalAudience.All,
            InternalReplyMessage = message,
            ExternalReplyMessage = message,
            ScheduledStartDateTime = vacation.FromDate is { } from ? DateTimeTimeZone.Utc(from) : null,
            ScheduledEndDateTime = vacation.ToDate is { } to ? DateTimeTimeZone.Utc(to) : null,
        };
    }

    private static JsonValue? UtcDate(DateTimeTimeZone? value) =>
        value is null
            ? null
            : JsonValue.Create(GraphTimeZone.ToInstant(value.DateTime, value.TimeZone).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    private static string TextToHtml(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : WebUtility.HtmlEncode(text).ReplaceLineEndings("<br>");
}
