using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

using Nightware.Api.Graph.Errors;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Hosting;

/// <summary>Request size limits (configuration section <c>Limits</c>).</summary>
public sealed class RequestLimitOptions
{
    public const string SectionName = "Limits";

    /// <summary>
    /// Stalwart's default JMAP <c>maxSizeUpload</c> (50,000,000 bytes): the largest message or
    /// attachment blob the mail store accepts.
    /// </summary>
    public const long StalwartMaxSizeUpload = 50_000_000;

    /// <summary>
    /// Largest request body, in bytes. NightMail sends attachments inline as base64
    /// <c>contentBytes</c> (sendMail, draft attachments), which is 4/3 of the raw size, so the
    /// default admits a JSON body carrying a blob as large as Stalwart's <c>maxSizeUpload</c> plus
    /// room for the JSON around it. Raise both together; the backend still answers 413 for a blob
    /// over its own limit.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = (StalwartMaxSizeUpload * 4 / 3) + 1_000_000;
}

internal sealed class RequestLimitOptionsValidator : IValidateOptions<RequestLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RequestLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.MaxRequestBodyBytes < 1
            ? ValidateOptionsResult.Fail("Limits:MaxRequestBodyBytes must be positive.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Enforces <see cref="RequestLimitOptions.MaxRequestBodyBytes"/> on every request, whatever the
/// server: a declared <c>Content-Length</c> over the limit is refused before the body is read, and
/// the server's own limit (Kestrel's <see cref="IHttpMaxRequestBodySizeFeature"/>) is set to the
/// same value so a chunked body fails while streaming. Either way the caller gets Graph's
/// <c>413 requestEntityTooLarge</c> envelope (the streaming case via
/// <see cref="GraphExceptionMiddleware"/>).
/// </summary>
public sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<RequestLimitOptions> options)
{
    private readonly long _limit = options.Value.MaxRequestBodyBytes;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Request.ContentLength > _limit)
        {
            return GraphErrorWriter.WriteAsync(context, new Error(
                ErrorKind.PayloadTooLarge,
                "requestEntityTooLarge",
                string.Create(CultureInfo.InvariantCulture, $"The request body exceeds the maximum of {_limit} bytes.")));
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = _limit;
        }

        return next(context);
    }
}
