using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Nightware.Api.Discovery;
using Nightware.Api.Graph.OData;

namespace Nightware.Api.Graph.Json;

/// <summary>
/// Builds absolute Graph URLs (<c>@odata.context</c>, <c>@odata.nextLink</c>, <c>@odata.deltaLink</c>).
/// </summary>
/// <remarks>
/// NightMail follows a link only when its scheme, host and port equal the account's
/// <c>graphBaseUrl</c>, so the root is the configured public base URL from <see cref="DiscoveryOptions"/>
/// (<c>GraphBaseUrl</c> for <c>/v1.0</c>, <c>GraphBetaBaseUrl</c> for <c>/beta</c>). Only when that is
/// not configured does it fall back to the request's own scheme/host/path base, which the
/// forwarded-headers middleware has already rewritten from <c>X-Forwarded-Proto/Host/Prefix</c>.
/// </remarks>
public sealed class GraphLinkBuilder(IOptions<DiscoveryOptions> discovery)
{
    /// <summary>The absolute version root for this request, without a trailing slash (e.g. <c>https://h/graph/v1.0</c>).</summary>
    public string VersionRoot(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var version = GraphPaths.VersionOf(request.Path);
        var configured = version == GraphPaths.Beta ? discovery.Value.GraphBetaBaseUrl : discovery.Value.GraphBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return $"{request.Scheme}://{request.Host.ToUriComponent()}{request.PathBase.ToUriComponent()}{version}";
    }

    /// <summary>The request path relative to its version root, e.g. <c>/me/mailFolders</c>.</summary>
    public static string RelativePath(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var version = GraphPaths.VersionOf(request.Path);
        return request.Path.StartsWithSegments(version, StringComparison.OrdinalIgnoreCase, out var rest)
            ? rest.ToUriComponent()
            : request.Path.ToUriComponent();
    }

    /// <summary><c>{root}/$metadata#{fragment}</c>.</summary>
    public string Context(HttpRequest request, string fragment) =>
        $"{VersionRoot(request)}/$metadata#{fragment}";

    /// <summary>An absolute link to this request's path with exactly the given query options.</summary>
    public string Link(HttpRequest request, IEnumerable<KeyValuePair<string, string>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var builder = new StringBuilder(VersionRoot(request)).Append(RelativePath(request));
        var separator = '?';
        foreach (var (key, value) in query)
        {
            builder.Append(separator).Append(EscapeKey(key)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return builder.ToString();
    }

    /// <summary>
    /// The next page of an ordinary collection: the request's own query options (so <c>$filter</c>,
    /// <c>$select</c>, <c>$top</c> carry over) with <c>$skip</c>/<c>$skiptoken</c> replaced by the
    /// adapter's continuation.
    /// </summary>
    public string NextLink(HttpRequest request, string skipToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(skipToken);
        var query = request.Query
            .Where(static q => !string.Equals(q.Key, ODataQueryParser.SkipOption, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(q.Key, ODataQueryParser.SkipTokenOption, StringComparison.OrdinalIgnoreCase))
            .Select(static q => new KeyValuePair<string, string>(q.Key, q.Value.ToString()))
            .Append(new(ODataQueryParser.SkipTokenOption, skipToken));
        return Link(request, query);
    }

    private static string EscapeKey(string key) =>
        key.StartsWith('$') ? "$" + Uri.EscapeDataString(key[1..]) : Uri.EscapeDataString(key);
}
