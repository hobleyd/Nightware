using System.Text.Json.Nodes;

using Microsoft.Extensions.Options;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Cloudflare.Administration;

/// <summary>
/// <see cref="IDnsHosting"/> over Cloudflare's v4 API: <c>GET zones?name=</c> for the domain and then each parent,
/// <c>GET zones/{id}/dns_records</c> page by page, and <c>POST</c>/<c>PATCH</c>/<c>DELETE</c> of one record per
/// change. A token scoped to "Zone / DNS / Edit" on a zone is enough to find and edit that zone.
/// </summary>
internal sealed class CloudflareDnsHosting(ICloudflareClient cloudflare, IOptions<CloudflareOptions> options) : IDnsHosting
{
    public async Task<Result<DnsZone>> FindZoneAsync(DnsProviderToken token, MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(domain);

        // acme.example for mail.acme.example; never a bare top-level domain.
        string[] labels = domain.Name.Split('.');
        for (int i = 0; i <= labels.Length - 2; i++)
        {
            string candidate = string.Join('.', labels[i..]);
            Result<CloudflareResponse> found = await cloudflare.SendAsync(
                    HttpMethod.Get, $"zones?name={Uri.EscapeDataString(candidate)}&per_page=5", token, null, cancellationToken)
                .ConfigureAwait(false);
            if (!found.IsSuccess)
            {
                return found.Error;
            }

            if (found.Value.Result is JsonArray { Count: > 0 } zones
                && zones[0] is JsonObject zone
                && zone["id"]?.GetValue<string>() is { } id)
            {
                return new DnsZone(id, zone["name"]?.GetValue<string>() ?? candidate);
            }
        }

        return Error.NotFound($"The token reaches no Cloudflare zone for {domain}.");
    }

    public async Task<Result<IReadOnlyList<ZoneRecord>>> ListRecordsAsync(DnsProviderToken token, DnsZone zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(zone);

        CloudflareOptions settings = options.Value;
        var records = new List<ZoneRecord>();
        for (int page = 1; ; page++)
        {
            Result<CloudflareResponse> listed = await cloudflare.SendAsync(
                    HttpMethod.Get, $"zones/{Uri.EscapeDataString(zone.Id)}/dns_records?per_page={settings.RecordPageSize}&page={page}", token, null, cancellationToken)
                .ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                return listed.Error;
            }

            if (listed.Value.Result is JsonArray items)
            {
                records.AddRange(items.OfType<JsonObject>().Select(CloudflareRecords.ToZoneRecord).OfType<ZoneRecord>());
            }

            if (records.Count > settings.MaxRecords)
            {
                return Error.BackendUnavailable($"The {zone.Name} zone has more than {settings.MaxRecords} records (Cloudflare:MaxRecords); publish by hand.");
            }

            if (page >= listed.Value.TotalPages)
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

        string records = $"zones/{Uri.EscapeDataString(zone.Id)}/dns_records";
        Result<CloudflareResponse> sent = change switch
        {
            { Kind: DnsChangeKind.Create, Desired: { } desired } =>
                await cloudflare.SendAsync(HttpMethod.Post, records, token, CloudflareRecords.ToBody(desired, create: true), cancellationToken).ConfigureAwait(false),
            { Kind: DnsChangeKind.Update, Desired: { } desired, Current: { } current } =>
                await cloudflare.SendAsync(HttpMethod.Patch, $"{records}/{Uri.EscapeDataString(current.Id)}", token, CloudflareRecords.ToBody(desired, create: false), cancellationToken).ConfigureAwait(false),
            { Kind: DnsChangeKind.Remove, Current: { } current } =>
                await cloudflare.SendAsync(HttpMethod.Delete, $"{records}/{Uri.EscapeDataString(current.Id)}", token, null, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentException($"{change.Kind} is not a change to write.", nameof(change)),
        };
        return sent.IsSuccess ? Result.Success() : sent.Error;
    }
}
