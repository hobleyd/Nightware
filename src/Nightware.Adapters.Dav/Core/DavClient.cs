using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using Polly;
using Polly.CircuitBreaker;

namespace Nightware.Adapters.Dav.Core;

/// <summary>
/// The <see cref="IDavClient"/> implementation: a typed <see cref="HttpClient"/> resolved
/// from the request scope. It obtains the caller's credential once per instance through
/// <see cref="IBackendCredentials"/> and attaches it to each request for
/// <see cref="DavAuthenticationHandler"/>.
/// </summary>
public sealed class DavClient : IDavClient
{
    private const int MaxErrorBodyChars = 64 * 1024;
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod ReportMethod = new("REPORT");
    private static readonly HttpMethod MkColMethod = new("MKCOL");
    private static readonly HttpMethod MkCalendarMethod = new("MKCALENDAR");
    private static readonly HttpMethod MoveMethod = new("MOVE");
    private static readonly HttpMethod PropPatchMethod = new("PROPPATCH");

    private readonly HttpClient _http;
    private readonly IBackendCredentials _credentials;
    private readonly DavPaths _paths;
    private readonly TimeProvider _timeProvider;
    private Task<Result<BackendCredential>>? _credential;

    public DavClient(HttpClient http, IBackendCredentials credentials, DavPaths paths, TimeProvider timeProvider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<Result<DavMultistatus>> PropFindAsync(string href, DavDepth depth, IEnumerable<XName> properties, CancellationToken cancellationToken)
    {
        var body = DavRequestBodies.PropFind(properties);
        return await SendMultistatusAsync(PropFindMethod, href, depth, body, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<DavMultistatus>> ReportAsync(string href, DavDepth depth, XDocument report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        return SendMultistatusAsync(ReportMethod, href, depth, report, cancellationToken);
    }

    public async Task<Result<DavSyncResult>> SyncCollectionAsync(string collectionHref, string? syncToken, IEnumerable<XName> properties, int? limit, CancellationToken cancellationToken)
    {
        var body = DavRequestBodies.SyncCollection(syncToken, properties, limit);
        var result = await SendMultistatusAsync(ReportMethod, collectionHref, DavDepth.Zero, body, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        var multistatus = result.Value;
        if (multistatus.SyncToken is null)
        {
            return DavErrors.Unavailable("The groupware server did not return a sync token.");
        }

        var collectionPath = _paths.ToAbsolutePath(collectionHref)?.TrimEnd('/');
        var changed = new List<DavResponse>();
        var removed = new List<string>();
        var truncated = false;
        foreach (var response in multistatus.Responses)
        {
            if (string.Equals(response.Href.TrimEnd('/'), collectionPath, StringComparison.Ordinal))
            {
                // RFC 6578 §3.6: 507 on the request-URI means "truncated, ask again".
                truncated |= response.StatusCode == 507;
                continue;
            }

            if (response.StatusCode == 404)
            {
                removed.Add(response.Href);
            }
            else if (response.IsFound)
            {
                changed.Add(response);
            }
        }

        return new DavSyncResult(changed, removed, multistatus.SyncToken, truncated);
    }

    public async Task<Result<string>> FreeBusyQueryAsync(string calendarHref, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var body = DavRequestBodies.FreeBusyQuery(start, end);
        using var response = await SendAsync(ReportMethod, calendarHref, r =>
        {
            r.Headers.Add("Depth", "1");
            r.Content = XmlContent(body);
        }, DavRequestIntent.Default, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<DavResource>> GetAsync(string href, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, href, static _ => { }, DavRequestIntent.Default, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var message = response.Message;
        var body = await message.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new DavResource(
            message.RequestMessage?.RequestUri?.AbsolutePath ?? href,
            body,
            message.Headers.ETag?.ToString(),
            message.Content.Headers.ContentType?.ToString());
    }

    public async Task<Result<DavDownload>> OpenReadAsync(string href, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Get, href, static _ => { }, DavRequestIntent.Default, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var stream = await response.Message.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new DavDownload(response.Detach(), stream);
    }

    public Task<Result<DavWriteResult>> PutAsync(string href, string body, string contentType, DavPrecondition precondition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        return PutCoreAsync(href, () => WithType(new StringContent(body, Encoding.UTF8), contentType), precondition, cancellationToken);
    }

    public Task<Result<DavWriteResult>> PutAsync(string href, Stream content, string contentType, DavPrecondition precondition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        return PutCoreAsync(href, () => WithType(new StreamContent(content), contentType), precondition, cancellationToken);
    }

    public async Task<Result> DeleteAsync(string href, DavPrecondition precondition, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Delete, href, precondition.Apply, precondition.Intent, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return response.ToResult();
    }

    public async Task<Result> MkColAsync(string href, XDocument? body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(MkColMethod, href, r =>
        {
            if (body is not null)
            {
                r.Content = XmlContent(body);
            }
        }, DavRequestIntent.Default, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return response.ToResult();
    }

    public async Task<Result> MkCalendarAsync(string href, string? displayName, IEnumerable<string> components, string? color, CancellationToken cancellationToken)
    {
        var body = DavRequestBodies.MkCalendar(displayName, components, color);
        using var response = await SendAsync(MkCalendarMethod, href, r => r.Content = XmlContent(body), DavRequestIntent.Default, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return response.ToResult();
    }

    public async Task<Result> MoveAsync(string sourceHref, string destinationHref, bool overwrite, CancellationToken cancellationToken)
    {
        var destination = _paths.ToRequestUri(destinationHref);
        if (destination is null)
        {
            return OutsideRoot();
        }

        using var response = await SendAsync(MoveMethod, sourceHref, r =>
        {
            r.Headers.Add("Destination", destination.AbsoluteUri);
            r.Headers.Add("Overwrite", overwrite ? "T" : "F");
        }, overwrite ? DavRequestIntent.Default : DavRequestIntent.CreateOnly, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        return response.ToResult();
    }

    public async Task<Result<DavMultistatus>> PropPatchAsync(string href, IEnumerable<XElement> set, IEnumerable<XName> remove, CancellationToken cancellationToken)
    {
        var body = DavRequestBodies.PropPatch(set, remove);
        var result = await SendMultistatusAsync(PropPatchMethod, href, null, body, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            return result;
        }

        // PROPPATCH is atomic: any failed propstat means nothing was applied.
        var failed = result.Value.Responses.SelectMany(r => r.PropStats).FirstOrDefault(p => !p.IsSuccess && p.StatusCode != 424);
        return failed is null
            ? result
            : DavErrors.Map((HttpStatusCode)failed.StatusCode, null, null);
    }

    public async Task<Result<IReadOnlyList<DavScheduleResponseItem>>> PostToOutboxAsync(string outboxHref, string ics, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ics);
        using var response = await SendAsync(HttpMethod.Post, outboxHref, r =>
            r.Content = WithType(new StringContent(ics, Encoding.UTF8), "text/calendar; charset=utf-8"),
            DavRequestIntent.Default, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = DavXml.Load(text).Root;
            if (root is null || root.Name != DavNames.ScheduleResponse)
            {
                return DavErrors.Unavailable("The groupware server returned an unexpected scheduling response.");
            }

            IReadOnlyList<DavScheduleResponseItem> items = root.Elements(DavNames.ScheduleResponseItem)
                .Select(item => new DavScheduleResponseItem(
                    item.Element(DavNames.Recipient)?.Element(DavNames.Href)?.Value.Trim()
                        ?? item.Element(DavNames.Recipient)?.Value.Trim() ?? string.Empty,
                    item.Element(DavNames.RequestStatus)?.Value.Trim() ?? string.Empty,
                    item.Element(DavNames.CalendarData)?.Value))
                .ToList();
            return Result<IReadOnlyList<DavScheduleResponseItem>>.Success(items);
        }
        catch (XmlException)
        {
            return DavErrors.Unavailable("The groupware server returned a malformed scheduling response.");
        }
    }

    public async Task<Result<JsonElement>> OcsGetAsync(string path, IReadOnlyDictionary<string, string>? query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!DavHrefRules.IsSafeRelative(path))
        {
            return OutsideRoot();
        }

        var parameters = new Dictionary<string, string>(query ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["format"] = "json",
        };
        var queryString = string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var uri = new Uri(_paths.Origin, $"{_paths.OcsRootPath}{path}?{queryString}");

        using var response = await SendToAsync(HttpMethod.Get, uri, r =>
        {
            r.Headers.Add("OCS-APIRequest", "true");
            r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }, DavRequestIntent.Default, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("ocs", out var ocs))
            {
                return DavErrors.Unavailable("The groupware server returned an unexpected OCS response.");
            }

            if (ocs.TryGetProperty("meta", out var meta) && meta.TryGetProperty("statuscode", out var statusCode)
                && statusCode.TryGetInt32(out var code) && code is not (100 or 200))
            {
                // The request succeeded, so the credential is cached and its strategy known.
                var credential = await GetCredentialAsync(cancellationToken).ConfigureAwait(false);
                var strategy = credential.IsSuccess ? credential.Value.Strategy : CredentialStrategy.PassThrough;
                return DavErrors.Map((HttpStatusCode)(code == 998 ? 404 : code == 997 ? 401 : code), null, null, DavRequestIntent.Default, strategy);
            }

            return ocs.TryGetProperty("data", out var data) ? data.Clone() : default;
        }
        catch (JsonException)
        {
            return DavErrors.Unavailable("The groupware server returned malformed OCS JSON.");
        }
    }

    private async Task<Result<DavWriteResult>> PutCoreAsync(string href, Func<HttpContent> content, DavPrecondition precondition, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Put, href, r =>
        {
            r.Content = content();
            precondition.Apply(r);
        }, precondition.Intent, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var message = response.Message;
        return new DavWriteResult(
            message.RequestMessage?.RequestUri?.AbsolutePath ?? href,
            message.Headers.ETag?.ToString(),
            message.StatusCode == HttpStatusCode.Created);
    }

    private async Task<Result<DavMultistatus>> SendMultistatusAsync(HttpMethod method, string href, DavDepth? depth, XDocument body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, href, r =>
        {
            if (depth is { } d)
            {
                r.Headers.Add("Depth", d == DavDepth.Zero ? "0" : "1");
            }

            r.Content = XmlContent(body);
        }, DavRequestIntent.Default, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.Message.StatusCode != HttpStatusCode.MultiStatus && string.IsNullOrWhiteSpace(text))
        {
            return DavMultistatus.Empty;
        }

        try
        {
            return DavXml.ParseMultistatus(text);
        }
        catch (Exception ex) when (ex is XmlException or FormatException)
        {
            return DavErrors.Unavailable("The groupware server returned a malformed multistatus.");
        }
    }

    private async Task<Outcome> SendAsync(HttpMethod method, string href, Action<HttpRequestMessage> configure, DavRequestIntent intent, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var uri = _paths.ToRequestUri(href);
        return uri is null
            ? new Outcome(OutsideRoot())
            : await SendToAsync(method, uri, configure, intent, completion, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Outcome> SendToAsync(HttpMethod method, Uri uri, Action<HttpRequestMessage> configure, DavRequestIntent intent, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        var credential = await GetCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (!credential.IsSuccess)
        {
            return new Outcome(credential.Error);
        }

        using var request = new HttpRequestMessage(method, uri);
        request.Options.Set(DavRequestOptions.Credential, credential.Value);
        configure(request);

        HttpResponseMessage message;
        try
        {
            message = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException
                                       or ExecutionRejectedException or BrokenCircuitException)
        {
            return new Outcome(DavErrors.Unavailable("The groupware server did not respond."));
        }

        if (message.IsSuccessStatusCode)
        {
            return new Outcome(message);
        }

        using (message)
        {
            var body = await ReadErrorBodyAsync(message, cancellationToken).ConfigureAwait(false);
            return new Outcome(DavErrors.Map(message.StatusCode, body, DavErrors.RetryAfterSeconds(message, _timeProvider), intent, credential.Value.Strategy));
        }
    }

    private Task<Result<BackendCredential>> GetCredentialAsync(CancellationToken cancellationToken)
    {
        return _credential is { IsCompletedSuccessfully: true } cached
            ? cached
            : _credential = FetchAsync();

        async Task<Result<BackendCredential>> FetchAsync()
        {
            var result = await _credentials.ForAsync(Backend.Groupware, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? DavCredentialPolicy.Check(result.Value) : result;
        }
    }

    private static async Task<string?> ReadErrorBodyAsync(HttpResponseMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var text = await message.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return text.Length > MaxErrorBodyChars ? text[..MaxErrorBodyChars] : text;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static Error OutsideRoot() =>
        Error.Validation("The href does not resolve inside the DAV root.", "href");

    private static StringContent XmlContent(XDocument document)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = false, Indent = false, Encoding = Encoding.UTF8 }))
        {
            document.Save(writer);
        }

        // StringBuilder output declares utf-16; the bytes we send are UTF-8.
        var xml = builder.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
        return WithType(new StringContent(xml, Encoding.UTF8), "application/xml; charset=utf-8");
    }

    private static T WithType<T>(T content, string contentType)
        where T : HttpContent
    {
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    /// <summary>Either an error or a successful response that is disposed with the outcome unless detached.</summary>
    private sealed class Outcome : IDisposable
    {
        private HttpResponseMessage? _message;

        public Outcome(Error error) => Error = error;

        public Outcome(HttpResponseMessage message) => _message = message;

        public Error? Error { get; }

        [MemberNotNullWhen(false, nameof(Error))]
        public bool IsSuccess => Error is null;

        public HttpResponseMessage Message => _message ?? throw new InvalidOperationException("No response.");

        public HttpContent Content => Message.Content;

        public Result ToResult() => IsSuccess ? Result.Success() : Result.Failure(Error);

        public HttpResponseMessage Detach()
        {
            var detached = Message;
            _message = null;
            return detached;
        }

        public void Dispose() => _message?.Dispose();
    }
}
