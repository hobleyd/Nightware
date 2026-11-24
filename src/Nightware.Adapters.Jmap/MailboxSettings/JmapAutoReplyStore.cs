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
/// <para>VacationResponse has one message and replies to every sender, so a setting with separate internal and
/// external messages, or an <c>externalAudience</c> other than <c>all</c>, goes to Nightware's Sieve script instead
/// (<see cref="SieveScriptWriter"/>), which keeps both and tests the sender's domain against the entity's. Once that
/// script exists (it also holds Inbox rules; one script is active at a time, ADR 0006 D5) every reply is read and
/// written there. A plain reply to everyone without the script stays a VacationResponse; its <b>external</b>
/// message is stored (the internal one only when there is no external one), since outsiders are whom it answers.
/// Only while the user's own filter is active does a split setting fall back to VacationResponse, as before.</para>
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

    public async Task<Result<AutoReplySetting>> SetAsync(
        MailboxRef mailbox, AutoReplySetting setting, IReadOnlyList<string> internalDomains, IReadOnlyList<string>? knownSenders, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(internalDomains);
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

        // Our script holds the reply once it exists, and is created for one VacationResponse can't express
        // (separate internal and external messages, or not everyone outside) unless the user's own filter is active.
        var kept = Normalise(setting);
        if (filter.Value is { } script && (script.Exists || (SieveScriptWriter.Splits(kept) && script.ForeignActiveScript is null)))
        {
            // Writing here also makes the script active again if vacation was switched on elsewhere.
            var state = script.State with { AutoReply = kept, InternalDomains = internalDomains, KnownSenders = knownSenders ?? script.State.KnownSenders };
            var saved = await sieve.SaveAsync(account.Value, script, state, cancellationToken).ConfigureAwait(false);
            return saved.IsSuccess ? kept : saved.Error;
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

    /// <summary>
    /// What a setting reads back as from Nightware's script: both messages (a blank one takes the other's text) and
    /// the audience, with the schedule in UTC. <c>scheduled</c> without dates is <c>alwaysEnabled</c>.
    /// </summary>
    internal static AutoReplySetting Normalise(AutoReplySetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (setting.Status == AutoReplyStatus.Disabled)
        {
            return new AutoReplySetting(AutoReplyStatus.Disabled);
        }

        var dated = setting.Status != AutoReplyStatus.AlwaysEnabled && (setting.ScheduledStartDateTime is not null || setting.ScheduledEndDateTime is not null);
        return new AutoReplySetting(dated ? AutoReplyStatus.Scheduled : AutoReplyStatus.AlwaysEnabled)
        {
            ExternalAudience = setting.ExternalAudience,
            InternalReplyMessage = SieveScriptWriter.InternalMessage(setting),
            ExternalReplyMessage = SieveScriptWriter.ExternalMessage(setting),
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
