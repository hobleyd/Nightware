using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;

using Nightware.Application.Common.Time;
using Nightware.Application.Presence;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Presence;

/// <summary>Reads the presence actions' bodies. Unknown properties are ignored; a known one with a bad value is a 400.</summary>
internal static partial class PresenceRequests
{
    /// <summary><c>setPresence</c> and <c>setUserPreferredPresence</c>: availability, activity, an ISO 8601 expirationDuration.</summary>
    public static Result<PresenceChange> ReadChange(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("A JSON body is required.");
        }

        try
        {
            var duration = Text(body, "expirationDuration");
            TimeSpan? expiration = null;
            if (!string.IsNullOrWhiteSpace(duration))
            {
                expiration = XmlConvert.ToTimeSpan(duration.Trim());
            }

            return new PresenceChange(Text(body, "availability"), Text(body, "activity"), expiration) { SessionId = Text(body, "sessionId") };
        }
        catch (FormatException)
        {
            return Error.Validation("expirationDuration must be an ISO 8601 duration, such as PT1H.", "expirationDuration");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return Error.Validation($"The request body is not valid: {e.Message}");
        }
    }

    /// <summary><c>clearPresence</c>'s sessionId (null when the body or the property is missing).</summary>
    public static Result<string?> ReadSessionId(JsonObject? body)
    {
        try
        {
            return body is null ? (string?)null : Text(body, "sessionId");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return Error.Validation($"The request body is not valid: {e.Message}");
        }
    }

    /// <summary><c>setStatusMessage</c>: <c>statusMessage.message</c> (text, or HTML reduced to text) and its <c>expiryDateTime</c>.</summary>
    public static Result<StatusMessageChange> ReadStatusMessage(JsonObject? body)
    {
        if (body?["statusMessage"] is not JsonObject statusMessage)
        {
            return Error.Validation("statusMessage is required.", "statusMessage");
        }

        try
        {
            var content = statusMessage["message"] is JsonObject message ? Text(message, "content") : null;
            if (content is not null && statusMessage["message"] is JsonObject typed
                && string.Equals(Text(typed, "contentType"), "html", StringComparison.OrdinalIgnoreCase))
            {
                content = WebUtility.HtmlDecode(Tags().Replace(content, " ")).Trim();
            }

            DateTimeOffset? expiry = null;
            if (statusMessage["expiryDateTime"] is JsonObject at && Text(at, "dateTime") is { } wallClock)
            {
                var parsed = DateTime.Parse(wallClock, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
                expiry = parsed.Kind == DateTimeKind.Utc ? new DateTimeOffset(parsed) : GraphTimeZone.ToInstant(parsed, Text(at, "timeZone"));
            }

            return new StatusMessageChange(content, expiry);
        }
        catch (FormatException)
        {
            return Error.Validation("statusMessage.expiryDateTime.dateTime is not a date and time.", "expiryDateTime");
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return Error.Validation($"The status message is not valid: {e.Message}");
        }
    }

    /// <summary><c>getPresencesByUserId</c>'s ids.</summary>
    public static Result<IReadOnlyList<string>> ReadIds(JsonObject? body)
    {
        if (body?["ids"] is not JsonArray ids)
        {
            return Error.Validation("ids is required.", "ids");
        }

        try
        {
            return ids.Select(static id => id?.GetValue<string>() ?? string.Empty).ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return Error.Validation("ids must be a list of user ids.", "ids");
        }
    }

    private static string? Text(JsonObject body, string name) =>
        body.TryGetPropertyValue(name, out var node) && node is not null ? node.GetValue<string>() : null;

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tags();
}
