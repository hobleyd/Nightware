using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Administration;

/// <summary>
/// <see cref="IDnsHosting"/> over Hetzner's Cloud API (<c>zones</c> and their record sets; Hetzner's separate DNS API
/// closed in May 2026) with a project's Read &amp; Write token (Bearer). A zone is found by exact name
/// (<c>GET zones?name=</c>) for the domain and then each parent; its record sets are read page by page and each value
/// becomes a <see cref="ZoneRecord"/> (<see cref="ZoneNames.RecordId"/>). A change adds, replaces or removes one value
/// with the record set's actions (<c>add_records</c>, <c>set_records</c>, <c>remove_records</c>), so the rest of the
/// set is never rewritten. Values are in zone-file form (quoted TXT, fully qualified host names).
/// </summary>
internal sealed class HetznerDnsHosting(IDnsHostClient client, IOptions<DnsHostsOptions> options) : IDnsHosting
{
    private const int PageSize = 100;

    public DnsProvider Provider => DnsProvider.Hetzner;

    public async Task<Result<DnsZone>> FindZoneAsync(DnsProviderToken token, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        foreach (string candidate in ZoneNames.Candidates(domain))
        {
            Result<DnsHostResponse> found = await SendAsync(HttpMethod.Get, $"zones?name={Uri.EscapeDataString(candidate)}", token, null, cancellationToken).ConfigureAwait(false);
            if (!found.IsSuccess)
            {
                return found.Error;
            }

            if (JsonNode.Parse(found.Value.Body)?["zones"] is JsonArray { Count: > 0 } zones && zones[0] is JsonObject zone && zone["id"] is JsonValue id)
            {
                return new DnsZone(id.ToString(), zone["name"]?.GetValue<string>() ?? candidate);
            }
        }

        return Error.NotFound($"The token reaches no Hetzner zone for {domain}.");
    }

    public async Task<Result<IReadOnlyList<ZoneRecord>>> ListRecordsAsync(DnsProviderToken token, DnsZone zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(zone);

        var records = new List<ZoneRecord>();
        for (int page = 1; ; page++)
        {
            Result<DnsHostResponse> listed = await SendAsync(
                    HttpMethod.Get, $"zones/{Uri.EscapeDataString(zone.Id)}/rrsets?per_page={PageSize}&page={page}", token, null, cancellationToken)
                .ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                return listed.Error;
            }

            JsonNode? json = JsonNode.Parse(listed.Value.Body);
            if (json?["rrsets"] is JsonArray sets)
            {
                records.AddRange(sets.OfType<JsonObject>().SelectMany(s => ToZoneRecords(s, zone.Name)));
            }

            if (records.Count > options.Value.MaxRecords)
            {
                return Error.BackendUnavailable($"The {zone.Name} zone has more than {options.Value.MaxRecords} records (DnsHosts:MaxRecords); publish by hand.");
            }

            if (json?["meta"]?["pagination"]?["next_page"] is not JsonValue next || !next.TryGetValue(out int nextPage) || nextPage <= page)
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

        DnsRecord record = change.Desired ?? change.Current?.Record ?? throw new ArgumentException("A change names a record.", nameof(change));
        string set = $"zones/{Uri.EscapeDataString(zone.Id)}/rrsets/{Uri.EscapeDataString(ZoneNames.Relative(record.Name, zone.Name))}/{record.Type.Trim().ToUpperInvariant()}";
        Result<DnsHostResponse> sent = change switch
        {
            { Kind: DnsChangeKind.Create, Desired: { } desired } =>
                await SendAsync(HttpMethod.Post, $"{set}/actions/add_records", token, Values([ZoneNames.Presentation(desired)]), cancellationToken).ConfigureAwait(false),
            { Kind: DnsChangeKind.Update, Desired: { } desired, Current: { } current } =>
                await ReplaceAsync(token, set, current.Record.Value, ZoneNames.Presentation(desired), cancellationToken).ConfigureAwait(false),
            { Kind: DnsChangeKind.Remove, Current: { } current } =>
                await SendAsync(HttpMethod.Post, $"{set}/actions/remove_records", token, Values([current.Record.Value]), cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"{change.Kind} is not a change to write.", nameof(change)),
        };
        return sent.IsSuccess ? Result.Success() : sent.Error;
    }

    /// <summary>The set's values with <paramref name="old"/> replaced by <paramref name="replacement"/>, written in one action.</summary>
    private async Task<Result<DnsHostResponse>> ReplaceAsync(DnsProviderToken token, string set, string old, string replacement, CancellationToken cancellationToken)
    {
        Result<DnsHostResponse> read = await SendAsync(HttpMethod.Get, set, token, null, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        // The other values keep their comments.
        JsonArray kept = [];
        if (JsonNode.Parse(read.Value.Body)?["rrset"]?["records"] is JsonArray records)
        {
            foreach (JsonObject record in records.OfType<JsonObject>().Where(r => r["value"]?.GetValue<string>() is { } v && v != old && v != replacement))
            {
                kept.Add(record.DeepClone());
            }
        }

        kept.Add(new JsonObject { ["value"] = replacement, ["comment"] = ZoneNames.Comment });
        return await SendAsync(HttpMethod.Post, $"{set}/actions/set_records", token, new JsonObject { ["records"] = kept }, cancellationToken).ConfigureAwait(false);
    }

    internal static IEnumerable<ZoneRecord> ToZoneRecords(JsonObject set, string zone)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set["name"]?.GetValue<string>() is not { } name || set["type"]?.GetValue<string>() is not { } type)
        {
            yield break;
        }

        string fqdn = ZoneNames.Absolute(name, zone);
        int? ttl = set["ttl"] is JsonValue t && t.TryGetValue(out int seconds) ? seconds : null;
        foreach (string value in Values(set))
        {
            yield return new ZoneRecord(ZoneNames.RecordId(fqdn, type, value), new DnsRecord(fqdn, type, ttl, value));
        }
    }

    private static IEnumerable<string> Values(JsonObject? set) =>
        set?["records"] is JsonArray records
            ? records.OfType<JsonObject>().Select(static r => r["value"]?.GetValue<string>()).OfType<string>()
            : [];

    /// <summary>
    /// The body of an action on a set's values. No TTL: <c>add_records</c> refuses one that differs from the set's, and
    /// a new set takes the zone's default.
    /// </summary>
    private static JsonObject Values(IEnumerable<string> values) => new()
    {
        ["records"] = new JsonArray([.. values.Select(static v => (JsonNode)new JsonObject { ["value"] = v, ["comment"] = ZoneNames.Comment })]),
    };

    private Task<Result<DnsHostResponse>> SendAsync(HttpMethod method, string path, DnsProviderToken token, JsonObject? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(options.Value.HetznerApiBaseUrl, path));
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

    /// <summary><c>{"error":{"code":"unauthorized","message":"…"}}</c>.</summary>
    internal static DnsHostFault Fault(string body)
    {
        JsonNode? error = JsonNode.Parse(body)?["error"];
        string? code = error?["code"]?.GetValue<string>();
        return new DnsHostFault(error?["message"]?.GetValue<string>(), TokenRefused: code is "unauthorized" or "forbidden", Throttled: code == "rate_limit_exceeded");
    }
}
