using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>
/// Stalwart's MTA-STS policy, the <c>x:MtaSts</c> singleton (permissions <c>sysMtaStsGet</c>, <c>sysMtaStsUpdate</c>).
/// Its <c>mxHosts</c> is a set, written as a map of host to <c>true</c>; while it is empty the policy lists the hosts of
/// <c>x:SystemSettings.mailExchangers</c>, a null hostname there being <c>defaultHostname</c>. The object reference does
/// not say whether a change needs a reload, so one rides in the same request, as it does for a new domain.
/// </summary>
internal sealed partial class StalwartMtaStsPolicies(IStalwartManagementClient client, ILogger<StalwartMtaStsPolicies> logger) : IMtaStsPolicies
{
    private const string Singleton = "singleton";

    public async Task<Result<MtaStsPolicy>> GetAsync(CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var policy = request.Add(StalwartMethods.MtaStsGet, new StalwartGetRequest { Ids = [Singleton], Properties = ["mode", "mxHosts"] });
        var settings = request.Add(StalwartMethods.SystemSettingsGet, new StalwartGetRequest { Ids = [Singleton], Properties = ["defaultHostname", "mailExchangers"] });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        Result<GetResponse<StalwartMtaSts>> read = response.Value.Get(policy);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        Result<GetResponse<StalwartSystemSettings>> system = response.Value.Get(settings);
        if (!system.IsSuccess)
        {
            return system.Error;
        }

        if (read.Value.List is not [var sts, ..])
        {
            return Error.BackendUnavailable("The mail server returned no MTA-STS settings.");
        }

        StalwartSystemSettings? server = system.Value.List is [var first, ..] ? first : null;
        return new MtaStsPolicy(sts.Mode ?? "testing", StalwartMethods.ReadSet(sts.MxHosts), DefaultHosts(server));
    }

    public async Task<Result> SetMxHostsAsync(IReadOnlyList<string> hosts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        var set = new JsonObject();
        foreach (string host in hosts)
        {
            set[host] = true;
        }

        var request = new JmapRequestBuilder();
        var update = request.Add(StalwartMethods.MtaStsSet, new StalwartSetRequest
        {
            Update = new Dictionary<string, JsonObject> { [Singleton] = new JsonObject { ["mxHosts"] = set } },
        });
        var reload = request.Add(StalwartMethods.ActionSet, StalwartMailDomains.ReloadSettings());
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        Result updated = response.Value.Get(update).Match(static r => r.Updated(Singleton), static e => Result.Failure(e));
        if (!updated.IsSuccess)
        {
            return updated;
        }

        LogUpdated(logger, hosts);
        Result reloaded = response.Value.Get(reload).Bind(static r => r.Created("r")).ToResult();
        if (!reloaded.IsSuccess)
        {
            // Saved: the settings load on the next reload, which adding or repairing a domain also sends.
            LogReloadFailed(logger, reloaded.Error.Kind);
        }

        return Result.Success();
    }

    /// <summary>The hosts of <c>mailExchangers</c> (a null one is <c>defaultHostname</c>); just <c>defaultHostname</c> when there are none.</summary>
    private static List<string> DefaultHosts(StalwartSystemSettings? server)
    {
        string? fallback = string.IsNullOrWhiteSpace(server?.DefaultHostname) ? null : server.DefaultHostname;
        var hosts = new List<string>();
        if (server?.MailExchangers is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } exchangers)
        {
            IEnumerable<JsonElement> entries = exchangers.ValueKind == JsonValueKind.Object
                ? exchangers.EnumerateObject().Select(static p => p.Value)
                : exchangers.EnumerateArray();
            foreach (JsonElement entry in entries)
            {
                string? host = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("hostname", out JsonElement name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : fallback;
                if (!string.IsNullOrWhiteSpace(host) && !hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                {
                    hosts.Add(host);
                }
            }
        }

        if (hosts.Count == 0 && fallback is not null)
        {
            hosts.Add(fallback);
        }

        return hosts;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Set Stalwart's MTA-STS policy MX hosts to {Hosts}")]
    private static partial void LogUpdated(ILogger logger, IReadOnlyList<string> hosts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Stalwart saved the MTA-STS MX hosts but ReloadSettings failed ({Kind})")]
    private static partial void LogReloadFailed(ILogger logger, ErrorKind kind);
}
