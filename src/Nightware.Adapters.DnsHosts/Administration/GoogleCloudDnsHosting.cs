using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Administration;

/// <summary>
/// <see cref="IDnsHosting"/> over Google Cloud DNS's v1 API with a service account's key, which signs a short JWT
/// exchanged at Google's token endpoint for an access token. A zone is a public managed zone of the key's project
/// found by DNS name (<c>managedZones?dnsName=</c>) for the domain and then each parent. Records are record sets
/// (<c>rrdatas</c> in zone-file form), read page by page; each value becomes a <see cref="ZoneRecord"/>
/// (<see cref="ZoneNames.RecordId"/>). A change reads its one set and creates, patches or deletes it, so no deletion
/// has to match the set exactly. Sets with a routing policy are left out.
/// </summary>
internal sealed class GoogleCloudDnsHosting(IDnsHostClient client, AccessTokenCache tokens, IOptions<DnsHostsOptions> options, TimeProvider clock) : IDnsHosting
{
    private const string Scope = "https://www.googleapis.com/auth/ndev.clouddns.readwrite";

    private const int PageSize = 1000;

    public DnsProvider Provider => DnsProvider.GoogleCloud;

    public async Task<Result<DnsZone>> FindZoneAsync(DnsProviderToken token, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ServiceAccount account = ServiceAccount.From(token);
        foreach (string candidate in ZoneNames.Candidates(domain))
        {
            Result<DnsHostResponse> found = await SendAsync(HttpMethod.Get, $"{Zones(account)}?dnsName={Uri.EscapeDataString(candidate + ".")}", token, null, cancellationToken).ConfigureAwait(false);
            if (!found.IsSuccess)
            {
                return found.Error;
            }

            // A private zone may carry the same name; only a public one is the domain's.
            JsonObject? zone = (JsonNode.Parse(found.Value.Body)?["managedZones"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(z =>
                ZoneNames.Plain(z["dnsName"]?.GetValue<string>() ?? string.Empty) == candidate
                && (z["visibility"]?.GetValue<string>() ?? "public") == "public");
            if (zone?["name"]?.GetValue<string>() is { } name)
            {
                return new DnsZone(name, candidate);
            }
        }

        return Error.NotFound($"The service account reaches no public Cloud DNS zone for {domain} in project {account.Project}.");
    }

    public async Task<Result<IReadOnlyList<ZoneRecord>>> ListRecordsAsync(DnsProviderToken token, DnsZone zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(zone);

        string sets = $"{Zones(ServiceAccount.From(token))}/{Uri.EscapeDataString(zone.Id)}/rrsets?maxResults={PageSize}";
        var records = new List<ZoneRecord>();
        string? page = null;
        do
        {
            Result<DnsHostResponse> listed = await SendAsync(HttpMethod.Get, page is null ? sets : $"{sets}&pageToken={Uri.EscapeDataString(page)}", token, null, cancellationToken).ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                return listed.Error;
            }

            JsonNode? json = JsonNode.Parse(listed.Value.Body);
            if (json?["rrsets"] is JsonArray items)
            {
                records.AddRange(items.OfType<JsonObject>().SelectMany(ToZoneRecords));
            }

            if (records.Count > options.Value.MaxRecords)
            {
                return Error.BackendUnavailable($"The {zone.Name} zone has more than {options.Value.MaxRecords} records (DnsHosts:MaxRecords); publish by hand.");
            }

            page = json?["nextPageToken"]?.GetValue<string>();
        }
        while (!string.IsNullOrEmpty(page));

        return records;
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

        string sets = $"{Zones(ServiceAccount.From(token))}/{Uri.EscapeDataString(zone.Id)}/rrsets";
        Result<DnsHostResponse> read = await SendAsync(HttpMethod.Get, $"{sets}?name={Uri.EscapeDataString(name)}&type={type}", token, null, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        JsonObject? existing = (JsonNode.Parse(read.Value.Body)?["rrsets"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(s => ZoneNames.Plain(s["name"]?.GetValue<string>() ?? string.Empty) == ZoneNames.Plain(name) && s["type"]?.GetValue<string>() == type);
        List<string> values = [.. Values(existing).Where(v => v != remove)];
        if (add is not null && !values.Contains(add))
        {
            values.Add(add);
        }

        string one = $"{sets}/{Uri.EscapeDataString(name)}/{type}";
        Result<DnsHostResponse> written;
        if (values.Count == 0)
        {
            if (existing is null)
            {
                return Result.Success();
            }

            written = await SendAsync(HttpMethod.Delete, one, token, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            int ttl = existing?["ttl"] is JsonValue t && t.TryGetValue(out int seconds) ? seconds : ZoneNames.Ttl(change.Desired?.Ttl, 3600, 0, 2_147_483_647);
            var body = new JsonObject
            {
                ["name"] = name,
                ["type"] = type,
                ["ttl"] = ttl,
                ["rrdatas"] = new JsonArray([.. values.Select(static v => (JsonNode)JsonValue.Create(v))]),
            };
            written = existing is null
                ? await SendAsync(HttpMethod.Post, sets, token, body, cancellationToken).ConfigureAwait(false)
                : await SendAsync(HttpMethod.Patch, one, token, body, cancellationToken).ConfigureAwait(false);
        }

        return written.IsSuccess ? Result.Success() : written.Error;
    }

    internal static IEnumerable<ZoneRecord> ToZoneRecords(JsonObject set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set["routingPolicy"] is not null || set["name"]?.GetValue<string>() is not { } name || set["type"]?.GetValue<string>() is not { } type)
        {
            yield break;
        }

        string fqdn = ZoneNames.Plain(name);
        int? ttl = set["ttl"] is JsonValue t && t.TryGetValue(out int seconds) ? seconds : null;
        foreach (string value in Values(set))
        {
            yield return new ZoneRecord(ZoneNames.RecordId(fqdn, type, value), new DnsRecord(fqdn, type, ttl, value));
        }
    }

    private static IEnumerable<string> Values(JsonObject? set) =>
        set?["rrdatas"] is JsonArray values ? values.Select(static v => v?.GetValue<string>()).OfType<string>() : [];

    private static string Zones(ServiceAccount account) => $"projects/{Uri.EscapeDataString(account.Project)}/managedZones";

    private async Task<Result<DnsHostResponse>> SendAsync(HttpMethod method, string path, DnsProviderToken token, JsonObject? body, CancellationToken cancellationToken)
    {
        Result<string> access = await tokens.GetAsync(Provider, token, ct => SignInAsync(ServiceAccount.From(token), ct), cancellationToken).ConfigureAwait(false);
        if (!access.IsSuccess)
        {
            return access.Error;
        }

        using var request = new HttpRequestMessage(method, new Uri(options.Value.GoogleDnsBaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Value);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(Provider, request, Fault, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A JWT signed with the key (RS256), exchanged at <see cref="DnsHostsOptions.GoogleTokenUrl"/>.</summary>
    private async Task<Result<(string Token, TimeSpan Lifetime)>> SignInAsync(ServiceAccount account, CancellationToken cancellationToken)
    {
        string? assertion = Assertion(account, options.Value.GoogleTokenUrl, clock.GetUtcNow());
        if (assertion is null)
        {
            return Error.Validation("The service account's private key could not be read. Paste the JSON key again.", "dnsToken");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.GoogleTokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            }),
        };
        Result<DnsHostResponse> issued = await client.SendAsync(Provider, request, Fault, cancellationToken).ConfigureAwait(false);
        return issued.IsSuccess ? AzureDnsHosting.AccessToken(issued.Value.Body) : issued.Error;
    }

    /// <summary>The signed JWT asking for Cloud DNS access for an hour; null when the key is not a readable RSA key.</summary>
    internal static string? Assertion(ServiceAccount account, Uri audience, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(audience);
        var header = new JsonObject { ["alg"] = "RS256", ["typ"] = "JWT" };
        if (account.KeyId is { Length: > 0 } keyId)
        {
            header["kid"] = keyId;
        }

        long issued = now.ToUnixTimeSeconds();
        var claims = new JsonObject
        {
            ["iss"] = account.Email,
            ["scope"] = Scope,
            ["aud"] = audience.ToString(),
            ["iat"] = issued,
            ["exp"] = issued + 3600,
        };
        string signed = $"{Base64Url(Encoding.UTF8.GetBytes(header.ToJsonString()))}.{Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()))}";
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(account.PrivateKey);
            byte[] signature = rsa.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return $"{signed}.{Base64Url(signature)}";
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary><c>{"error":{"code":403,"message","status":"PERMISSION_DENIED"}}</c>, or the token endpoint's <c>{"error":"invalid_grant"}</c>.</summary>
    internal static DnsHostFault Fault(string body)
    {
        JsonNode? json = JsonNode.Parse(body);
        if (json?["error"] is JsonValue oauth && oauth.TryGetValue(out string? oauthCode))
        {
            return new DnsHostFault(json["error_description"]?.GetValue<string>() ?? oauthCode, TokenRefused: oauthCode is "invalid_grant" or "invalid_client" or "unauthorized_client");
        }

        JsonNode? error = json?["error"];
        string? status = error?["status"]?.GetValue<string>();
        return new DnsHostFault(error?["message"]?.GetValue<string>(), TokenRefused: status is "UNAUTHENTICATED" or "PERMISSION_DENIED", Throttled: status == "RESOURCE_EXHAUSTED");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The key as the console keeps it (<see cref="DnsProviderToken"/>).</summary>
    internal sealed record ServiceAccount(string Project, string Email, string? KeyId, string PrivateKey)
    {
        public static ServiceAccount From(DnsProviderToken token)
        {
            JsonNode key = JsonNode.Parse(token.Value) ?? throw new ArgumentException("Not a service account key.", nameof(token));
            return new ServiceAccount(
                key["project_id"]?.GetValue<string>() ?? throw new ArgumentException("No project_id.", nameof(token)),
                key["client_email"]?.GetValue<string>() ?? throw new ArgumentException("No client_email.", nameof(token)),
                key["private_key_id"]?.GetValue<string>(),
                key["private_key"]?.GetValue<string>() ?? throw new ArgumentException("No private_key.", nameof(token)));
        }
    }
}
