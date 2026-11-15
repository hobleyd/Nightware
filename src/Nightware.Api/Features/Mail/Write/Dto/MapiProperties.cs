using System.Globalization;
using Nightware.Api.Features.Mail.Dto;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Mail.Write.Dto;

/// <summary>
/// The MAPI properties NightMail's migration writes through <c>singleValueExtendedProperties</c>
/// (endpoints.md, cross-cutting requirement 10). Graph would otherwise date a migrated message "now"
/// and create it as an unsent draft.
/// </summary>
public static class MapiProperties
{
    /// <summary><c>PR_MESSAGE_DELIVERY_TIME</c>: when the message was received.</summary>
    public const int DeliveryTime = 0x0E06;

    /// <summary><c>PR_CLIENT_SUBMIT_TIME</c>: when it was sent (the <c>Date</c> header).</summary>
    public const int ClientSubmitTime = 0x0039;

    /// <summary><c>PR_MESSAGE_FLAGS</c>.</summary>
    public const int MessageFlags = 0x0E07;

    private const int MsgFlagRead = 0x1;
    private const int MsgFlagUnsent = 0x8;

    /// <summary>
    /// How the message lands. Without <c>PR_MESSAGE_FLAGS</c> Graph creates a draft (MSGFLAG_UNSENT), read
    /// as <c>isRead</c> says; with it, bit 0x1 is read and 0x8 unsent. Other properties are not stored.
    /// </summary>
    public static Result<ImportOptions> ToImportOptions(MessageDto message)
    {
        ArgumentNullException.ThrowIfNull(message);
        DateTimeOffset? received = null;
        DateTimeOffset? sent = null;
        int? flags = null;
        foreach (var property in message.SingleValueExtendedProperties ?? [])
        {
            switch (Tag(property.Id))
            {
                case DeliveryTime:
                    if (!TryTime(property.Value, out var deliveredAt))
                    {
                        return Invalid(property);
                    }

                    received = deliveredAt;
                    break;
                case ClientSubmitTime:
                    if (!TryTime(property.Value, out var submittedAt))
                    {
                        return Invalid(property);
                    }

                    sent = submittedAt;
                    break;
                case MessageFlags:
                    if (!int.TryParse(property.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        return Invalid(property);
                    }

                    flags = value;
                    break;
            }
        }

        return flags is { } f
            ? new ImportOptions((f & MsgFlagRead) != 0, received, sent, IsDraft: (f & MsgFlagUnsent) != 0)
            : new ImportOptions(message.IsRead == true, received, sent, IsDraft: true);
    }

    /// <summary>The property tag of a <c>"&lt;Type&gt; 0x&lt;tag&gt;"</c> id, or null for any other form (named properties).</summary>
    public static int? Tag(string? id)
    {
        var parts = (id ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is [_, var tag]
            && tag.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(tag.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
    }

    private static bool TryTime(string? value, out DateTimeOffset time) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time);

    private static Error Invalid(SingleValueExtendedPropertyDto property) =>
        Error.Validation($"'{property.Value}' is not a valid value for {property.Id}.", "singleValueExtendedProperties");
}
