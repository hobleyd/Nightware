using System.Diagnostics.CodeAnalysis;
using System.Text;

using Nightware.Domain.Kernel;

namespace Nightware.Domain.Administration;

/// <summary>
/// A group's stable handle within its entity (phase 9): lower-case letters, digits and single hyphens, starting with
/// a letter, at most 40 characters. Never changes after creation, so downstream names built from it survive a rename.
/// </summary>
public sealed record GroupKey
{
    public const int MaxLength = 40;

    private GroupKey(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out GroupKey? key)
    {
        key = null;
        string? trimmed = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxLength || !char.IsAsciiLetterLower(trimmed[0]) || trimmed[^1] == '-'
            || trimmed.Contains("--", StringComparison.Ordinal) || !trimmed.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
        {
            return false;
        }

        key = new GroupKey(trimmed);
        return true;
    }

    /// <summary>A key made from a display name ("Finance & Ops" → <c>finance-ops</c>); null when the name has no usable letters.</summary>
    public static GroupKey? Propose(string? name)
    {
        var slug = new StringBuilder();
        foreach (char c in (name ?? string.Empty).Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-' && (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)))
            {
                slug.Append('-');
            }
        }

        string proposed = slug.ToString();
        int start = proposed.TakeWhile(static c => !char.IsAsciiLetterLower(c)).Count();
        proposed = proposed[start..];
        proposed = proposed[..Math.Min(proposed.Length, MaxLength)].TrimEnd('-');
        return TryParse(proposed, out GroupKey? key) ? key : null;
    }

    /// <summary>
    /// The group's id on the groupware server, where group ids are global: the entity alias keeps two entities'
    /// <c>finance</c> groups apart.
    /// </summary>
    public string GroupwareId(string entityAlias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityAlias);
        return entityAlias.Trim().ToLowerInvariant() + "-" + Value;
    }

    public override string ToString() => Value;
}

/// <summary>Who may send to a group's address.</summary>
public enum SenderPolicy
{
    /// <summary>The group's members only (the default).</summary>
    Members,

    /// <summary>Anyone with an address on one of the entity's domains.</summary>
    Organization,

    /// <summary>Anyone, including the internet.</summary>
    Anyone,
}

/// <summary>
/// An outside address a list forwards to. More lenient than <see cref="MailAddress"/> (plus-addressing and other
/// local-part characters people use) but never anything that needs quoting in SMTP or Sieve.
/// </summary>
public sealed record RecipientAddress
{
    private RecipientAddress(string value) => Value = value;

    public string Value { get; }

    public static bool TryParse(string? value, [NotNullWhen(true)] out RecipientAddress? address)
    {
        address = null;
        string? trimmed = value?.Trim();
        int at = trimmed?.LastIndexOf('@') ?? -1;
        if (trimmed is null || at <= 0 || at > 64 || !MailDomain.TryParse(trimmed[(at + 1)..], out MailDomain? domain))
        {
            return false;
        }

        string local = trimmed[..at];
        if (local[0] == '.' || local[^1] == '.' || local.Contains("..", StringComparison.Ordinal)
            || !local.All(static c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-/=?^_`{|}~.".Contains(c, StringComparison.Ordinal)))
        {
            return false;
        }

        address = new RecipientAddress(local.ToLowerInvariant() + "@" + domain.Name);
        return true;
    }

    public override string ToString() => Value;
}

/// <summary>A group's mail facet: its address on the mail server, as a distribution list.</summary>
public sealed record GroupMail(MailAddress Address, SenderPolicy Senders)
{
    public IReadOnlyList<MailAddress> Aliases { get; init; } = [];

    /// <summary>Outside addresses the list also delivers to. Not members: they never reach the groupware server.</summary>
    public IReadOnlyList<RecipientAddress> ExternalRecipients { get; init; } = [];

    /// <summary>The address and its aliases.</summary>
    public IEnumerable<MailAddress> Addresses => Aliases.Prepend(Address);

    /// <summary>
    /// External recipients only on a list closed to outside senders (phase 9 G5): the mail server forwards without
    /// rewriting the envelope sender, so a copy forwarded outside must come from one of our own domains to pass the
    /// receiver's SPF and DMARC checks.
    /// </summary>
    public Result Validate() =>
        ExternalRecipients.Count > 0 && Senders == SenderPolicy.Anyone
            ? Error.Validation("A list with outside recipients cannot accept mail from anyone: choose members or the organisation as senders, or remove the outside recipients.", "senders")
            : Result.Success();
}

/// <summary>A group's files facet: a groupware group, and optionally a team folder only the group can see.</summary>
public sealed record GroupFiles(bool TeamFolder)
{
    /// <summary>The team folder's id on the groupware server, once created.</summary>
    public int? TeamFolderId { get; init; }
}

/// <summary>
/// A group of an entity's users (phase 9): kept in Keycloak (an organization group), mirrored to a mail server list
/// (<see cref="Mail"/>) and a groupware group (<see cref="Files"/>), either or both.
/// </summary>
public sealed record Group(string Id, string EntityId, GroupKey Key, string Name)
{
    public GroupMail? Mail { get; init; }

    public GroupFiles? Files { get; init; }
}

/// <summary>What the console asks for when a group is created.</summary>
public sealed record NewGroup(GroupKey Key, string Name, GroupMail? Mail, GroupFiles? Files);

/// <summary>
/// Who may send to one list, as the mail server enforces it: mail to <see cref="ListAddresses"/> is refused unless
/// the envelope sender is one of <see cref="AllowedSenders"/> or on one of <see cref="AllowedDomains"/>.
/// </summary>
public sealed record SenderRule(IReadOnlyList<MailAddress> ListAddresses, IReadOnlyList<string> AllowedSenders, IReadOnlyList<MailDomain> AllowedDomains)
{
    /// <summary>The rule for <paramref name="mail"/>, or null when anyone may send.</summary>
    public static SenderRule? For(GroupMail mail, IEnumerable<string> memberAddresses, IEnumerable<MailDomain> entityDomains)
    {
        ArgumentNullException.ThrowIfNull(mail);
        ArgumentNullException.ThrowIfNull(memberAddresses);
        ArgumentNullException.ThrowIfNull(entityDomains);
        return mail.Senders switch
        {
            SenderPolicy.Members => new SenderRule([.. mail.Addresses], [.. memberAddresses.Select(static a => a.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], []),
            SenderPolicy.Organization => new SenderRule([.. mail.Addresses], [], [.. entityDomains.Distinct().OrderBy(static d => d.Name, StringComparer.Ordinal)]),
            _ => null,
        };
    }
}
