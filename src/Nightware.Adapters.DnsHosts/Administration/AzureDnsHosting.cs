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
/// <see cref="IDnsHosting"/> over Azure Resource Manager (<c>Microsoft.Network/dnsZones</c>, api-version 2018-05-01)
/// with a service principal (<c>TENANT:CLIENT:SUBSCRIPTION:RESOURCEGROUP:SECRET</c>) that signs in with client
/// credentials. A zone is read by name in the principal's resource group, the domain and then each parent, so DNS
/// Zone Contributor on the zone alone is enough. Records are record sets with typed arrays (<c>MXRecords</c>,
/// <c>TXTRecords</c>, …), read page by page; each value becomes a <see cref="ZoneRecord"/>
/// (<see cref="ZoneNames.RecordId"/>). A change reads its one set, changes one entry and writes the set back
/// (<c>PUT</c>, guarded by its etag), or deletes it when no entry is left. That API version hosts no TLSA records,
/// and alias record sets are left out.
/// </summary>
internal sealed class AzureDnsHosting(IDnsHostClient client, AccessTokenCache tokens, IOptions<DnsHostsOptions> options) : IDnsHosting
{
    private const string ApiVersion = "api-version=2018-05-01";

    private static readonly HashSet<string> TokenRefusals =
    [
        "InvalidAuthenticationToken", "AuthenticationFailed", "AuthorizationFailed", "InvalidAuthenticationTokenTenant", "ExpiredAuthenticationToken",
        "invalid_client", "unauthorized_client", "invalid_request", "invalid_grant",
    ];

    public DnsProvider Provider => DnsProvider.AzureDns;

    public async Task<Result<DnsZone>> FindZoneAsync(DnsProviderToken token, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        Principal principal = Principal.From(token);
        foreach (string candidate in ZoneNames.Candidates(domain))
        {
            Result<DnsHostResponse> found = await SendAsync(HttpMethod.Get, Zone(principal, candidate, ApiVersion), token, null, null, cancellationToken, ZoneFault).ConfigureAwait(false);
            if (found.IsSuccess)
            {
                string name = JsonNode.Parse(found.Value.Body)?["name"]?.GetValue<string>() ?? candidate;
                return new DnsZone(name, name);
            }

            if (found.Error.Kind != ErrorKind.NotFound)
            {
                return found.Error;
            }
        }

        return Error.NotFound($"The service principal reaches no Azure DNS zone for {domain} in resource group {principal.ResourceGroup}.");
    }

    public async Task<Result<IReadOnlyList<ZoneRecord>>> ListRecordsAsync(DnsProviderToken token, DnsZone zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(zone);

        var records = new List<ZoneRecord>();
        Uri? next = Zone(Principal.From(token), zone.Id, $"recordsets?$top=1000&{ApiVersion}");
        while (next is not null)
        {
            Result<DnsHostResponse> listed = await SendAsync(HttpMethod.Get, next, token, null, null, cancellationToken).ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                return listed.Error;
            }

            JsonNode? json = JsonNode.Parse(listed.Value.Body);
            if (json?["value"] is JsonArray sets)
            {
                records.AddRange(sets.OfType<JsonObject>().SelectMany(s => ToZoneRecords(s, zone.Name)));
            }

            if (records.Count > options.Value.MaxRecords)
            {
                return Error.BackendUnavailable($"The {zone.Name} zone has more than {options.Value.MaxRecords} records (DnsHosts:MaxRecords); publish by hand.");
            }

            next = NextLink(json?["nextLink"]?.GetValue<string>());
        }

        return records;
    }

    public async Task<Result> ApplyAsync(DnsProviderToken token, DnsZone zone, DnsChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(change);

        DnsRecord record = change.Desired ?? change.Current?.Record ?? throw new ArgumentException("A change names a record.", nameof(change));
        string type = record.Type.Trim().ToUpperInvariant();
        if (Field(type) is not { } field)
        {
            return Error.Validation($"Azure DNS does not host {type} records; publish this one elsewhere or leave it out.");
        }

        (string? remove, JsonNode? add) = change switch
        {
            { Kind: DnsChangeKind.Create, Desired: { } desired } => ((string?)null, Entry(desired)),
            { Kind: DnsChangeKind.Update, Desired: { } desired, Current: { } held } => (held.Record.Value, Entry(desired)),
            { Kind: DnsChangeKind.Remove, Current: { } held } => (held.Record.Value, (JsonNode?)null),
            _ => throw new ArgumentException($"{change.Kind} is not a change to write.", nameof(change)),
        };

        Uri set = Zone(Principal.From(token), zone.Id, $"{type}/{Uri.EscapeDataString(ZoneNames.Relative(record.Name, zone.Name))}?{ApiVersion}");
        Result<DnsHostResponse> read = await SendAsync(HttpMethod.Get, set, token, null, null, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess && read.Error.Kind != ErrorKind.NotFound)
        {
            return read.Error;
        }

        JsonObject? current = read.IsSuccess ? JsonNode.Parse(read.Value.Body) as JsonObject : null;
        JsonObject properties = current?["properties"]?.DeepClone() as JsonObject ?? [];
        List<JsonNode> entries = [.. Entries(properties, field).Where(e => Value(type, e) != remove).Select(static e => e.DeepClone())];
        if (add is not null && !entries.Any(e => Value(type, e) == Value(type, add)))
        {
            entries.Add(add);
        }

        string? etag = current?["etag"]?.GetValue<string>();
        Result<DnsHostResponse> written;
        if (entries.Count == 0)
        {
            if (current is null)
            {
                return Result.Success();
            }

            written = await SendAsync(HttpMethod.Delete, set, token, null, etag, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var body = new JsonObject { ["TTL"] = properties["TTL"]?.GetValue<int>() ?? ZoneNames.Ttl(change.Desired?.Ttl, 3600, 1, 2_147_483_647) };
            if (properties["metadata"] is JsonObject metadata)
            {
                body["metadata"] = metadata.DeepClone();
            }

            body[field] = type == "CNAME" ? entries[^1] : new JsonArray([.. entries]);
            written = await SendAsync(HttpMethod.Put, set, token, new JsonObject { ["properties"] = body }, etag, cancellationToken).ConfigureAwait(false);
        }

        return written.IsSuccess ? Result.Success() : written.Error;
    }

    internal static IEnumerable<ZoneRecord> ToZoneRecords(JsonObject set, string zone)
    {
        ArgumentNullException.ThrowIfNull(set);
        string type = (set["type"]?.GetValue<string>() ?? string.Empty).Split('/')[^1].ToUpperInvariant();
        if (set["name"]?.GetValue<string>() is not { } name
            || set["properties"] is not JsonObject properties
            || properties["targetResource"]?["id"] is not null
            || Field(type) is not { } field)
        {
            yield break;
        }

        string fqdn = ZoneNames.Absolute(name, zone);
        int? ttl = properties["TTL"] is JsonValue t && t.TryGetValue(out int seconds) ? seconds : null;
        foreach (JsonNode entry in Entries(properties, field))
        {
            string value = Value(type, entry);
            yield return new ZoneRecord(ZoneNames.RecordId(fqdn, type, value), new DnsRecord(fqdn, type, ttl, value));
        }
    }

    /// <summary>The property holding a type's entries; null for a type the console does not read or write.</summary>
    private static string? Field(string type) => type switch
    {
        "A" => "ARecords",
        "AAAA" => "AAAARecords",
        "CNAME" => "CNAMERecord",
        "MX" => "MXRecords",
        "NS" => "NSRecords",
        "PTR" => "PTRRecords",
        "SRV" => "SRVRecords",
        "TXT" => "TXTRecords",
        "CAA" => "caaRecords",
        _ => null,
    };

    private static IEnumerable<JsonNode> Entries(JsonObject properties, string field) =>
        properties[field] switch
        {
            JsonArray entries => entries.OfType<JsonNode>(),
            JsonObject single => [single],
            _ => [],
        };

    /// <summary>An entry in <see cref="DnsRecord"/>'s value format (TXT strings joined, host names as Azure holds them).</summary>
    private static string Value(string type, JsonNode entry) => type switch
    {
        "A" => Text(entry["ipv4Address"]),
        "AAAA" => Text(entry["ipv6Address"]),
        "CNAME" => Text(entry["cname"]),
        "MX" => $"{Number(entry["preference"])} {Text(entry["exchange"])}",
        "NS" => Text(entry["nsdname"]),
        "PTR" => Text(entry["ptrdname"]),
        "SRV" => $"{Number(entry["priority"])} {Number(entry["weight"])} {Number(entry["port"])} {Text(entry["target"])}",
        "TXT" => entry["value"] is JsonArray strings ? string.Concat(strings.Select(static s => s?.GetValue<string>())) : string.Empty,
        "CAA" => $"{Number(entry["flags"])} {Text(entry["tag"])} \"{Text(entry["value"])}\"",
        _ => string.Empty,
    };

    /// <summary>The entry for <paramref name="record"/> (canonical).</summary>
    internal static JsonObject Entry(DnsRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string type = record.Type.Trim().ToUpperInvariant();
        string[] parts = record.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return type switch
        {
            "A" => new JsonObject { ["ipv4Address"] = record.Value },
            "AAAA" => new JsonObject { ["ipv6Address"] = record.Value },
            "CNAME" => new JsonObject { ["cname"] = record.Value },
            "NS" => new JsonObject { ["nsdname"] = record.Value },
            "PTR" => new JsonObject { ["ptrdname"] = record.Value },
            "MX" when parts.Length == 2 => new JsonObject { ["preference"] = Int(parts[0]), ["exchange"] = parts[1] },
            "SRV" when parts.Length == 4 => new JsonObject { ["priority"] = Int(parts[0]), ["weight"] = Int(parts[1]), ["port"] = Int(parts[2]), ["target"] = parts[3] },
            "CAA" when parts.Length >= 3 => new JsonObject { ["flags"] = Int(parts[0]), ["tag"] = parts[1], ["value"] = string.Join(' ', parts.Skip(2)).Trim('"') },
            "TXT" => new JsonObject { ["value"] = new JsonArray([.. record.Value.Chunk(255).Select(static c => (JsonNode)JsonValue.Create(new string(c)))]) },
            _ => throw new ArgumentException($"{type} {record.Value} cannot be written to Azure DNS.", nameof(record)),
        };
    }

    private Uri Zone(Principal principal, string zone, string rest) =>
        new(options.Value.AzureManagementBaseUrl,
            $"subscriptions/{Uri.EscapeDataString(principal.Subscription)}/resourceGroups/{Uri.EscapeDataString(principal.ResourceGroup)}/providers/Microsoft.Network/dnsZones/{Uri.EscapeDataString(zone)}"
            + (rest.StartsWith(ApiVersion, StringComparison.Ordinal) ? "?" + rest : "/" + rest));

    /// <summary>Azure's next page, followed only on Azure's own host: the access token goes nowhere else.</summary>
    private Uri? NextLink(string? link)
    {
        Uri management = options.Value.AzureManagementBaseUrl;
        return link is not null && Uri.TryCreate(link, UriKind.Absolute, out Uri? next)
            && next.Scheme == management.Scheme && string.Equals(next.Authority, management.Authority, StringComparison.OrdinalIgnoreCase)
            ? next
            : null;
    }

    private async Task<Result<DnsHostResponse>> SendAsync(
        HttpMethod method, Uri uri, DnsProviderToken token, JsonObject? body, string? etag, CancellationToken cancellationToken, Func<string, DnsHostFault>? fault = null)
    {
        Result<string> access = await tokens.GetAsync(Provider, token, ct => SignInAsync(Principal.From(token), ct), cancellationToken).ConfigureAwait(false);
        if (!access.IsSuccess)
        {
            return access.Error;
        }

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Value);
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(Provider, request, fault ?? Fault, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Client credentials at Microsoft Entra ID, for Azure Resource Manager.</summary>
    private async Task<Result<(string Token, TimeSpan Lifetime)>> SignInAsync(Principal principal, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Value.AzureLoginBaseUrl, $"{Uri.EscapeDataString(principal.Tenant)}/oauth2/v2.0/token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = principal.Client,
                ["client_secret"] = principal.Secret,
                ["scope"] = new Uri(options.Value.AzureManagementBaseUrl, ".default").ToString(),
            }),
        };
        Result<DnsHostResponse> issued = await client.SendAsync(Provider, request, Fault, cancellationToken).ConfigureAwait(false);
        return issued.IsSuccess ? AccessToken(issued.Value.Body) : issued.Error;
    }

    internal static Result<(string Token, TimeSpan Lifetime)> AccessToken(string body)
    {
        JsonNode? json = JsonNode.Parse(body);
        return json?["access_token"]?.GetValue<string>() is { Length: > 0 } accessToken
            ? (accessToken, TimeSpan.FromSeconds(json["expires_in"] is JsonValue e && e.TryGetValue(out int seconds) ? seconds : 300))
            : Error.BackendUnavailable("The sign-in answered without an access token. Try again shortly.");
    }

    /// <summary>
    /// Resource Manager's <c>{"error":{"code","message"}}</c>, or Entra ID's
    /// <c>{"error":"invalid_client","error_description":"AADSTS7000215: …"}</c>.
    /// </summary>
    internal static DnsHostFault Fault(string body)
    {
        JsonNode? json = JsonNode.Parse(body);
        if (json?["error"] is JsonValue oauth && oauth.TryGetValue(out string? oauthCode))
        {
            string? description = json["error_description"]?.GetValue<string>();
            return new DnsHostFault(description?.Split(['\r', '\n'])[0] ?? oauthCode, TokenRefused: TokenRefusals.Contains(oauthCode));
        }

        string? code = json?["error"]?["code"]?.GetValue<string>();
        return new DnsHostFault(json?["error"]?["message"]?.GetValue<string>() ?? code, TokenRefused: code is not null && TokenRefusals.Contains(code));
    }

    /// <summary>
    /// <see cref="Fault"/> for reading a zone by name. A principal with the role on its own zones only is refused
    /// (<c>AuthorizationFailed</c>) any other name in the group, whether or not a zone has it. Signing in already
    /// proved the credential, so there that refusal means "not this one", and the walk goes on to the parent.
    /// </summary>
    internal static DnsHostFault ZoneFault(string body)
    {
        DnsHostFault fault = Fault(body);
        return JsonNode.Parse(body)?["error"]?["code"]?.GetValue<string>() == "AuthorizationFailed" ? fault with { NotFound = true } : fault;
    }

    private static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;

    private static string Number(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out int n) ? n.ToString(CultureInfo.InvariantCulture) : "0";

    private static int Int(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>The parts of <c>TENANT:CLIENT:SUBSCRIPTION:RESOURCEGROUP:SECRET</c>.</summary>
    private sealed record Principal(string Tenant, string Client, string Subscription, string ResourceGroup, string Secret)
    {
        public static Principal From(DnsProviderToken token)
        {
            string[] parts = token.Value.Split(':', 5);
            return parts.Length == 5
                ? new Principal(parts[0], parts[1], parts[2], parts[3], parts[4])
                : throw new ArgumentException("Not an Azure service principal.", nameof(token));
        }
    }
}
