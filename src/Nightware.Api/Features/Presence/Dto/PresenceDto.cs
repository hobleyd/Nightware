using System.Text.Json.Serialization;

using Nightware.Domain.Presence;

namespace Nightware.Api.Features.Presence.Dto;

/// <summary>
/// Graph <c>presence</c>. <c>sequenceNumber</c> and <c>workLocation</c> have no Nextcloud counterpart and are left out,
/// as is <c>statusMessage.publishedDateTime</c> (ADR 0009 D6).
/// </summary>
public sealed record PresenceDto
{
    public required string Id { get; init; }

    public required string Availability { get; init; }

    public required string Activity { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PresenceStatusMessageDto? StatusMessage { get; init; }

    public required OutOfOfficeSettingsDto OutOfOfficeSettings { get; init; }

    public static PresenceDto From(UserPresence presence)
    {
        ArgumentNullException.ThrowIfNull(presence);
        return new PresenceDto
        {
            Id = presence.Id,
            Availability = presence.Availability,
            Activity = presence.Activity,
            StatusMessage = presence.StatusMessage is { } message
                ? new PresenceStatusMessageDto
                {
                    Message = new PresenceItemBodyDto { Content = message.Content, ContentType = "text" },
                    ExpiryDateTime = message.ExpiryDateTime is { } expiry ? new PresenceDateTimeDto { DateTime = expiry.UtcDateTime, TimeZone = "UTC" } : null,
                }
                : null,
            OutOfOfficeSettings = new OutOfOfficeSettingsDto { Message = presence.OutOfOffice.Message, IsOutOfOffice = presence.OutOfOffice.IsOutOfOffice },
        };
    }
}

/// <summary>Graph <c>presenceStatusMessage</c>; another user's carries no expiry, as Graph's doesn't.</summary>
public sealed record PresenceStatusMessageDto
{
    public required PresenceItemBodyDto Message { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PresenceDateTimeDto? ExpiryDateTime { get; init; }
}

/// <summary>Graph <c>itemBody</c>.</summary>
public sealed record PresenceItemBodyDto
{
    public required string Content { get; init; }

    public required string ContentType { get; init; }
}

/// <summary>Graph <c>dateTimeTimeZone</c>, always in UTC.</summary>
public sealed record PresenceDateTimeDto
{
    public DateTime DateTime { get; init; }

    public required string TimeZone { get; init; }
}

/// <summary>Graph <c>outOfOfficeSettings</c>.</summary>
public sealed record OutOfOfficeSettingsDto
{
    public string? Message { get; init; }

    public bool IsOutOfOffice { get; init; }
}
