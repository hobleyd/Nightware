using System.Globalization;
using System.Text;
using System.Xml.Linq;

using Microsoft.Extensions.Options;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Administration;

/// <summary>
/// <see cref="IDnsHosting"/> over Amazon Route 53's REST/XML API with an IAM access key (<c>KEYID:SECRET</c>, signed
/// with <see cref="AwsSignature"/>). A zone is a public hosted zone found by exact name
/// (<c>GET hostedzonesbyname?dnsname=</c>) for the domain and then each parent. Records are record sets, read page by
/// page; each value becomes a <see cref="ZoneRecord"/> (<see cref="ZoneNames.RecordId"/>). Alias and routing-policy
/// sets are not the console's to touch and are left out. A change reads its one set and writes it back with UPSERT,
/// or DELETEs it (as read) when no value is left. Values are in zone-file form.
/// </summary>
internal sealed class Route53DnsHosting(IDnsHostClient client, IOptions<DnsHostsOptions> options, TimeProvider clock) : IDnsHosting
{
    private const int PageSize = 300;

    private static readonly XNamespace Ns = "https://route53.amazonaws.com/doc/2013-04-01/";

    /// <summary>Error codes for a key that is wrong, expired, mis-signed or lacks a permission.</summary>
    private static readonly HashSet<string> TokenRefusals =
    [
        "InvalidClientTokenId", "SignatureDoesNotMatch", "AccessDenied", "AccessDeniedException", "IncompleteSignature",
        "MissingAuthenticationToken", "UnrecognizedClientException", "ExpiredToken", "InvalidSignatureException",
    ];

    private static readonly HashSet<string> Throttles = ["Throttling", "ThrottlingException", "PriorRequestNotComplete"];

    public DnsProvider Provider => DnsProvider.Route53;

    public async Task<Result<DnsZone>> FindZoneAsync(DnsProviderToken token, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        foreach (string candidate in ZoneNames.Candidates(domain))
        {
            Result<DnsHostResponse> found = await SendAsync(HttpMethod.Get, $"hostedzonesbyname?dnsname={Uri.EscapeDataString(candidate)}&maxitems=1", token, null, cancellationToken).ConfigureAwait(false);
            if (!found.IsSuccess)
            {
                return found.Error;
            }

            // The listing starts at the name: its first zone may be a later one.
            XElement? zone = XDocument.Parse(found.Value.Body).Root?.Element(Ns + "HostedZones")?.Elements(Ns + "HostedZone").FirstOrDefault();
            if (zone is not null
                && ZoneNames.Plain(Decode((string?)zone.Element(Ns + "Name") ?? string.Empty)) == candidate
                && !string.Equals((string?)zone.Element(Ns + "Config")?.Element(Ns + "PrivateZone"), "true", StringComparison.OrdinalIgnoreCase)
                && (string?)zone.Element(Ns + "Id") is { } id)
            {
                return new DnsZone(id.Replace("/hostedzone/", string.Empty, StringComparison.Ordinal), candidate);
            }
        }

        return Error.NotFound($"The key reaches no public Route 53 hosted zone for {domain}.");
    }

    public async Task<Result<IReadOnlyList<ZoneRecord>>> ListRecordsAsync(DnsProviderToken token, DnsZone zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(zone);

        var records = new List<ZoneRecord>();
        string next = string.Empty;
        while (true)
        {
            Result<DnsHostResponse> listed = await SendAsync(HttpMethod.Get, $"{Sets(zone)}?maxitems={PageSize}{next}", token, null, cancellationToken).ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                return listed.Error;
            }

            XElement root = XDocument.Parse(listed.Value.Body).Root!;
            foreach (XElement set in root.Element(Ns + "ResourceRecordSets")?.Elements(Ns + "ResourceRecordSet") ?? [])
            {
                records.AddRange(ToZoneRecords(set));
            }

            if (records.Count > options.Value.MaxRecords)
            {
                return Error.BackendUnavailable($"The {zone.Name} zone has more than {options.Value.MaxRecords} records (DnsHosts:MaxRecords); publish by hand.");
            }

            if (!string.Equals((string?)root.Element(Ns + "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return records;
            }

            next = $"&name={Uri.EscapeDataString((string?)root.Element(Ns + "NextRecordName") ?? string.Empty)}&type={Uri.EscapeDataString((string?)root.Element(Ns + "NextRecordType") ?? string.Empty)}"
                + ((string?)root.Element(Ns + "NextRecordIdentifier") is { } identifier ? $"&identifier={Uri.EscapeDataString(identifier)}" : string.Empty);
        }
    }

    public async Task<Result> ApplyAsync(DnsProviderToken token, DnsZone zone, DnsChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(change);

        DnsRecord record = change.Desired ?? change.Current?.Record ?? throw new ArgumentException("A change names a record.", nameof(change));
        string name = ZoneNames.Fqdn(record.Name);
        string type = record.Type.Trim().ToUpperInvariant();
        (string? remove, string? add) = change switch
        {
            { Kind: DnsChangeKind.Create, Desired: { } desired } => ((string?)null, ZoneNames.Presentation(desired)),
            { Kind: DnsChangeKind.Update, Desired: { } desired, Current: { } held } => (held.Record.Value, ZoneNames.Presentation(desired)),
            { Kind: DnsChangeKind.Remove, Current: { } held } => (held.Record.Value, (string?)null),
            _ => throw new ArgumentException($"{change.Kind} is not a change to write.", nameof(change)),
        };

        Result<DnsHostResponse> read = await SendAsync(
                HttpMethod.Get, $"{Sets(zone)}?name={Uri.EscapeDataString(name)}&type={type}&maxitems=1", token, null, cancellationToken)
            .ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        XElement? existing = XDocument.Parse(read.Value.Body).Root?.Element(Ns + "ResourceRecordSets")?.Elements(Ns + "ResourceRecordSet")
            .FirstOrDefault(s => ZoneNames.Plain(Decode((string?)s.Element(Ns + "Name") ?? string.Empty)) == ZoneNames.Plain(name)
                && (string?)s.Element(Ns + "Type") == type
                && s.Element(Ns + "SetIdentifier") is null
                && s.Element(Ns + "AliasTarget") is null);
        List<string> current = existing is null ? [] : [.. Values(existing)];
        List<string> values = [.. current.Where(v => v != remove)];
        if (add is not null && !values.Contains(add))
        {
            values.Add(add);
        }

        XElement changeElement;
        if (values.Count == 0)
        {
            if (existing is null)
            {
                return Result.Success();
            }

            // A deletion must match the set exactly as Route 53 holds it.
            changeElement = new XElement(Ns + "Change", new XElement(Ns + "Action", "DELETE"), Strip(existing));
        }
        else
        {
            int ttl = ZoneNames.Ttl(existing is null ? change.Desired?.Ttl : (int?)existing.Element(Ns + "TTL"), 3600, 0, 2_147_483_647);
            changeElement = new XElement(Ns + "Change", new XElement(Ns + "Action", "UPSERT"), Set(name, type, ttl, values));
        }

        var body = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "ChangeResourceRecordSetsRequest", new XElement(Ns + "ChangeBatch", new XElement(Ns + "Comment", ZoneNames.Comment), new XElement(Ns + "Changes", changeElement))));
        Result<DnsHostResponse> written = await SendAsync(HttpMethod.Post, $"{Sets(zone)}", token, body, cancellationToken).ConfigureAwait(false);
        return written.IsSuccess ? Result.Success() : written.Error;
    }

    internal static IEnumerable<ZoneRecord> ToZoneRecords(XElement set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Element(Ns + "AliasTarget") is not null || set.Element(Ns + "SetIdentifier") is not null
            || (string?)set.Element(Ns + "Name") is not { } encoded || (string?)set.Element(Ns + "Type") is not { } type)
        {
            yield break;
        }

        string fqdn = ZoneNames.Plain(Decode(encoded));
        int? ttl = (int?)set.Element(Ns + "TTL");
        foreach (string value in Values(set))
        {
            yield return new ZoneRecord(ZoneNames.RecordId(fqdn, type, value), new DnsRecord(fqdn, type, ttl, value));
        }
    }

    /// <summary>Route 53 writes some characters in names as <c>\ooo</c> (octal), a wildcard as <c>\052</c>.</summary>
    internal static string Decode(string name)
    {
        if (!name.Contains('\\', StringComparison.Ordinal))
        {
            return name;
        }

        var text = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '\\' && i + 3 < name.Length && name.Substring(i + 1, 3).All(static c => c is >= '0' and <= '7'))
            {
                text.Append((char)Convert.ToInt32(name.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                text.Append(name[i]);
            }
        }

        return text.ToString();
    }

    private static string Sets(DnsZone zone) => $"hostedzone/{Uri.EscapeDataString(zone.Id)}/rrset";

    private static IEnumerable<string> Values(XElement set) =>
        set.Element(Ns + "ResourceRecords")?.Elements(Ns + "ResourceRecord").Select(static r => (string?)r.Element(Ns + "Value")).OfType<string>() ?? [];

    private static XElement Set(string name, string type, int ttl, IEnumerable<string> values) =>
        new(Ns + "ResourceRecordSet",
            new XElement(Ns + "Name", name),
            new XElement(Ns + "Type", type),
            new XElement(Ns + "TTL", ttl.ToString(CultureInfo.InvariantCulture)),
            new XElement(Ns + "ResourceRecords", values.Select(v => new XElement(Ns + "ResourceRecord", new XElement(Ns + "Value", v)))));

    /// <summary>The set as read, without what a listing adds beyond the set itself.</summary>
    private static XElement Strip(XElement set) =>
        Set((string?)set.Element(Ns + "Name") ?? string.Empty, (string?)set.Element(Ns + "Type") ?? string.Empty, (int?)set.Element(Ns + "TTL") ?? 0, Values(set));

    private async Task<Result<DnsHostResponse>> SendAsync(HttpMethod method, string path, DnsProviderToken token, XDocument? body, CancellationToken cancellationToken)
    {
        string[] key = token.Value.Split(':', 2);
        byte[] bytes = body is null ? [] : Encoding.UTF8.GetBytes(body.Declaration + body.ToString(SaveOptions.DisableFormatting));
        using var request = new HttpRequestMessage(method, new Uri(options.Value.Route53ApiBaseUrl, path));
        if (body is not null)
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/xml");
        }

        AwsSignature.Sign(request, bytes, key[0], key.Length > 1 ? key[1] : string.Empty, "us-east-1", "route53", clock.GetUtcNow());
        return await client.SendAsync(Provider, request, Fault, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>&lt;ErrorResponse&gt;&lt;Error&gt;&lt;Code/&gt;&lt;Message/&gt;</c>, or for a refused batch
    /// <c>&lt;InvalidChangeBatch&gt;&lt;Messages&gt;&lt;Message/&gt;</c>.
    /// </summary>
    internal static DnsHostFault Fault(string body)
    {
        XElement root = XDocument.Parse(body).Root!;
        XNamespace ns = root.Name.Namespace;
        if (root.Name.LocalName == "InvalidChangeBatch")
        {
            return new DnsHostFault(string.Join("; ", root.Descendants(ns + "Message").Select(static m => m.Value)));
        }

        XElement? error = root.Element(ns + "Error") ?? root;
        string? code = (string?)error.Element(ns + "Code");
        return new DnsHostFault(
            (string?)error.Element(ns + "Message") ?? code,
            TokenRefused: code is not null && TokenRefusals.Contains(code),
            Throttled: code is not null && Throttles.Contains(code));
    }
}
