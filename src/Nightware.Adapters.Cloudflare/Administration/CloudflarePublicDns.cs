using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Application.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Cloudflare.Administration;

/// <summary>
/// <see cref="IPublicDns"/> over Cloudflare's DNS-over-HTTPS JSON API: the host's A and AAAA answers as the public
/// internet sees them, not as the deployment's own resolver does. Any failure (timeout, refusal, a name that does
/// not exist) is BackendUnavailable or NotFound for the caller to treat as "not known".
/// </summary>
internal sealed partial class CloudflarePublicDns(HttpClient http, IOptions<CloudflareOptions> options, ILogger<CloudflarePublicDns> logger) : IPublicDns
{
    private const int TypeA = 1;
    private const int TypeAaaa = 28;

    public async Task<Result<IReadOnlyList<IPAddress>>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        Result<IReadOnlyList<IPAddress>>[] answers = await Task.WhenAll(
            QueryAsync(host, "A", TypeA, cancellationToken),
            QueryAsync(host, "AAAA", TypeAaaa, cancellationToken)).ConfigureAwait(false);
        if (answers.All(static a => !a.IsSuccess))
        {
            return answers[0].Error!;
        }

        return answers.Where(static a => a.IsSuccess).SelectMany(static a => a.Value).Distinct().ToList();
    }

    private async Task<Result<IReadOnlyList<IPAddress>>> QueryAsync(string host, string type, int typeCode, CancellationToken cancellationToken)
    {
        var url = new Uri($"{options.Value.PublicDnsUrl}?name={Uri.EscapeDataString(host)}&type={type}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/dns-json");
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogFailed(logger, host, type, $"HTTP {(int)response.StatusCode}");
                return Error.BackendUnavailable($"Public DNS did not answer for {host}.");
            }

            JsonNode? body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            int status = body?["Status"]?.GetValue<int>() ?? -1;
            if (status == 3)
            {
                return Error.NotFound($"{host} does not exist in public DNS.");
            }

            if (status != 0)
            {
                LogFailed(logger, host, type, $"DNS status {status}");
                return Error.BackendUnavailable($"Public DNS could not resolve {host}.");
            }

            // CNAMEs come back as answers of their own type; only the addresses at the end of the chain count.
            return (body?["Answer"] as JsonArray ?? [])
                .Where(a => a?["type"]?.GetValue<int>() == typeCode)
                .Select(static a => IPAddress.TryParse(a?["data"]?.GetValue<string>(), out IPAddress? ip) ? ip : null)
                .OfType<IPAddress>()
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, host, type, ex.GetType().Name);
            return Error.BackendUnavailable($"Public DNS did not answer for {host}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Public DNS lookup of {Type} for {Host} failed: {Reason}.")]
    private static partial void LogFailed(ILogger logger, string host, string type, string reason);
}
