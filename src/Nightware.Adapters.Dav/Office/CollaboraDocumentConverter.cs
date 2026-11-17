using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Files;
using Nightware.Application.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Office;

/// <summary>
/// <see cref="IDocumentConverter"/> on Collabora Online's conversion API:
/// <c>POST {BaseUrl}cool/convert-to/pdf</c> with the document as the multipart field <c>data</c> (through
/// <c>proxy.php?req=</c> for the built-in CODE server, see <see cref="ConvertUri"/>).
/// </summary>
/// <remarks>
/// <para>Why in the Dav adapter: Collabora is Nextcloud Office's engine, deployed beside Nextcloud and only
/// ever used for documents this adapter reads. A project of its own would add a composition seam for one class.</para>
/// <para>Collabora detects the source format from the file name's extension, so the part is named
/// <c>document.{ext}</c> — never the user's file name, which may not survive a multipart header. The answer
/// is always <c>application/octet-stream</c>; it is labelled PDF here. Requests are never retried (a large
/// upload, and Collabora's failures are not transient), and the PDF is streamed back, not buffered.</para>
/// <para>Collabora answers 400 for a request it cannot read and 403 for a caller outside
/// <c>net.post_allow</c> — the latter is this deployment's misconfiguration, a 503 rather than the caller's fault.</para>
/// </remarks>
internal sealed partial class CollaboraDocumentConverter(
    HttpClient http,
    IOptions<CollaboraOptions> options,
    ILogger<CollaboraDocumentConverter> logger) : IDocumentConverter
{
    /// <summary>
    /// <c>{BaseUrl}cool/convert-to/pdf</c>, or for the built-in CODE server (Nextcloud's richdocumentscode app,
    /// whose <see cref="CollaboraOptions.BaseUrl"/> is its <c>proxy.php</c>) that path as <c>proxy.php</c>'s
    /// <c>req</c> parameter.
    /// </summary>
    internal static Uri ConvertUri(Uri baseUrl) =>
        baseUrl.AbsolutePath.EndsWith("/proxy.php", StringComparison.Ordinal)
            ? new Uri(baseUrl, $"{baseUrl.AbsolutePath}?req=/cool/convert-to/pdf")
            : new Uri(baseUrl, $"{baseUrl.AbsolutePath.TrimEnd('/')}/cool/convert-to/pdf");

    public bool CanConvertToPdf(string fileName, string? mimeType) =>
        options.Value.BaseUrl is not null && OfficeFormats.IsConvertible(fileName, mimeType);

    public async Task<Result<BlobContent>> ConvertToPdfAsync(BlobContent source, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fileName);
        var settings = options.Value;
        var baseUrl = settings.BaseUrl ?? throw new InvalidOperationException("Collabora:BaseUrl is not configured.");
        await using var document = await source.Open(cancellationToken).ConfigureAwait(false);
        if (source.Length > settings.MaxSourceBytes)
        {
            return new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", $"'{fileName}' is too large to convert.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Timeout);
        using var part = new StreamContent(document);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var form = new MultipartFormDataContent { { part, "data", PartName(fileName) } };
        using var request = new HttpRequestMessage(HttpMethod.Post, ConvertUri(baseUrl)) { Content = form };
        HttpResponseMessage? response = null;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var error = Map(response.StatusCode);
                LogFailed(logger, (int)response.StatusCode, error.Kind);
                response.Dispose();
                return error;
            }

            var pdf = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            Stream body = new ResponseStream(pdf, response);
            var length = response.Content.Headers.ContentLength;
            response = null;
            return new BlobContent("application/pdf", length, _ => Task.FromResult(body)) { FileName = StemOf(fileName) + ".pdf" };
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, ex.Message);
            return Error.BackendUnavailable("The document converter is unreachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, "timed out");
            return Error.BackendUnavailable("The document converter timed out.");
        }
        finally
        {
            response?.Dispose();
        }
    }

    internal static string PartName(string fileName) =>
        OfficeFormats.ExtensionOf(fileName) is { Length: > 0 } extension && extension.All(char.IsAsciiLetterOrDigit)
            ? "document." + extension.ToLowerInvariant()
            : "document";

    internal static string StemOf(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }

    private static Error Map(HttpStatusCode status) => (int)status switch
    {
        400 or 415 or 422 => new Error(ErrorKind.Validation, "notSupported", "The document could not be converted to PDF."),
        413 => new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", "The document is too large to convert."),
        429 => new Error(ErrorKind.Throttled, "activityLimitReached", "The document converter is busy."),
        _ => Error.BackendUnavailable($"The document converter failed ({(int)status})."),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collabora conversion failed: HTTP {Status} → {Kind}")]
    private static partial void LogFailed(ILogger logger, int status, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collabora unreachable: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string reason);
}
