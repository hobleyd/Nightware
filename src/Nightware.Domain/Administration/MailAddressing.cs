using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Nightware.Domain.Administration;

/// <summary>
/// A mail domain an entity owns, normalised to lower-case ASCII (IDN names arrive as punycode).
/// Only letter-digit-hyphen labels are accepted, so a name can be passed to any backend unescaped.
/// </summary>
public sealed record MailDomain
{
    private MailDomain(string name) => Name = name;

    public string Name { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out MailDomain? domain)
    {
        domain = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string name;
        try
        {
            name = new IdnMapping().GetAscii(value.Trim().TrimEnd('.')).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        string[] labels = name.Split('.');
        if (name.Length > 253 || labels.Length < 2 || !labels.All(IsLabel) || labels[^1].All(char.IsAsciiDigit))
        {
            return false;
        }

        domain = new MailDomain(name);
        return true;
    }

    public override string ToString() => Name;

    private static bool IsLabel(string label) =>
        label.Length is > 0 and <= 63
        && label[0] != '-' && label[^1] != '-'
        && label.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}

/// <summary>
/// An address the console may assign: a conservative local part (letters, digits, <c>.</c>, <c>_</c>,
/// <c>-</c>; no leading, trailing or doubled dot) at a <see cref="MailDomain"/>. Stricter than RFC 5321 on
/// purpose: the local part doubles as the Stalwart account name and the proposed Keycloak username.
/// </summary>
public sealed record MailAddress
{
    private MailAddress(string localPart, MailDomain domain)
    {
        LocalPart = localPart;
        Domain = domain;
    }

    public string LocalPart { get; }

    public MailDomain Domain { get; }

    public string Value => LocalPart + "@" + Domain.Name;

    public static bool TryParse(string? value, [NotNullWhen(true)] out MailAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        int at = trimmed.LastIndexOf('@');
        if (at <= 0 || !TryParseLocalPart(trimmed[..at], out string? local) || !MailDomain.TryParse(trimmed[(at + 1)..], out MailDomain? domain))
        {
            return false;
        }

        address = new MailAddress(local, domain);
        return true;
    }

    public static bool TryCreate(string? localPart, MailDomain domain, [NotNullWhen(true)] out MailAddress? address)
    {
        ArgumentNullException.ThrowIfNull(domain);
        address = TryParseLocalPart(localPart, out string? local) ? new MailAddress(local, domain) : null;
        return address is not null;
    }

    /// <summary>Case-insensitive comparison with a raw address from a backend.</summary>
    public bool Matches(string? other) => string.Equals(Value, other?.Trim(), StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Value;

    private static bool TryParseLocalPart(string? value, [NotNullWhen(true)] out string? local)
    {
        local = value?.Trim().ToLowerInvariant();
        if (local is null || local.Length is 0 or > 64 || local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal))
        {
            local = null;
            return false;
        }

        if (!local.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-'))
        {
            local = null;
            return false;
        }

        return true;
    }
}
