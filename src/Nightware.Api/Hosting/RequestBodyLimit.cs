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

    /// <summary>
    /// Largest body on a <see cref="LargeUpload"/> route (OneDrive simple upload), in bytes: Graph's 250 MB, which
    /// the drive's own check enforces too. The body streams into the backend, so only these routes pay for it.
    /// </summary>
    public long MaxLargeUploadBytes { get; set; } = 250L * 1024 * 1024;
}

/// <summary>
/// Endpoint metadata for a route that takes a raw upload larger than <see cref="RequestLimitOptions.MaxRequestBodyBytes"/>
/// (a drive item's <c>PUT …/content</c>): its body may be up to <see cref="RequestLimitOptions.MaxLargeUploadBytes"/>,
/// and it gets <see cref="RequestBudgetOptions.LargeUpload"/> to arrive.
/// </summary>
public sealed class LargeUpload
{
    public static readonly LargeUpload Instance = new();

    private LargeUpload()
    {
    }
}

internal sealed class RequestLimitOptionsValidator : IValidateOptions<RequestLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RequestLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxRequestBodyBytes < 1)
        {
            return ValidateOptionsResult.Fail("Limits:MaxRequestBodyBytes must be positive.");
        }

        return options.MaxLargeUploadBytes < options.MaxRequestBodyBytes
            ? ValidateOptionsResult.Fail("Limits:MaxLargeUploadBytes must be at least Limits:MaxRequestBodyBytes.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Enforces <see cref="RequestLimitOptions.MaxRequestBodyBytes"/> (on a <see cref="LargeUpload"/> route,
/// <see cref="RequestLimitOptions.MaxLargeUploadBytes"/>) on every request, whatever the
/// server: a declared <c>Content-Length</c> over the limit is refused before the body is read, and
/// the server's own limit (Kestrel's <see cref="IHttpMaxRequestBodySizeFeature"/>) is set to the
/// same value so a chunked body fails while streaming. Either way the caller gets Graph's
/// <c>413 requestEntityTooLarge</c> envelope (the streaming case via
/// <see cref="GraphExceptionMiddleware"/>).
/// </summary>
public sealed class RequestBodyLimitMiddleware(RequestDelegate next, IOptions<RequestLimitOptions> options)
{
    private readonly long _limit = options.Value.MaxRequestBodyBytes;
    private readonly long _largeLimit = options.Value.MaxLargeUploadBytes;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limit = context.GetEndpoint()?.Metadata.GetMetadata<LargeUpload>() is null ? _limit : _largeLimit;
        if (context.Request.ContentLength > limit)
        {
            return GraphErrorWriter.WriteAsync(context, new Error(
                ErrorKind.PayloadTooLarge,
                "requestEntityTooLarge",
                string.Create(CultureInfo.InvariantCulture, $"The request body exceeds the maximum of {limit} bytes.")));
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
        {
            feature.MaxRequestBodySize = limit;
        }

        return next(context);
    }
}
