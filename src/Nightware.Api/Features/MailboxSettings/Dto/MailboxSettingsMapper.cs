using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Graph.Json;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Api.Features.MailboxSettings.Dto;

/// <summary>Domain ↔ Graph <c>mailboxSettings</c>.</summary>
public static class MailboxSettingsMapper
{
    public static MailboxSettingsDto ToDto(MailboxSettingsSnapshot settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var replies = settings.AutomaticReplies;
        return new MailboxSettingsDto
        {
            TimeZone = settings.TimeZone,
            AutomaticRepliesSetting = new AutomaticRepliesSettingDto
            {
                Status = replies.Status,
                ExternalAudience = replies.ExternalAudience,
                InternalReplyMessage = replies.InternalReplyMessage ?? string.Empty,
                ExternalReplyMessage = replies.ExternalReplyMessage ?? string.Empty,
                ScheduledStartDateTime = ToDto(replies.ScheduledStartDateTime),
                ScheduledEndDateTime = ToDto(replies.ScheduledEndDateTime),
            },
        };
    }

    /// <summary>
    /// A PATCH body. <c>automaticRepliesSetting</c> replaces the whole setting (Graph replaces complex
    /// properties, and NightMail sends it whole); properties NightMail Server does not store
    /// (<c>language</c>, <c>workingHours</c>, …) are refused rather than silently dropped.
    /// </summary>
    public static Result<MailboxSettingsPatch> ReadPatch(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("A JSON body is required.");
        }

        AutoReplySetting? replies = null;
        string? zone = null;
        foreach (var (key, value) in body)
        {
            if (string.Equals(key, "automaticRepliesSetting", StringComparison.OrdinalIgnoreCase))
            {
                var read = ReadReplies(value);
                if (!read.IsSuccess)
                {
                    return read.Error;
                }

                replies = read.Value;
            }
            else if (string.Equals(key, "timeZone", StringComparison.OrdinalIgnoreCase))
            {
                zone = value is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                    ? text
                    : null;
                if (zone is null)
                {
                    return Error.Validation("timeZone must be a time-zone name.", "timeZone");
                }
            }
            else if (!key.StartsWith('@'))
            {
                return Error.Validation($"Setting '{key}' is not supported: only automaticRepliesSetting and timeZone can be changed.", key);
            }
        }

        return new MailboxSettingsPatch { AutomaticReplies = replies, TimeZone = zone };
    }

    /// <summary>The top-level properties a PATCH named, so the response echoes just those (as Graph does).</summary>
    public static IReadOnlySet<string> Patched(MailboxSettingsPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (patch.AutomaticReplies is not null)
        {
            names.Add("automaticRepliesSetting");
        }

        if (patch.TimeZone is not null)
        {
            names.Add("timeZone");
        }

        return names;
    }

    private static Result<AutoReplySetting> ReadReplies(JsonNode? node)
    {
        if (node is not JsonObject)
        {
            return Error.Validation("automaticRepliesSetting must be an object.", "automaticRepliesSetting");
        }

        AutomaticRepliesSettingDto? dto;
        try
        {
            dto = node.Deserialize<AutomaticRepliesSettingDto>(GraphJson.Options);
        }
        catch (JsonException e)
        {
            return Error.Validation($"automaticRepliesSetting is not valid: {e.Message}", "automaticRepliesSetting");
        }

        if (dto is null)
        {
            return Error.Validation("automaticRepliesSetting must be an object.", "automaticRepliesSetting");
        }

        return new AutoReplySetting(dto.Status)
        {
            ExternalAudience = dto.ExternalAudience,
            InternalReplyMessage = dto.InternalReplyMessage,
            ExternalReplyMessage = dto.ExternalReplyMessage,
            ScheduledStartDateTime = FromDto(dto.ScheduledStartDateTime),
            ScheduledEndDateTime = FromDto(dto.ScheduledEndDateTime),
        };
    }

    private static DateTimeTimeZoneDto? ToDto(DateTimeTimeZone? value) =>
        value is null ? null : new DateTimeTimeZoneDto { DateTime = value.DateTime, TimeZone = value.TimeZone };

    // Graph's "unset" schedule is 0001-01-01; it carries no instant.
    private static DateTimeTimeZone? FromDto(DateTimeTimeZoneDto? value) =>
        value is null || value.DateTime.Year < 1900
            ? null
            : new DateTimeTimeZone(value.DateTime, string.IsNullOrWhiteSpace(value.TimeZone) ? "UTC" : value.TimeZone);
}
