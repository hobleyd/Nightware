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
/// <see cref="IDnsHosting"/> over Gandi's LiveDNS v5 API with a personal access token (Bearer). A zone is a LiveDNS
/// domain: <c>GET domains/{fqdn}</c> for the domain and then each parent. Records are record sets
/// (<c>rrset_name</c> relative, <c>@</c> for the apex, with <c>rrset_values</c>), read page by page; each value becomes
/// a <see cref="ZoneRecord"/> (<see cref="ZoneNames.RecordId"/>). A change reads its one set, changes one value and
/// writes the set back (<c>PUT</c>), or deletes it when no value is left. Values are in zone-file form.
/// </summary>
internal sealed class GandiDnsHosting(IDnsHostClient client, IOptions<DnsHostsOptions> options) : IDnsHosting
{
    private const int PageSize = 500;

    /// <summary>Gandi's shortest TTL, and the TTL of a set it creates without one.</summary>
    private const int MinTtl = 300;

    private const int DefaultTtl = 10800;

    public DnsProvider Provider => DnsProvider.Gandi;

    public async Task<Result<DnsZone>> FindZoneAsync(DnsProviderToken token, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        // Gandi answers a domain the token may not see with 403, as it does a refused token: the parent may still
        // be reachable, so a refusal is reported only when no candidate is.
        Error? refused = null;
        foreach (string candidate in ZoneNames.Candidates(domain))
        {
            Result<DnsHostResponse> found = await SendAsync(HttpMethod.Get, $"domains/{Uri.EscapeDataString(candidate)}", token, null, cancellationToken).ConfigureAwait(false);
            if (found.IsSuccess)
            {
                string name = JsonNode.Parse(found.Value.Body)?["fqdn"]?.GetValue<string>() ?? candidate;
                return new DnsZone(name, name);
            }

            if (found.Error is { Kind: ErrorKind.Validation, Target: "dnsToken" })
            {
                refused ??= found.Error;
            }
            else if (found.Error.Kind != ErrorKind.NotFound)
            {
                return found.Error;
            }
        }

        return refused ?? Error.NotFound($"The token reaches no Gandi LiveDNS domain for {domain}.");
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

            JsonArray sets = JsonNode.Parse(listed.Value.Body) as JsonArray ?? [];
            records.AddRange(sets.OfType<JsonObject>().SelectMany(s => ToZoneRecords(s, zone.Name)));
            if (records.Count > options.Value.MaxRecords)
            {
                return Error.BackendUnavailable($"The {zone.Name} zone has more than {options.Value.MaxRecords} records (DnsHosts:MaxRecords); publish by hand.");
            }

            // Total-Count counts record sets; without it, a short page is the last.
            bool more = int.TryParse(listed.Value.Header("Total-Count"), NumberStyles.None, CultureInfo.InvariantCulture, out int total)
                ? page * PageSize < total
                : sets.Count == PageSize;
            if (!more || sets.Count == 0)
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
        string set = $"domains/{Uri.EscapeDataString(zone.Id)}/records/{Uri.EscapeDataString(ZoneNames.Relative(record.Name, zone.Name))}/{record.Type.Trim().ToUpperInvariant()}";
        (string? remove, string? add) = change switch
        {
            { Kind: DnsChangeKind.Create, Desired: { } desired } => ((string?)null, ZoneNames.Presentation(desired)),
            { Kind: DnsChangeKind.Update, Desired: { } desired, Current: { } held } => (held.Record.Value, ZoneNames.Presentation(desired)),
            { Kind: DnsChangeKind.Remove, Current: { } held } => (held.Record.Value, (string?)null),
            _ => throw new ArgumentException($"{change.Kind} is not a change to write.", nameof(change)),
        };

        Result<DnsHostResponse> read = await SendAsync(HttpMethod.Get, set, token, null, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess && read.Error.Kind != ErrorKind.NotFound)
        {
            return read.Error;
        }

        JsonObject? current = read.IsSuccess ? JsonNode.Parse(read.Value.Body) as JsonObject : null;
        List<string> values = [.. Values(current).Where(v => v != remove)];
        if (add is not null && !values.Contains(add))
        {
            values.Add(add);
        }

        Result<DnsHostResponse> written;
        if (values.Count == 0)
        {
            written = current is null ? read : await SendAsync(HttpMethod.Delete, set, token, null, cancellationToken).ConfigureAwait(false);
            if (!written.IsSuccess && written.Error.Kind == ErrorKind.NotFound)
            {
                return Result.Success();
            }
        }
        else
        {
            int? ttl = current?["rrset_ttl"] is JsonValue t && t.TryGetValue(out int seconds) ? seconds : null;
            var body = new JsonObject
            {
                ["rrset_values"] = new JsonArray([.. values.Select(static v => (JsonNode)JsonValue.Create(v))]),
                ["rrset_ttl"] = ZoneNames.Ttl(ttl ?? change.Desired?.Ttl, DefaultTtl, MinTtl, 2_592_000),
            };
            written = await SendAsync(HttpMethod.Put, set, token, body, cancellationToken).ConfigureAwait(false);
        }

        return written.IsSuccess ? Result.Success() : written.Error;
    }

    internal static IEnumerable<ZoneRecord> ToZoneRecords(JsonObject set, string zone)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set["rrset_name"]?.GetValue<string>() is not { } name || set["rrset_type"]?.GetValue<string>() is not { } type)
        {
            yield break;
        }

        string fqdn = ZoneNames.Absolute(name, zone);
        int? ttl = set["rrset_ttl"] is JsonValue t && t.TryGetValue(out int seconds) ? seconds : null;
        foreach (string value in Values(set))
        {
            yield return new ZoneRecord(ZoneNames.RecordId(fqdn, type, value), new DnsRecord(fqdn, type, ttl, value));
        }
    }

    private static IEnumerable<string> Values(JsonObject? set) =>
        set?["rrset_values"] is JsonArray values ? values.Select(static v => v?.GetValue<string>()).OfType<string>() : [];

    private Task<Result<DnsHostResponse>> SendAsync(HttpMethod method, string path, DnsProviderToken token, JsonObject? body, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, new Uri(options.Value.GandiApiBaseUrl, path));
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

    /// <summary><c>{"code":403,"message":"…","object":"…","cause":"Forbidden"}</c>.</summary>
    internal static DnsHostFault Fault(string body)
    {
        JsonNode? json = JsonNode.Parse(body);
        return new DnsHostFault(json?["message"]?.GetValue<string>() ?? json?["cause"]?.GetValue<string>());
    }
}
