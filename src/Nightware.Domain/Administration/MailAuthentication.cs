using Nightware.Domain.Kernel;

namespace Nightware.Domain.Administration;

/// <summary>
/// A domain's SPF record (RFC 7208): the TXT at the domain's apex that starts <c>v=spf1</c>. One line of
/// printable ASCII without quotes; the version tag is checked, the mechanisms are left to the operator.
/// </summary>
public sealed record SpfRecord
{
    private SpfRecord(string value) => Value = value;

    /// <summary>Only the domain's MX hosts may send its mail.</summary>
    public static SpfRecord Default { get; } = new("v=spf1 mx -all");

    public string Value { get; }

    public static Result<SpfRecord> Parse(string? value)
    {
        string? text = MailAuthenticationText.Clean(value, "SPF", "spf", out Error? error);
        if (text is null)
        {
            return error!;
        }

        return text.Equals("v=spf1", StringComparison.OrdinalIgnoreCase) || text.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase)
            ? new SpfRecord(text)
            : Error.Validation("An SPF record starts with \"v=spf1\", for example \"v=spf1 mx -all\".", "spf");
    }

    public override string ToString() => Value;
}

/// <summary>
/// A domain's DMARC record (RFC 7489): the TXT at <c>_dmarc.&lt;domain&gt;</c>. Starts <c>v=DMARC1</c>, has a
/// <c>p=</c> policy, and sends reports only to <c>mailto:</c> addresses.
/// </summary>
public sealed record DmarcRecord
{
    private static readonly string[] Policies = ["none", "quarantine", "reject"];

    private DmarcRecord(string value, string policy)
    {
        Value = value;
        Policy = policy;
    }

    public string Value { get; }

    /// <summary>What receivers do with mail that fails: <c>none</c>, <c>quarantine</c> or <c>reject</c>.</summary>
    public string Policy { get; }

    /// <summary>Failing mail goes to spam; aggregate reports go to the domain's postmaster.</summary>
    public static DmarcRecord Default(MailDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return new DmarcRecord($"v=DMARC1; p=quarantine; rua=mailto:postmaster@{domain.Name}", "quarantine");
    }

    public static Result<DmarcRecord> Parse(string? value)
    {
        string? text = MailAuthenticationText.Clean(value, "DMARC", "dmarc", out Error? error);
        if (text is null)
        {
            return error!;
        }

        string[] tags = [.. text.Split(';').Select(static t => t.Trim())];
        if (!tags[0].Equals("v=DMARC1", StringComparison.Ordinal))
        {
            return Invalid("A DMARC record starts with \"v=DMARC1;\", for example \"v=DMARC1; p=quarantine\".");
        }

        string? policy = null;
        for (int i = 1; i < tags.Length; i++)
        {
            if (tags[i].Length == 0)
            {
                continue;
            }

            int equals = tags[i].IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return Invalid($"\"{tags[i]}\" is not a DMARC tag; tags look like \"p=quarantine\".");
            }

            string name = tags[i][..equals].Trim().ToLowerInvariant();
            string tagValue = tags[i][(equals + 1)..].Trim();
            switch (name)
            {
                case "p" when i != 1:
                    return Invalid("The p= policy comes straight after v=DMARC1.");
                case "p" or "sp" when !Policies.Contains(tagValue.ToLowerInvariant()):
                    return Invalid($"{name}= is one of none, quarantine or reject.");
                case "p":
                    policy = tagValue.ToLowerInvariant();
                    break;
                case "rua" or "ruf" when tagValue.Split(',').Any(static uri => !uri.Trim().StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)):
                    return Invalid($"{name}= takes mailto: addresses, for example \"{name}=mailto:postmaster@example.com\".");
                case "pct" when !int.TryParse(tagValue, out int pct) || pct is < 0 or > 100:
                    return Invalid("pct= is a whole number from 0 to 100.");
            }
        }

        return policy is null
            ? Invalid("A DMARC record needs a policy: p=none, p=quarantine or p=reject.")
            : new DmarcRecord(text, policy);
    }

    public override string ToString() => Value;

    private static Error Invalid(string message) => Error.Validation(message, "dmarc");
}

/// <summary>
/// The SPF and DMARC records an entity chose for one of its domains. A null record means "what the mail server
/// generates", which is the default.
/// </summary>
public sealed record DomainMailPolicy(SpfRecord? Spf, DmarcRecord? Dmarc)
{
    public static DomainMailPolicy MailServerDefault { get; } = new(null, null);

    public bool IsDefault => Spf is null && Dmarc is null;

    /// <summary>
    /// The mail server's records for <paramref name="domain"/> with this policy's SPF and DMARC in place of the
    /// server's own (never beside them: two SPF records at one name fail evaluation). Where the server publishes
    /// no SPF or DMARC record, <see cref="SpfRecord.Default"/> or <see cref="DmarcRecord.Default"/> is added. An
    /// empty list stays empty: until the mail server serves the domain there is nothing to publish.
    /// </summary>
    public IReadOnlyList<DnsRecord> ApplyTo(MailDomain domain, IReadOnlyList<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            return records;
        }

        string dmarcName = "_dmarc." + domain.Name;
        List<DnsRecord> result = [.. records];
        Replace(result, domain.Name, "v=spf1", Spf?.Value ?? (Find(records, domain.Name, "v=spf1") is null ? SpfRecord.Default.Value : null));
        Replace(result, dmarcName, "v=DMARC1", Dmarc?.Value ?? (Find(records, dmarcName, "v=DMARC1") is null ? DmarcRecord.Default(domain).Value : null));
        return result;
    }

    /// <summary>The domain's SPF value in <paramref name="records"/> (as <see cref="ApplyTo"/> returns them), if any.</summary>
    public static string? SpfIn(MailDomain domain, IReadOnlyList<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return Find(records, domain.Name, "v=spf1")?.Value;
    }

    /// <summary>The domain's DMARC value in <paramref name="records"/> (as <see cref="ApplyTo"/> returns them), if any.</summary>
    public static string? DmarcIn(MailDomain domain, IReadOnlyList<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return Find(records, "_dmarc." + domain.Name, "v=DMARC1")?.Value;
    }

    private static DnsRecord? Find(IReadOnlyList<DnsRecord> records, string name, string version) =>
        records.FirstOrDefault(r => Is(r, name, version));

    /// <summary>Puts <paramref name="value"/> where the first matching record was (keeping its TTL), dropping any others.</summary>
    private static void Replace(List<DnsRecord> records, string name, string version, string? value)
    {
        if (value is null)
        {
            return;
        }

        int index = records.FindIndex(r => Is(r, name, version));
        DnsRecord replacement = new(index >= 0 ? records[index].Name : name, "TXT", index >= 0 ? records[index].Ttl : null, value);
        records.RemoveAll(r => Is(r, name, version));
        records.Insert(index >= 0 ? index : records.Count, replacement);
    }

    private static bool Is(DnsRecord record, string name, string version) =>
        record.Type.Equals("TXT", StringComparison.OrdinalIgnoreCase)
        && record.Name.TrimEnd('.').Equals(name, StringComparison.OrdinalIgnoreCase)
        && record.Value.TrimStart().StartsWith(version, StringComparison.OrdinalIgnoreCase);
}

internal static class MailAuthenticationText
{
    /// <summary>One TXT string, and what a Keycloak attribute value is taken to hold (ADR 0002, unverified).</summary>
    public const int MaxLength = 255;

    /// <summary>Trimmed text, or null with the reason: empty, too long, or not one line of printable ASCII without quotes.</summary>
    public static string? Clean(string? value, string label, string target, out Error? error)
    {
        error = null;
        string text = value?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = Error.Validation($"Enter a {label} record.", target);
            return null;
        }

        if (text.Length > MaxLength)
        {
            error = Error.Validation($"A {label} record is at most {MaxLength} characters.", target);
            return null;
        }

        if (!text.All(static c => c is >= ' ' and <= '~' && c is not '"' and not '\\'))
        {
            error = Error.Validation($"A {label} record is one line of plain ASCII text without quotes or backslashes.", target);
            return null;
        }

        return text;
    }
}
