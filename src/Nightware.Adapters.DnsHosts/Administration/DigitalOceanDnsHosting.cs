using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Administration;

/// <summary>
/// <see cref="IDnsHosting"/> over DigitalOcean's v2 API with a personal access token (Bearer): <c>GET domains/{name}</c>
/// for the domain and then each parent, <c>GET domains/{name}/records</c> page by page, and <c>POST</c>/<c>PUT</c>/
/// <c>DELETE</c> of one record per change. Records have ids of their own. Names are relative (<c>@</c> for the apex);
/// MX and SRV keep priority, weight and port beside <c>data</c>, CAA its flags and tag; TXT <c>data</c> is the bare text.
/// DigitalOcean hosts no TLSA records.
/// </summary>
internal sealed class DigitalOceanDnsHosting(IDnsHostClient client, IOptions<DnsHostsOptions> options) : IDnsHosting
{
    private const int PageSize = 200;

    public DnsProvider Provider => DnsProvider.DigitalOcean;

    public async Task<Result<DnsZone>> FindZoneAsync(DnsProviderToken token, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        foreach (string candidate in ZoneNames.Candidates(domain))
        {
            Result<DnsHostResponse> found = await SendAsync(HttpMethod.Get, $"domains/{Uri.EscapeDataString(candidate)}", token, null, cancellationToken).ConfigureAwait(false);
            if (found.IsSuccess)
            {
                string name = JsonNode.Parse(found.Value.Body)?["domain"]?["name"]?.GetValue<string>() ?? candidate;
                return new DnsZone(name, name);
            }

            if (found.Error.Kind != ErrorKind.NotFound)
            {
                return found.Error;
            }
        }

        return Error.NotFound($"The token reaches no DigitalOcean domain for {domain}.");
    }

    public async Task<Result<IReadOnlyList<ZoneRecord>>> ListRecordsAsync(DnsProviderToken token, DnsZone zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(zone);

        var records = new List<ZoneRecord>();
        for (int page = 1; ; page++)
        {
            Result<DnsHostResponse> listed = await SendAsync(
                    HttpMethod.Get, $"domains/{Uri.EscapeDataString(zone.Id)}/records?per_page={PageSize}&page={page}", token, null, cancellationToken)
                .ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                return listed.Error;
            }

            JsonNode? json = JsonNode.Parse(listed.Value.Body);
            if (json?["domain_records"] is JsonArray items)
            {
                records.AddRange(items.OfType<JsonObject>().Select(r => ToZoneRecord(r, zone.Name)).OfType<ZoneRecord>());
            }

            if (records.Count > options.Value.MaxRecords)
            {
                return Error.BackendUnavailable($"The {zone.Name} zone has more than {options.Value.MaxRecords} records (DnsHosts:MaxRecords); publish by hand.");
            }

            if (json?["links"]?["pages"]?["next"] is null || json["domain_records"] is not JsonArray { Count: > 0 })
            {
                return records;
            }
        }
    }

    public async Task<Result> ApplyAsync(DnsProviderToken token, DnsZone zone, DnsChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(change);

        if (change.Desired is { Type: var type } && type.Equals("TLSA", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("DigitalOcean does not host TLSA records; publish this one elsewhere or leave it out.");
        }

        string records = $"domains/{Uri.EscapeDataString(zone.Id)}/records";
        Result<DnsHostResponse> sent = change switch
        {
            { Kind: DnsChangeKind.Create, Desired: { } desired } =>
                await SendAsync(HttpMethod.Post, records, token, ToBody(desired, zone.Name), cancellationToken).ConfigureAwait(false),
            { Kind: DnsChangeKind.Update, Desired: { } desired, Current: { } current } =>
                await SendAsync(HttpMethod.Put, $"{records}/{Uri.EscapeDataString(current.Id)}", token, ToBody(desired, zone.Name), cancellationToken).ConfigureAwait(false),
            { Kind: DnsChangeKind.Remove, Current: { } current } =>
                await SendAsync(HttpMethod.Delete, $"{records}/{Uri.EscapeDataString(current.Id)}", token, null, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"{change.Kind} is not a change to write.", nameof(change)),
        };
        return sent.IsSuccess ? Result.Success() : sent.Error;
    }

    internal static ZoneRecord? ToZoneRecord(JsonObject json, string zone)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json["id"] is not JsonValue idValue || Text(json["type"]) is not { } type || Text(json["name"]) is not { } name)
        {
            return null;
        }

        string id = idValue.TryGetValue(out long number) ? number.ToString(CultureInfo.InvariantCulture) : idValue.ToString();
        string data = Text(json["data"]) ?? string.Empty;
        string target = data == ZoneNames.Apex ? zone : data;
        string value = type.ToUpperInvariant() switch
        {
            "MX" => $"{Number(json["priority"])} {target}",
            "SRV" => $"{Number(json["priority"])} {Number(json["weight"])} {Number(json["port"])} {target}",
            "CAA" => $"{Number(json["flags"])} {Text(json["tag"])} \"{data}\"",
            "CNAME" or "NS" => target,
            _ => data,
        };
        int? ttl = json["ttl"] is JsonValue t && t.TryGetValue(out int seconds) ? seconds : null;
        return new ZoneRecord(id, new DnsRecord(ZoneNames.Absolute(name, zone), type, ttl, value));
    }

    /// <summary>The body that creates or overwrites a record with <paramref name="record"/> (canonical).</summary>
    internal static JsonObject ToBody(DnsRecord record, string zone)
    {
        ArgumentNullException.ThrowIfNull(record);
        string type = record.Type.ToUpperInvariant();
        string[] parts = record.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var body = new JsonObject
        {
            ["type"] = type,
            ["name"] = ZoneNames.Relative(record.Name, zone),
            ["ttl"] = ZoneNames.Ttl(record.Ttl, 3600, 30, 86400),
        };
        switch (type)
        {
            case "MX" when parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int priority):
                body["priority"] = priority;
                body["data"] = ZoneNames.Fqdn(parts[1]);
                break;
            case "SRV" when parts.Length == 4 && parts.Take(3).All(static p => int.TryParse(p, CultureInfo.InvariantCulture, out _)):
                body["priority"] = int.Parse(parts[0], CultureInfo.InvariantCulture);
                body["weight"] = int.Parse(parts[1], CultureInfo.InvariantCulture);
                body["port"] = int.Parse(parts[2], CultureInfo.InvariantCulture);
                body["data"] = ZoneNames.Fqdn(parts[3]);
                break;
            case "CAA" when parts.Length >= 3 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out int flags):
                body["flags"] = flags;
                body["tag"] = parts[1];
                body["data"] = string.Join(' ', parts.Skip(2)).Trim('"');
                break;
            case "CNAME" or "NS":
                body["data"] = ZoneNames.Fqdn(record.Value);
                break;
            default:
                body["data"] = record.Value;
                break;
        }

        return body;
    }

    private Task<Result<DnsHostResponse>> SendAsync(HttpMethod method, string path, DnsProviderToken token, JsonObject? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(options.Value.DigitalOceanApiBaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return SendDisposingAsync(request, cancellationToken);
    }

    private async Task<Result<DnsHostResponse>> SendDisposingAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            return await client.SendAsync(Provider, request, Fault, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary><c>{"id":"unauthorized","message":"Unable to authenticate you"}</c>.</summary>
    internal static DnsHostFault Fault(string body) => new(Text(JsonNode.Parse(body)?["message"]));

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string Number(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out int n) ? n.ToString(CultureInfo.InvariantCulture) : "0";
}
