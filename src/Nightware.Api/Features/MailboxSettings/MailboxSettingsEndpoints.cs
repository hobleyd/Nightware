using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.MailboxSettings.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.MailboxSettings;

namespace Nightware.Api.Features.MailboxSettings;

public static class MailboxSettingsEndpoints
{
    /// <summary>
    /// Maps <c>GET</c>/<c>PATCH {mailbox}/mailboxSettings</c> onto a Graph version group (<c>/v1.0</c> and
    /// <c>/beta</c>). PATCH changes <c>automaticRepliesSetting</c> and <c>timeZone</c>; GET also reads single settings and the supported time zones and languages.
    /// </summary>
    public static RouteGroupBuilder MapMailboxSettingsEndpoints(this RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/mailboxSettings", GetAsync);
            scope.MapPatch("/mailboxSettings", UpdateAsync);
            scope.MapGet("/mailboxSettings/{setting}", GetSettingAsync);
            scope.MapGet("/outlook/supportedTimeZones", (Mailbox mailbox, GetSupportedTimeZones useCase, CancellationToken cancellationToken) =>
                TimeZonesAsync(mailbox, null, useCase, cancellationToken));
            scope.MapGet("/outlook/supportedTimeZones(TimeZoneStandard={standard})", (Mailbox mailbox, string standard, GetSupportedTimeZones useCase, CancellationToken cancellationToken) =>
                TimeZonesAsync(mailbox, standard, useCase, cancellationToken));
            scope.MapGet("/outlook/supportedLanguages", LanguagesAsync);
        }
        return graph;
    }

    /// <summary>
    /// One setting (Graph's <c>GET mailboxSettings/{property}</c>): the complex ones as objects, the strings as
    /// <c>value</c>. <c>userPurpose</c> and <c>delegateMeetingMessageDeliveryOptions</c> have no source here (404).
    /// </summary>
    private static async Task<IResult> GetSettingAsync(Mailbox mailbox, string setting, GetMailboxSettings useCase, CancellationToken cancellationToken)
    {
        var name = SettingNames.FirstOrDefault(n => string.Equals(n, setting, StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return GraphResults.Error(Nightware.Domain.Kernel.Error.NotFound($"Mailbox setting '{setting}' is not available."));
        }

        var settings = await useCase.ExecuteAsync(mailbox.Ref, cancellationToken).ConfigureAwait(false);
        if (!settings.IsSuccess)
        {
            return GraphResults.Error(settings.Error);
        }

        var value = settings.Value;
        var context = $"{Context(mailbox)}/{name}";
        return name switch
        {
            "automaticRepliesSetting" => GraphResults.Property(MailboxSettingsMapper.ToDto(value).AutomaticRepliesSetting!, context),
            "language" => GraphResults.Property(MailboxSettingsMapper.ToDto(value.Language), context),
            "workingHours" => GraphResults.Property(MailboxSettingsMapper.ToDto(value.WorkingHours!), context),
            "timeZone" => GraphResults.PropertyValue(value.TimeZone, context),
            "dateFormat" => GraphResults.PropertyValue(value.DateFormat, context),
            _ => GraphResults.PropertyValue(value.TimeFormat, context),
        };
    }

    private static readonly string[] SettingNames = ["automaticRepliesSetting", "timeZone", "language", "workingHours", "dateFormat", "timeFormat"];

    private static async Task<IResult> TimeZonesAsync(Mailbox mailbox, string? standard, GetSupportedTimeZones useCase, CancellationToken cancellationToken)
    {
        var parsed = standard is null ? TimeZoneStandard.Windows : OutlookCatalog.ParseStandard(standard);
        if (!parsed.IsSuccess)
        {
            return GraphResults.Error(parsed.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, parsed.Value, cancellationToken).ConfigureAwait(false),
            zones => GraphResults.Collection(zones.Select(static z => (object)new TimeZoneInformationDto(z.Alias, z.DisplayName)), "Collection(microsoft.graph.timeZoneInformation)", Nightware.Application.Common.Query.ItemQuery.Default));
    }

    private static async Task<IResult> LanguagesAsync(Mailbox mailbox, GetSupportedLanguages useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, cancellationToken).ConfigureAwait(false),
            languages => GraphResults.Collection(languages.Select(static l => (object)new LocaleInfoDto(l.Locale, l.DisplayName)), "Collection(microsoft.graph.localeInfo)", Nightware.Application.Common.Query.ItemQuery.Default));

    private static string Context(Mailbox mailbox) => $"{mailbox.ContextSegment}/mailboxSettings";

    private static async Task<IResult> GetAsync(Mailbox mailbox, ODataQuery odata, GetMailboxSettings useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, cancellationToken).ConfigureAwait(false),
            settings => GraphResults.Entity(MailboxSettingsMapper.ToDto(settings), Context(mailbox), odata.Query.Select));

    private static async Task<IResult> UpdateAsync(Mailbox mailbox, JsonObject? body, UpdateMailboxSettings useCase, CancellationToken cancellationToken)
    {
        var patch = MailboxSettingsMapper.ReadPatch(body);
        if (!patch.IsSuccess)
        {
            return GraphResults.Error(patch.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, patch.Value, cancellationToken).ConfigureAwait(false),
            settings => GraphResults.Entity(MailboxSettingsMapper.ToDto(settings), Context(mailbox), MailboxSettingsMapper.Patched(patch.Value)));
    }
}
