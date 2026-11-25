using System.Globalization;
using System.Text.Json;

using Microsoft.Extensions.Logging;

using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Application.Administration.Reports;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>
/// The DMARC aggregate and TLS reports Stalwart received and analysed: <c>x:DmarcExternalReport</c> and
/// <c>x:TlsExternalReport</c>, queried and read in one request (permissions <c>sys…ExternalReportQuery</c> and
/// <c>…Get</c>). Shapes verified on 1.1.0 by mailing it real reports: lists are maps keyed by index, sets maps of
/// member to <c>true</c>, enums camelCase (<c>noPolicyFound</c>, <c>softFail</c>). A report missing what the page
/// cannot do without (its period) is skipped and logged rather than failing the list.
/// </summary>
internal sealed partial class StalwartMailReports(IStalwartManagementClient client, ILogger<StalwartMailReports> logger) : IMailReports
{
    /// <summary>The most reports of each kind read: Stalwart answers its newest first and keeps them 30 days by default.</summary>
    internal const int Limit = 250;

    private static readonly string[] Properties = ["receivedAt", "report"];

    public async Task<Result<MailReports>> ListAsync(CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var dmarcQuery = request.Add(StalwartMethods.DmarcExternalReportQuery, new StalwartQueryRequest<object> { Limit = Limit });
        var dmarcGet = request.Add(StalwartMethods.DmarcExternalReportGet, new StalwartGetRequest { Properties = Properties }, dmarcQuery.Ref("ids", JmapPaths.Ids));
        var tlsQuery = request.Add(StalwartMethods.TlsExternalReportQuery, new StalwartQueryRequest<object> { Limit = Limit });
        var tlsGet = request.Add(StalwartMethods.TlsExternalReportGet, new StalwartGetRequest { Properties = Properties }, tlsQuery.Ref("ids", JmapPaths.Ids));

        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        // A failed query makes its get fail on the reference; report the query's own error (a missing permission, say).
        Result<GetResponse<StalwartExternalReport>> dmarc = response.Value.Get(dmarcQuery).Bind(_ => response.Value.Get(dmarcGet));
        if (!dmarc.IsSuccess)
        {
            return dmarc.Error;
        }

        Result<GetResponse<StalwartExternalReport>> tls = response.Value.Get(tlsQuery).Bind(_ => response.Value.Get(tlsGet));
        if (!tls.IsSuccess)
        {
            return tls.Error;
        }

        var dmarcReports = new List<DmarcAggregateReport>();
        foreach (StalwartExternalReport held in dmarc.Value.List)
        {
            if (ReadDmarc(held) is { } report)
            {
                dmarcReports.Add(report);
            }
            else
            {
                LogUnreadable(logger, "DMARC", held.Id);
            }
        }

        var tlsReports = new List<TlsReport>();
        foreach (StalwartExternalReport held in tls.Value.List)
        {
            if (ReadTls(held) is { } report)
            {
                tlsReports.Add(report);
            }
            else
            {
                LogUnreadable(logger, "TLS", held.Id);
            }
        }

        return new MailReports(dmarcReports, tlsReports);
    }

    internal static DmarcAggregateReport? ReadDmarc(StalwartExternalReport held)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (held.Report is not { ValueKind: JsonValueKind.Object } r
            || Date(r, "dateRangeStart") is not { } start || Date(r, "dateRangeEnd") is not { } end)
        {
            return null;
        }

        var policy = new DmarcPublishedPolicy(
            Text(r, "policyDisposition") ?? "unspecified",
            Text(r, "policySubdomainDisposition") ?? "unspecified",
            Text(r, "policyAdkim") ?? "unspecified",
            Text(r, "policyAspf") ?? "unspecified",
            r.TryGetProperty("policyTestingMode", out JsonElement testing) && testing.ValueKind == JsonValueKind.True);

        List<DmarcSource> sources = [.. Items(r, "records").Select(static row => new DmarcSource(
            Text(row, "sourceIp"),
            Number(row, "count"),
            Text(row, "evaluatedDisposition") ?? "unspecified",
            Verdict(Text(row, "evaluatedDkim")),
            Verdict(Text(row, "evaluatedSpf")),
            Text(row, "headerFrom"),
            Text(row, "envelopeFrom"),
            [.. Items(row, "dkimResults").Select(static c => new AuthenticationCheck(Text(c, "domain") ?? string.Empty, Text(c, "selector"), Text(c, "result") ?? "none"))],
            [.. Items(row, "spfResults").Select(static c => new AuthenticationCheck(Text(c, "domain") ?? string.Empty, Text(c, "scope"), Text(c, "result") ?? "none"))]))];

        return new DmarcAggregateReport(
            held.Id,
            Text(r, "orgName") ?? "Unknown reporter",
            Text(r, "reportId") ?? string.Empty,
            Text(r, "policyDomain") ?? string.Empty,
            start,
            end,
            held.ReceivedAt,
            policy,
            sources);
    }

    internal static TlsReport? ReadTls(StalwartExternalReport held)
    {
        ArgumentNullException.ThrowIfNull(held);
        if (held.Report is not { ValueKind: JsonValueKind.Object } r
            || Date(r, "dateRangeStart") is not { } start || Date(r, "dateRangeEnd") is not { } end)
        {
            return null;
        }

        List<TlsPolicyResult> policies = [.. Items(r, "policies").Select(static p => new TlsPolicyResult(
            Text(p, "policyType") ?? "other",
            Text(p, "policyDomain") ?? string.Empty,
            p.TryGetProperty("mxHosts", out JsonElement mx) ? StalwartMethods.ReadSet(mx) : [],
            Number(p, "totalSuccessfulSessions"),
            Number(p, "totalFailedSessions"),
            [.. Items(p, "failureDetails").Select(static f => new TlsFailure(
                Text(f, "resultType") ?? "other",
                Text(f, "sendingMtaIp"),
                Text(f, "receivingMxHostname"),
                Text(f, "receivingIp"),
                Number(f, "failedSessionCount"),
                Text(f, "additionalInformation") ?? Text(f, "failureReasonCode")))]))];

        return new TlsReport(
            held.Id,
            Text(r, "organizationName") ?? "Unknown reporter",
            Text(r, "reportId") ?? string.Empty,
            start,
            end,
            held.ReceivedAt,
            policies);
    }

    private static DmarcVerdict Verdict(string? value) => value switch
    {
        "pass" => DmarcVerdict.Pass,
        "fail" => DmarcVerdict.Fail,
        _ => DmarcVerdict.Unspecified,
    };

    /// <summary>A list's items whether Stalwart sends an index-keyed map (its wire form) or an array, in index order.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement list))
        {
            return [];
        }

        return list.ValueKind switch
        {
            JsonValueKind.Array => [.. list.EnumerateArray().Where(static e => e.ValueKind == JsonValueKind.Object)],
            JsonValueKind.Object => [.. list.EnumerateObject()
                .Where(static p => p.Value.ValueKind == JsonValueKind.Object)
                .OrderBy(static p => int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int i) ? i : int.MaxValue)
                .ThenBy(static p => p.Name, StringComparer.Ordinal)
                .Select(static p => p.Value)],
            _ => [],
        };
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static long Number(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) && n > 0 ? n : 0;

    private static DateTimeOffset? Date(JsonElement parent, string name) =>
        Text(parent, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset at) ? at : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped an unreadable {Kind} report {Id} from the mail server")]
    private static partial void LogUnreadable(ILogger logger, string kind, string id);
}
