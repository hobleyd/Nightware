using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Nightware.Domain.Kernel;

namespace Nightware.Domain.Administration;

/// <summary>
/// The credential for an entity's DNS host: an API token; for <see cref="DnsProvider.Route53"/> an AWS access key as
/// <c>KEYID:SECRET</c>; for <see cref="DnsProvider.AzureDns"/> a service principal as
/// <c>TENANT:CLIENT:SUBSCRIPTION:RESOURCEGROUP:SECRET</c>; for <see cref="DnsProvider.GoogleCloud"/> a service account's key, kept
/// as JSON with only <c>project_id</c>, <c>client_email</c>, <c>private_key_id</c> and <c>private_key</c>. Held only
/// for the length of a request: the console stores it sealed and never shows it back. <see cref="ToString"/> redacts.
/// </summary>
public sealed record DnsProviderToken
{
    /// <summary>
    /// Longest single-word token or AWS key accepted. Cloudflare's tokens are 40 characters, DigitalOcean's 71, an AWS
    /// key pair 61: this bound keeps their sealed form in one 255-character Keycloak attribute value, as it always was.
    /// </summary>
    public const int MaxLength = 79;

    /// <summary>Longest Azure service principal or Google key accepted; stored sealed across several attribute values.</summary>
    public const int MaxCredentialLength = 4096;

    private DnsProviderToken(string value) => Value = value;

    public string Value { get; }

    public static Result<DnsProviderToken> Parse(DnsProvider provider, string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        switch (provider)
        {
            case DnsProvider.Route53:
                return ParseAwsKey(text);
            case DnsProvider.AzureDns:
                return ParseAzurePrincipal(text);
            case DnsProvider.GoogleCloud:
                return ParseGoogleKey(text);
        }

        if (text.Length == 0)
        {
            return Error.Validation($"Paste the {provider.Name()} API token.", "dnsToken");
        }

        if (text.Length > MaxLength || !text.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return Error.Validation($"That is not a {provider.Name()} API token: one is a single word of letters, digits, '-' and '_'.", "dnsToken");
        }

        return new DnsProviderToken(text);
    }

    /// <summary>An AWS access key ID (upper-case letters and digits) and its secret (base64), joined by a colon.</summary>
    private static Result<DnsProviderToken> ParseAwsKey(string text)
    {
        if (text.Length == 0)
        {
            return Error.Validation("Enter the AWS access key ID and secret access key as KEYID:SECRET.", "dnsToken");
        }

        string[] parts = text.Split(':');
        bool valid = text.Length <= MaxLength
            && parts.Length == 2
            && parts[0].Length >= 16 && parts[0].All(static c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))
            && parts[1].Length >= 16 && parts[1].All(static c => char.IsAsciiLetterOrDigit(c) || c is '/' or '+' or '=');
        return valid
            ? new DnsProviderToken(text)
            : Error.Validation("That is not an AWS access key: enter it as KEYID:SECRET, the access key ID (AKIA…), a colon, then the secret access key.", "dnsToken");
    }

    /// <summary>
    /// A directory (tenant) ID or name, an application (client) ID, a subscription ID, the resource group holding the
    /// zones, and a client secret. Knowing the group, the console reads a zone directly, so the principal needs no
    /// more than DNS Zone Contributor on the zone itself.
    /// </summary>
    private static Result<DnsProviderToken> ParseAzurePrincipal(string text)
    {
        string[] parts = [.. text.Split(':', 5).Select(static p => p.Trim())];
        if (parts.Length != 5 || parts.Any(static p => p.Length == 0))
        {
            return Error.Validation("Enter the directory (tenant) ID, application (client) ID, subscription ID, resource group and client secret.", "dnsToken");
        }

        (string tenant, string client, string subscription, string group, string secret) = (parts[0], parts[1], parts[2], parts[3], parts[4]);
        bool valid = text.Length <= MaxCredentialLength
            && (Guid.TryParse(tenant, out _) || tenant.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            && Guid.TryParse(client, out _)
            && Guid.TryParse(subscription, out _)
            && group.Length <= 90 && group.All(static c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '(' or ')')
            && secret.All(static c => c is > ' ' and < (char)127);
        return valid
            ? new DnsProviderToken($"{tenant.ToLowerInvariant()}:{client.ToLowerInvariant()}:{subscription.ToLowerInvariant()}:{group}:{secret}")
            : Error.Validation("That is not an Azure service principal: the client and subscription IDs are GUIDs, the resource group a name, and the secret one word.", "dnsToken");
    }

    /// <summary>A service account's JSON key, as Google Cloud downloads it; only what signing in needs is kept.</summary>
    private static Result<DnsProviderToken> ParseGoogleKey(string text)
    {
        const string Refused = "Paste the service account's JSON key (IAM → Service accounts → Keys → Add key → JSON).";
        if (text.Length == 0 || text.Length > 3 * MaxCredentialLength)
        {
            return Error.Validation(Refused, "dnsToken");
        }

        try
        {
            using var key = System.Text.Json.JsonDocument.Parse(text);
            System.Text.Json.JsonElement root = key.RootElement;
            string? Field(string name) =>
                root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty(name, out System.Text.Json.JsonElement v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

            string? type = Field("type");
            string? project = Field("project_id");
            string? email = Field("client_email");
            string? privateKey = Field("private_key");
            if ((type is not null && type != "service_account")
                || string.IsNullOrWhiteSpace(project)
                || email is null || !email.Contains('@', StringComparison.Ordinal)
                || privateKey is null || !privateKey.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal))
            {
                return Error.Validation("That is not a Google Cloud service account key: it needs project_id, client_email and private_key.", "dnsToken");
            }

            string kept = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string?>
            {
                ["project_id"] = project,
                ["client_email"] = email,
                ["private_key_id"] = Field("private_key_id"),
                ["private_key"] = privateKey,
            });
            return kept.Length <= MaxCredentialLength ? new DnsProviderToken(kept) : Error.Validation("That key is too long to store.", "dnsToken");
        }
        catch (System.Text.Json.JsonException)
        {
            return Error.Validation(Refused, "dnsToken");
        }
    }

    public override string ToString() => "DnsProviderToken { *** }";
}

/// <summary>An entity's DNS host and the credential the console reaches it with.</summary>
public sealed record DnsConnection(DnsProvider Provider, DnsProviderToken Token);

/// <summary>A DNS zone at the entity's DNS host.</summary>
public sealed record DnsZone(string Id, string Name);

/// <summary>A record as the DNS host holds it, in <see cref="DnsRecord"/>'s value format (MX <c>"10 host"</c>, SRV <c>"0 1 443 host"</c>, TXT unquoted).</summary>
/// <param name="Proxied">Served through the host's proxy (Cloudflare's orange cloud) rather than as plain DNS.</param>
public sealed record ZoneRecord(string Id, DnsRecord Record, bool Proxied = false);

public enum DnsChangeKind
{
    /// <summary>Already published as needed.</summary>
    Unchanged,
    Create,
    Update,

    /// <summary>
    /// A record removed from the zone: a second record of a kind there must be one of (SPF, DMARC, DKIM at one
    /// selector), or another server's MX at a name the domain publishes MX for, which would keep receiving its mail.
    /// </summary>
    Remove,

    /// <summary>Not published, with the reason in <see cref="DnsChange.Note"/>.</summary>
    Skip,
}

/// <param name="Desired">The record the domain needs; null for <see cref="DnsChangeKind.Remove"/>.</param>
/// <param name="Current">The zone's record that is updated, removed or already right; null for a create or a skip.</param>
public sealed record DnsChange(DnsChangeKind Kind, DnsRecord? Desired, ZoneRecord? Current, string? Note = null)
{
    /// <summary>Whether publishing writes anything for this change.</summary>
    public bool IsWrite => Kind is DnsChangeKind.Create or DnsChangeKind.Update or DnsChangeKind.Remove;
}

/// <summary>
/// What publishing a domain's records to its zone would do. <see cref="Warnings"/> name records left in the zone
/// that still matter (another provider's MX). <see cref="Fingerprint"/> identifies the plan, so a publish applies
/// only the plan the administrator saw; it does not depend on the order the changes are listed in.
/// </summary>
public sealed record DnsPublicationPlan(DnsZone Zone, IReadOnlyList<DnsChange> Changes, IReadOnlyList<string> Warnings)
{
    public bool HasWork => Changes.Any(static c => c.IsWrite);

    public int Count(DnsChangeKind kind) => Changes.Count(c => c.Kind == kind);

    /// <summary>The other mail servers this plan stops sending the domain's mail to (their MX records are removed).</summary>
    public IReadOnlyList<string> MailServersRemoved =>
        [.. Changes.Where(static c => c.Kind == DnsChangeKind.Remove && c.Current is { Record.Type: var t } && t.Trim().Equals("MX", StringComparison.OrdinalIgnoreCase))
            .Select(static c => c.Current!.Record.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1].TrimEnd('.'))];

    /// <summary>One line per change, sorted: what <see cref="Fingerprint"/> hashes, and what to compare when two plans differ.</summary>
    public IReadOnlyList<string> FingerprintLines =>
        [.. Changes.Select(static change => $"{change.Kind}|{(change.Desired is { } d ? $"{d.Name}|{d.Type}|{d.Value}" : "-")}|{(change.Current is { } c ? $"{c.Id}|{c.Record.Value}|{c.Proxied}" : "-")}")
            .Order(StringComparer.Ordinal)];

    public string Fingerprint
    {
        get
        {
            var text = new StringBuilder(Zone.Id).Append('\n');
            foreach (string line in FingerprintLines)
            {
                text.Append(line).Append('\n');
            }

            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
        }
    }
}

/// <summary>
/// Plans publishing a domain's records into its zone, adding and correcting, and deleting only what would work
/// against what is published: another server's MX at a name the domain publishes MX for (it would keep receiving
/// some of the domain's mail) and a duplicate of a one-of-a-kind record. One-of-a-kind records (a CNAME; a TXT whose <c>v=</c> tag makes it SPF, DMARC, DKIM, MTA-STS or
/// TLS-RPT) replace the zone's record of that kind at the name, and a duplicate of the kind is removed (two SPF
/// records fail evaluation). Other types are sets: a missing value is added, others are left (MX and SRV by host
/// keep their entry and correct its priority). CAA is added only where the name already has CAA records: a first
/// CAA record restricts every certificate authority for the whole domain. The zone's records are taken in id
/// order, so the same zone listed in another order gives the same plan (which duplicate is kept, which removed).
/// </summary>
public static class DnsPublicationPlanner
{
    public static DnsPublicationPlan Plan(DnsZone zone, IReadOnlyList<DnsRecord> desired, IReadOnlyList<ZoneRecord> existing)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        existing = [.. existing.OrderBy(static r => r.Id, StringComparer.Ordinal)];

        string zoneName = Name(zone.Name);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var changes = new List<DnsChange>();
        foreach (DnsRecord record in desired)
        {
            changes.AddRange(PlanOne(zoneName, Canonical(record), existing, claimed));
        }

        changes.AddRange(ForeignMx(desired, existing, claimed));
        return new DnsPublicationPlan(zone, changes, []);
    }

    /// <summary>
    /// <paramref name="record"/> in the form records are compared in: names lower-case without the trailing dot,
    /// type upper-case, TXT unquoted, host names in values lower-case, TLSA data without spaces.
    /// </summary>
    public static DnsRecord Canonical(DnsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string type = record.Type.Trim().ToUpperInvariant();
        string[] parts = record.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string value = type switch
        {
            "TXT" => Unquote(record.Value.Trim()),
            "CNAME" or "NS" or "PTR" => Name(record.Value),
            "MX" when parts.Length == 2 => $"{Number(parts[0])} {Name(parts[1])}",
            "SRV" when parts.Length == 4 => $"{Number(parts[0])} {Number(parts[1])} {Number(parts[2])} {(parts[3] == "." ? "." : Name(parts[3]))}",
            "TLSA" when parts.Length >= 4 => $"{Number(parts[0])} {Number(parts[1])} {Number(parts[2])} {string.Concat(parts.Skip(3)).ToUpperInvariant()}",
            "CAA" when parts.Length >= 3 => $"{Number(parts[0])} {parts[1].ToLowerInvariant()} \"{Unquote(string.Join(' ', parts.Skip(2)))}\"",
            _ => string.Join(' ', parts),
        };
        return record with { Name = Name(record.Name), Type = type, Value = value };
    }

    private static IEnumerable<DnsChange> PlanOne(string zoneName, DnsRecord wanted, IReadOnlyList<ZoneRecord> existing, HashSet<string> claimed)
    {
        if (wanted.Name != zoneName && !wanted.Name.EndsWith("." + zoneName, StringComparison.Ordinal))
        {
            yield return new DnsChange(DnsChangeKind.Skip, wanted, null, $"Outside the {zoneName} zone: publish it where {wanted.Name} is hosted.");
            yield break;
        }

        List<ZoneRecord> atName = [.. existing.Where(r => !claimed.Contains(r.Id) && Name(r.Record.Name) == wanted.Name)];
        ZoneRecord? cname = atName.FirstOrDefault(static r => Type(r) == "CNAME");
        if (wanted.Type == "CNAME")
        {
            if (atName.FirstOrDefault(static r => Type(r) != "CNAME") is { } blocker)
            {
                yield return new DnsChange(DnsChangeKind.Skip, wanted, blocker, $"{wanted.Name} already has a {Type(blocker)} record, and a CNAME cannot sit beside other records. Remove it at the DNS host to publish this one.");
                yield break;
            }

            yield return Match(wanted, cname, claimed);
            yield break;
        }

        if (cname is not null)
        {
            yield return new DnsChange(DnsChangeKind.Skip, wanted, cname, $"{wanted.Name} is a CNAME to {Canonical(cname.Record).Value}, so it cannot have other records.");
            yield break;
        }

        List<ZoneRecord> sameType = [.. atName.Where(r => Type(r) == wanted.Type)];
        switch (wanted.Type)
        {
            case "TXT" when Tag(wanted.Value) is { } tag:
                List<ZoneRecord> sameKind = [.. sameType.Where(r => Tag(Canonical(r.Record).Value) == tag)];
                yield return Match(wanted, sameKind.FirstOrDefault(), claimed);
                foreach (ZoneRecord duplicate in sameKind.Skip(1))
                {
                    claimed.Add(duplicate.Id);
                    yield return new DnsChange(DnsChangeKind.Remove, null, duplicate, $"A second {tag} record at {wanted.Name}; there must be only one.");
                }

                yield break;
            case "MX" or "SRV":
                // Same host (and port): the entry is ours, perhaps with another priority or weight.
                string key = HostKey(wanted);
                yield return Match(wanted, sameType.FirstOrDefault(r => HostKey(Canonical(r.Record)) == key), claimed);
                yield break;
            case "CAA" when sameType.Count == 0:
                yield return new DnsChange(DnsChangeKind.Skip, wanted, null, "A first CAA record would stop every other certificate authority issuing for the domain, which can break its websites. Publish it yourself if that is intended.");
                yield break;
            default:
                ZoneRecord? same = sameType.FirstOrDefault(r => Canonical(r.Record).Value.Equals(wanted.Value, StringComparison.OrdinalIgnoreCase));
                if (same is not null)
                {
                    claimed.Add(same.Id);
                    yield return new DnsChange(DnsChangeKind.Unchanged, wanted, same);
                }
                else
                {
                    yield return new DnsChange(DnsChangeKind.Create, wanted, null);
                }

                yield break;
        }
    }

    /// <summary>Unchanged when <paramref name="current"/> already holds the value (as plain DNS), an update when it differs, a create when there is none.</summary>
    private static DnsChange Match(DnsRecord wanted, ZoneRecord? current, HashSet<string> claimed)
    {
        if (current is null)
        {
            return new DnsChange(DnsChangeKind.Create, wanted, null);
        }

        claimed.Add(current.Id);
        bool sameValue = string.Equals(Canonical(current.Record).Value, wanted.Value, wanted.Type == "TXT" ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        if (sameValue && !current.Proxied)
        {
            return new DnsChange(DnsChangeKind.Unchanged, wanted, current);
        }

        return new DnsChange(DnsChangeKind.Update, wanted, current, sameValue ? "Served through Cloudflare's proxy; mail needs plain DNS." : null);
    }

    /// <summary>
    /// MX records at a name the domain publishes MX for, pointing at hosts it does not: removed, since they would
    /// keep receiving some of the domain's mail. The page asks before publishing such a plan.
    /// </summary>
    private static IEnumerable<DnsChange> ForeignMx(IReadOnlyList<DnsRecord> desired, IReadOnlyList<ZoneRecord> existing, HashSet<string> claimed)
    {
        List<DnsRecord> wantedMx = [.. desired.Select(Canonical).Where(static r => r.Type == "MX")];
        foreach (ZoneRecord record in existing.Where(r => Type(r) == "MX" && !claimed.Contains(r.Id)))
        {
            DnsRecord mx = Canonical(record.Record);
            if (wantedMx.Any(w => w.Name == mx.Name) && !wantedMx.Any(w => w.Name == mx.Name && HostKey(w) == HostKey(mx)))
            {
                claimed.Add(record.Id);
                yield return new DnsChange(DnsChangeKind.Remove, null, record, $"Another mail server ({HostKey(mx)}); it would keep receiving some of {mx.Name}'s mail.");
            }
        }
    }

    private static string Type(ZoneRecord record) => record.Record.Type.Trim().ToUpperInvariant();

    /// <summary>The <c>v=</c> tag of a TXT value that has one (<c>v=spf1</c>, <c>v=dmarc1</c>, …), lower-case.</summary>
    private static string? Tag(string value)
    {
        if (!value.StartsWith("v=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int end = value.IndexOfAny([';', ' ']);
        return (end < 0 ? value : value[..end]).ToLowerInvariant();
    }

    /// <summary>MX: the exchange; SRV: port and target.</summary>
    private static string HostKey(DnsRecord canonical)
    {
        string[] parts = canonical.Value.Split(' ');
        return canonical.Type == "SRV" && parts.Length == 4 ? $"{parts[2]} {parts[3]}" : parts[^1];
    }

    private static string Name(string name) => name.Trim().TrimEnd('.').ToLowerInvariant();

    private static string Number(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n.ToString(CultureInfo.InvariantCulture) : text;

    /// <summary>A TXT value from <c>"a" "b"</c> form (as DNS hosts show it) to <c>ab</c>; unquoted text is returned as is.</summary>
    internal static string Unquote(string value)
    {
        if (!value.StartsWith('"'))
        {
            return value;
        }

        var text = new StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"')
            {
                inQuote = !inQuote;
            }
            else if (inQuote && c == '\\' && i + 1 < value.Length)
            {
                text.Append(value[++i]);
            }
            else if (inQuote)
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
