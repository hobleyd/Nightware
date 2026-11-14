using System.Net;
using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Dav.Core;

/// <summary>Outcome of <see cref="DavHealthProbe.CheckAsync"/>.</summary>
public sealed record DavHealthStatus(bool IsHealthy, string Description, int? StatusCode = null);

/// <summary>
/// Anonymous liveness probe of the DAV root (tag: <c>backend</c>). Runs outside any user
/// context, so it sends no credential: a 207 or a 401 challenge both prove Sabre is up;
/// 5xx, 503 (maintenance mode) and timeouts are unhealthy.
/// </summary>
public sealed class DavHealthProbe
{
    public const string HttpClientName = "Nightware.Dav.Health";
    public const string Name = "dav";
    public const string Tag = "backend";

    private static readonly HttpMethod PropFind = new("PROPFIND");
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DavPaths _paths;
    private readonly DavOptions _options;

    public DavHealthProbe(IHttpClientFactory httpClientFactory, DavPaths paths, IOptions<DavOptions> options)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

    public async Task<DavHealthStatus> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.HealthCheckTimeout);
        using var request = new HttpRequestMessage(PropFind, _paths.RootUri);
        request.Headers.Add("Depth", "0");
        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            return response.StatusCode is HttpStatusCode.MultiStatus or HttpStatusCode.Unauthorized || response.IsSuccessStatusCode
                ? new DavHealthStatus(true, $"DAV root answered {code}.", code)
                : new DavHealthStatus(false, $"DAV root answered {code}.", code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new DavHealthStatus(false, "DAV root did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new DavHealthStatus(false, $"DAV root is unreachable: {ex.HttpRequestError}.");
        }
    }
}
