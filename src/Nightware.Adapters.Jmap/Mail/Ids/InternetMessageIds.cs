using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Mail.Ids;

/// <summary>RFC 5322 Message-ID helpers. The façade mints the Message-ID of every draft it creates (ADR 0001 §1).</summary>
internal static class InternetMessageIds
{
    /// <summary>
    /// A new Message-ID, without angle brackets: <c>&lt;uuid&gt;@&lt;domain of the mailbox&gt;</c>.
    /// Falls back to <c>nightware.invalid</c> when the address has no usable domain.
    /// </summary>
    public static string Mint(MailboxRef mailbox) => Mint(mailbox, Guid.NewGuid());

    internal static string Mint(MailboxRef mailbox, Guid unique)
    {
        var address = mailbox.Address ?? string.Empty;
        var at = address.LastIndexOf('@');
        var domain = at >= 0 && at < address.Length - 1 ? address[(at + 1)..].Trim().ToLowerInvariant() : string.Empty;
        if (domain.Length == 0 || !MailItemId.IsValidMessageId(domain) || domain.Contains('@', StringComparison.Ordinal))
        {
            domain = "nightware.invalid";
        }

        return $"{unique:N}@{domain}";
    }

    /// <summary>Removes surrounding angle brackets and whitespace.</summary>
    public static string StripBrackets(string messageId)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        var trimmed = messageId.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '<' && trimmed[^1] == '>' ? trimmed[1..^1].Trim() : trimmed;
    }

    /// <summary>The header form, with angle brackets.</summary>
    public static string ToHeaderValue(string messageId) => $"<{StripBrackets(messageId)}>";
}
