using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>Stalwart domains (<c>x:Domain</c>), their DKIM keys and the DNS records they need (ADR 0002 D3, D4).</summary>
internal sealed partial class StalwartMailDomains(IStalwartManagementClient client, ILogger<StalwartMailDomains> logger) : IMailDomains
{
    public async Task<Result<MailDomainInfo>> FindAsync(MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);
        Result<StalwartDomain> found = await FindDomainAsync(client, domain.Name, ["name"], cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? await WithDkimAsync(domain, found.Value.Id, cancellationToken).ConfigureAwait(false) : found.Error;
    }

    public async Task<Result<MailDomainInfo>> CreateAsync(MailDomain domain, string description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);

        // dkimManagement / dnsManagement are typed objects in the v1 object reference
        // (https://stalw.art/docs/ref/object/domain/), not the bare strings ADR 0002 D4 sketches.
        var create = new JsonObject
        {
            ["name"] = domain.Name,
            ["description"] = description,
            ["dkimManagement"] = new JsonObject { ["@type"] = "Automatic" },
            ["dnsManagement"] = new JsonObject { ["@type"] = "Manual" },
        };

        // The reload rides in the same HTTP request (calls run in order), so a created domain normally loads
        // its DKIM keys straight away; when the reload fails, Repair sends it again (ReloadSettingsAsync).
        var request = new JmapRequestBuilder();
        var set = request.Add(StalwartMethods.DomainSet, new StalwartSetRequest { Create = new Dictionary<string, JsonObject> { ["d"] = create } });
        var reload = request.Add(StalwartMethods.ActionSet, ReloadSettings());
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        Result<StalwartCreated> created = response.Value.Get(set).Bind(r => r.Created("d"));
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        LogCreated(logger, domain.Name, created.Value.Id);
        Result<StalwartCreated> reloaded = response.Value.Get(reload).Bind(r => r.Created("r"));
        if (!reloaded.IsSuccess)
        {
            LogCreatedReloadFailed(logger, domain.Name, reloaded.Error.Kind);
            return Error.BackendUnavailable($"The mail server created {domain.Name} but did not reload its settings ({reloaded.Error.Message}); DKIM keys may be missing until it does.");
        }

        return await WithDkimAsync(domain, created.Value.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> ReloadSettingsAsync(CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var reload = request.Add(StalwartMethods.ActionSet, ReloadSettings());
        Result<StalwartCreated> reloaded = (await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
            .Bind(r => r.Get(reload)).Bind(r => r.Created("r"));
        if (!reloaded.IsSuccess)
        {
            LogReloadFailed(logger, reloaded.Error.Kind);
        }

        return reloaded.ToResult();
    }

    public async Task<Result<IReadOnlyList<DnsRecord>>> GetDnsRecordsAsync(MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);
        Result<StalwartDomain> found = await FindDomainAsync(client, domain.Name, ["name", "dnsZoneFile"], cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        IReadOnlyList<DnsRecord> records = ZoneFileParser.Parse(found.Value.DnsZoneFile ?? string.Empty, domain.Name, out int skipped);
        if (skipped > 0)
        {
            LogSkippedLines(logger, domain.Name, skipped);
        }

        return Result<IReadOnlyList<DnsRecord>>.Success(records);
    }

    /// <summary>
    /// The domain named exactly <paramref name="name"/> (case-insensitive). The <c>name</c> filter's
    /// matching rule is not documented, so every hit is checked here; NotFound when none matches.
    /// </summary>
    internal static async Task<Result<StalwartDomain>> FindDomainAsync(IStalwartManagementClient client, string name, IReadOnlyList<string> properties, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var query = request.Add(StalwartMethods.DomainQuery, new StalwartQueryRequest<DomainFilter> { Filter = new DomainFilter(name) });
        var get = request.Add(StalwartMethods.DomainGet, new StalwartGetRequest { Properties = properties }, query.Ref("ids", JmapPaths.Ids));
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        // A failed query makes the get fail on its reference; report the query's own error.
        Result<GetResponse<StalwartDomain>> domains = response.Value.Get(query).Bind(_ => response.Value.Get(get));
        if (!domains.IsSuccess)
        {
            return domains.Error;
        }

        StalwartDomain? match = domains.Value.List.FirstOrDefault(d => string.Equals(d.Name?.TrimEnd('.'), name, StringComparison.OrdinalIgnoreCase));
        return match is null ? Error.NotFound($"The mail server does not serve {name}.", name) : match;
    }

    internal static StalwartSetRequest ReloadSettings() =>
        new() { Create = new Dictionary<string, JsonObject> { ["r"] = new JsonObject { ["@type"] = "ReloadSettings" } } };

    private async Task<Result<MailDomainInfo>> WithDkimAsync(MailDomain domain, string domainId, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var query = request.Add(StalwartMethods.DkimSignatureQuery, new StalwartQueryRequest<DkimSignatureFilter> { Filter = new DkimSignatureFilter(domainId), Limit = 1 });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(query)).Map(q => new MailDomainInfo(domainId, domain, DkimPresent: q.Ids.Count > 0));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created Stalwart domain {Domain} ({DomainId})")]
    private static partial void LogCreated(ILogger logger, string domain, string domainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stalwart created domain {Domain} but ReloadSettings failed ({Kind})")]
    private static partial void LogCreatedReloadFailed(ILogger logger, string domain, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stalwart ReloadSettings failed ({Kind})")]
    private static partial void LogReloadFailed(ILogger logger, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped {Count} unreadable line(s) of Stalwart's DNS zone for {Domain}")]
    private static partial void LogSkippedLines(ILogger logger, string domain, int count);
}