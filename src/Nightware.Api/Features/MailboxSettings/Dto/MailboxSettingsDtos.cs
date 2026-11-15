using System.Text.Json.Serialization;

using Nightware.Api.Features.Mail.Dto;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Api.Features.MailboxSettings.Dto;

/// <summary>Graph <c>mailboxSettings</c>, restricted to what NightMail reads and writes.</summary>
public sealed record MailboxSettingsDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AutomaticRepliesSettingDto? AutomaticRepliesSetting { get; init; }

    /// <summary>A Windows time-zone id, as Graph returns it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimeZone { get; init; }
}

/// <summary>Graph <c>automaticRepliesSetting</c>.</summary>
public sealed record AutomaticRepliesSettingDto
{
    public AutoReplyStatus Status { get; init; }

    public ExternalAudience ExternalAudience { get; init; }

    public string? InternalReplyMessage { get; init; }

    public string? ExternalReplyMessage { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeTimeZoneDto? ScheduledStartDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeTimeZoneDto? ScheduledEndDateTime { get; init; }
}
