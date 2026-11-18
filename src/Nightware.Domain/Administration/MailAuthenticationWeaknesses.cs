using System.Globalization;

namespace Nightware.Domain.Administration;

/// <summary>How much a weakness in a published SPF or DMARC record matters.</summary>
public enum WeaknessSeverity
{
    /// <summary>Lets some forged or failing mail through, or hides failures; worth tightening.</summary>
    Weak,

    /// <summary>Lets anyone send as the domain.</summary>
    Unsafe,
}

/// <summary>One thing wrong with an SPF or DMARC record, in plain words.</summary>
public sealed record MailAuthenticationWeakness(WeaknessSeverity Severity, string Message);

/// <summary>
/// What a published SPF or DMARC record leaves open, judged against the strict records the console suggests
/// (<see cref="SecureMailDefaults"/>): only listed senders, everyone else refused, failing mail rejected, and
/// reports sent somewhere. Reads the record text only; it does not resolve includes or count lookups.
/// </summary>
public static class MailAuthenticationWeaknesses
{
    /// <summary>The weaknesses of <paramref name="record"/> when it is an SPF or DMARC record; none for any other.</summary>
    public static IReadOnlyList<MailAuthenticationWeakness> Of(DnsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string value = DnsPublicationPlanner.Unquote(record.Value.Trim());
        if (!record.Type.Trim().Equals("TXT", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (value.Equals("v=spf1", StringComparison.OrdinalIgnoreCase) || value.StartsWith("v=spf1 ", StringComparison.OrdinalIgnoreCase))
        {
            return Spf(value);
        }

        return value.StartsWith("v=DMARC1", StringComparison.OrdinalIgnoreCase) ? Dmarc(value) : [];
    }

    public static IReadOnlyList<MailAuthenticationWeakness> Spf(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string[] terms = [.. value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(static t => t.ToLowerInvariant())];
        var found = new List<MailAuthenticationWeakness>();
        string? all = terms.LastOrDefault(static t => t is "all" or "+all" or "-all" or "~all" or "?all");
        switch (all)
        {
            case "all" or "+all":
                found.Add(new(WeaknessSeverity.Unsafe, "+all lets any server send mail as the domain."));
                break;
            case "?all":
                found.Add(new(WeaknessSeverity.Weak, "?all says nothing about unlisted senders, so their mail is accepted."));
                break;
            case "~all":
                found.Add(new(WeaknessSeverity.Weak, "~all only soft-fails unlisted senders; most receivers still accept their mail. -all refuses it."));
                break;
            case null when !terms.Any(static t => t.StartsWith("redirect=", StringComparison.Ordinal)):
                found.Add(new(WeaknessSeverity.Weak, "No all at the end, so unlisted senders are not refused. End it with -all."));
                break;
        }

        if (terms.Any(static t => t.TrimStart('+', '-', '~', '?').StartsWith("ptr", StringComparison.Ordinal)))
        {
            found.Add(new(WeaknessSeverity.Weak, "ptr is deprecated and unreliable; list the senders by address or include: instead."));
        }

        return found;
    }

    public static IReadOnlyList<MailAuthenticationWeakness> Dmarc(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string tag in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = tag.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                tags[tag[..equals].Trim()] = tag[(equals + 1)..].Trim();
            }
        }

        var found = new List<MailAuthenticationWeakness>();
        if (tags.GetValueOrDefault("p")?.Equals("none", StringComparison.OrdinalIgnoreCase) == true)
        {
            found.Add(new(WeaknessSeverity.Weak, "p=none only reports mail that fails; it is still delivered. p=reject refuses it."));
        }

        if (tags.GetValueOrDefault("sp")?.Equals("none", StringComparison.OrdinalIgnoreCase) == true)
        {
            found.Add(new(WeaknessSeverity.Weak, "sp=none lets failing mail claiming a subdomain through."));
        }

        if (tags.TryGetValue("pct", out string? pct) && int.TryParse(pct, NumberStyles.None, CultureInfo.InvariantCulture, out int percent) && percent < 100)
        {
            found.Add(new(WeaknessSeverity.Weak, $"pct={percent} applies the policy to only {percent}% of failing mail."));
        }

        if (!tags.ContainsKey("rua"))
        {
            found.Add(new(WeaknessSeverity.Weak, "No rua= address, so nobody receives reports of mail that fails."));
        }

        return found;
    }
}
