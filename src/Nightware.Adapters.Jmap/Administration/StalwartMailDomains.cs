using System.Text.Json;
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

    /// <summary>
    /// Stalwart refuses to destroy a domain while anything links to it (<c>objectIsLinked</c>, naming each linked
    /// object): its DKIM keys, and every account whose primary address or alias is on it (verified on 1.1.0). So the
    /// domain is destroyed alone first, and its keys go with it in a second request only when Stalwart names
    /// nothing else; the keys are never lost while an account still holds the domain. Each request reloads the
    /// settings, without which SMTP keeps treating the domain as local.
    /// </summary>
    public async Task<Result> DestroyAsync(MailDomain domain, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domain);
        Result<StalwartDomain> found = await FindDomainAsync(client, domain.Name, ["name"], cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        string id = found.Value.Id;
        Result<DestroyOutcome> outcome = await DestroyRequestAsync(id, [], cancellationToken).ConfigureAwait(false);
        if (outcome.IsSuccess && outcome.Value.Linked is { } links)
        {
            int others = links.Count(static l => l.Object != "DkimSignature");
            if (others > 0)
            {
                return Error.Conflict($"The mail server still has {others} account(s) with an address or alias on {domain.Name}; remove them first.", "domain");
            }

            outcome = await DestroyRequestAsync(id, [.. links.Select(static l => l.Id)], cancellationToken).ConfigureAwait(false);
        }

        if (!outcome.IsSuccess)
        {
            return outcome.Error;
        }

        if (!outcome.Value.Destroyed.IsSuccess)
        {
            return outcome.Value.Destroyed;
        }

        LogDestroyed(logger, domain.Name, id);
        if (!outcome.Value.Reloaded.IsSuccess)
        {
            LogDestroyedReloadFailed(logger, domain.Name, outcome.Value.Reloaded.Error.Kind);
            return Error.BackendUnavailable($"The mail server destroyed {domain.Name} but did not reload its settings ({outcome.Value.Reloaded.Error.Message}); it treats the domain as its own until it does.");
        }

        return Result.Success();
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

    /// <summary>Destroys <paramref name="dkimIds"/> (if any), then the domain, then reloads: one request, calls in order.</summary>
    private async Task<Result<DestroyOutcome>> DestroyRequestAsync(string domainId, IReadOnlyList<string> dkimIds, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        if (dkimIds.Count > 0)
        {
            request.Add(StalwartMethods.DkimSignatureSet, new StalwartSetRequest { Destroy = dkimIds });
        }

        var set = request.Add(StalwartMethods.DomainSet, new StalwartSetRequest { Destroy = [domainId] });
        var reload = request.Add(StalwartMethods.ActionSet, ReloadSettings());
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Result<SetResponse<StalwartCreated>> domainSet = response.Bind(r => r.Get(set));
        if (!domainSet.IsSuccess)
        {
            return domainSet.Error;
        }

        // Only the first request asks again: a second one that is still linked is reported as refused.
        IReadOnlyList<(string Object, string Id)>? linked = dkimIds.Count == 0 && domainSet.Value.NotDestroyed?.GetValueOrDefault(domainId) is { Type: "objectIsLinked" } refused
            ? LinkedObjects(refused)
            : null;
        Result reloaded = response.Value.Get(reload).Bind(r => r.Created("r")).ToResult();
        return new DestroyOutcome(domainSet.Value.Destroyed(domainId), linked, reloaded);
    }

    /// <summary>The <c>linkedObjects</c> of an <c>objectIsLinked</c> SetError (a Stalwart extension): <c>[{object, id}]</c>.</summary>
    private static List<(string Object, string Id)> LinkedObjects(SetError error)
    {
        var links = new List<(string Object, string Id)>();
        if (error.Extra?.TryGetValue("linkedObjects", out JsonElement list) == true && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("object", out JsonElement kind) && kind.ValueKind == JsonValueKind.String
                    && item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String)
                {
                    links.Add((kind.GetString()!, id.GetString()!));
                }
            }
        }

        return links;
    }

    /// <param name="Linked">What Stalwart named when it refused the first, domain-only destroy as <c>objectIsLinked</c>; otherwise null.</param>
    private sealed record DestroyOutcome(Result Destroyed, IReadOnlyList<(string Object, string Id)>? Linked, Result Reloaded);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Destroyed Stalwart domain {Domain} ({DomainId})")]
    private static partial void LogDestroyed(ILogger logger, string domain, string domainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stalwart destroyed domain {Domain} but ReloadSettings failed ({Kind})")]
    private static partial void LogDestroyedReloadFailed(ILogger logger, string domain, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stalwart ReloadSettings failed ({Kind})")]
    private static partial void LogReloadFailed(ILogger logger, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped {Count} unreadable line(s) of Stalwart's DNS zone for {Domain}")]
    private static partial void LogSkippedLines(ILogger logger, string domain, int count);
}