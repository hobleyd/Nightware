using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Pages.Entities.Mailboxes;

/// <summary>The shared mailbox form as posted: aliases are comma- or line-separated text, as on the group form.</summary>
public sealed class MailboxInput
{
    public string Name { get; set; } = string.Empty;

    public string? LocalPart { get; set; }

    public string? Domain { get; set; }

    public string? Aliases { get; set; }

    public SharedMailboxForm ToForm() => new(Name)
    {
        LocalPart = LocalPart,
        Domain = Domain,
        Aliases = (Aliases ?? string.Empty).Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };

    /// <summary>The form filled with what <paramref name="mailbox"/> is now.</summary>
    public static MailboxInput From(SharedMailbox mailbox)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        return new MailboxInput
        {
            Name = mailbox.Name,
            LocalPart = mailbox.Address.LocalPart,
            Domain = mailbox.Address.Domain.Name,
            Aliases = string.Join(", ", mailbox.Aliases.Select(static a => a.Value)),
        };
    }
}
