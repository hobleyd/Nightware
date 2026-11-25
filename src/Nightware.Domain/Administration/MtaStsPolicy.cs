namespace Nightware.Domain.Administration;

/// <summary>
/// The mail server's one MTA-STS policy (RFC 8461), served for every domain at
/// <c>https://mta-sts.&lt;domain&gt;/.well-known/mta-sts.txt</c>. Its <c>mx:</c> lines are <see cref="MxHosts"/>, or, while
/// that override is empty, <see cref="DefaultHosts"/> (the mail server's own mail exchangers). A sender that applies
/// the policy refuses (under <c>enforce</c>) or reports (under <c>testing</c>) delivery to an MX the policy does not list.
/// </summary>
/// <param name="Mode"><c>enforce</c>, <c>testing</c> or <c>disable</c> (no policy is served).</param>
/// <param name="MxHosts">The hosts set on the policy itself; empty when none are, and the defaults apply.</param>
/// <param name="DefaultHosts">The mail server's mail exchangers, the policy's hosts while <see cref="MxHosts"/> is empty.</param>
public sealed record MtaStsPolicy(string Mode, IReadOnlyList<string> MxHosts, IReadOnlyList<string> DefaultHosts)
{
    /// <summary>Senders get no policy at all: nothing to add a host to.</summary>
    public bool IsDisabled => string.Equals(Mode, "disable", StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>mx:</c> lines the policy is served with.</summary>
    public IReadOnlyList<string> Hosts => MxHosts.Count > 0 ? MxHosts : DefaultHosts;

    /// <summary>
    /// Whether the policy allows delivery to <paramref name="host"/>: a listed name, or a <c>*.</c> pattern matching
    /// exactly one more label at its left (RFC 8461 §4.1). Case and a trailing dot are ignored.
    /// </summary>
    public bool Covers(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        string name = Normalize(host);
        return Hosts.Select(Normalize).Any(pattern => Matches(pattern, name));
    }

    private static bool Matches(string pattern, string name)
    {
        if (!pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return string.Equals(name, pattern, StringComparison.Ordinal);
        }

        string suffix = pattern[1..];
        if (!name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        string label = name[..^suffix.Length];
        return label.Length > 0 && !label.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>The hosts of <paramref name="mxHosts"/> the policy does not cover, each once, in their order.</summary>
    public IReadOnlyList<string> Missing(IEnumerable<string> mxHosts)
    {
        ArgumentNullException.ThrowIfNull(mxHosts);
        return [.. mxHosts.Select(Normalize).Where(static h => h.Length > 0).Distinct(StringComparer.Ordinal).Where(h => !Covers(h))];
    }

    /// <summary>
    /// The policy's own host list with <paramref name="hosts"/> added. While it has none, it starts from the defaults,
    /// so the domains they cover stay covered.
    /// </summary>
    public IReadOnlyList<string> Adding(IEnumerable<string> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        return [.. Hosts.Select(Normalize).Concat(hosts.Select(Normalize)).Where(static h => h.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The MX hosts of a domain's records (their exchange names, without preference), in order.</summary>
    public static IReadOnlyList<string> MxHostsIn(IEnumerable<DnsRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        return [.. records.Where(static r => string.Equals(r.Type, "MX", StringComparison.OrdinalIgnoreCase))
            .Select(static r => r.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries) switch
            {
                [_, var exchange, ..] => exchange,
                [var exchange] => exchange,
                _ => string.Empty,
            })
            .Select(Normalize).Where(static h => h.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    private static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();
}
