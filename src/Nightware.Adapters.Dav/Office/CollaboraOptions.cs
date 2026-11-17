using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Dav.Office;

/// <summary>
/// Configuration section <c>Collabora</c>: the Collabora Online (CODE) server behind Nextcloud Office, used to
/// render Office documents to PDF. Optional — without <see cref="BaseUrl"/> no document is offered as PDF.
/// </summary>
public sealed class CollaboraOptions
{
    public const string SectionName = "Collabora";

    /// <summary>
    /// Collabora's internal URL (e.g. <c>http://collabora:9980/</c>). Its <c>convert-to</c> endpoint answers only
    /// addresses in its <c>net.post_allow</c> list, so this must be a direct route, not a public proxy.
    /// For Nextcloud's built-in CODE server (the richdocumentscode app) it is that app's <c>proxy.php</c>,
    /// e.g. <c>https://cloud.example.com/apps/richdocumentscode/proxy.php</c>: the request then reaches
    /// CODE from the proxy on the Nextcloud host itself.
    /// </summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Time allowed for one conversion, upload and download included.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>Largest document sent for conversion; NightMail previews nothing over 40 MB.</summary>
    public long MaxSourceBytes { get; set; } = 50L * 1024 * 1024;
}

/// <summary>Validates <see cref="CollaboraOptions"/> at startup.</summary>
internal sealed class CollaboraOptionsValidator : IValidateOptions<CollaboraOptions>
{
    public ValidateOptionsResult Validate(string? name, CollaboraOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.BaseUrl is { } baseUrl
            && (!baseUrl.IsAbsoluteUri || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(baseUrl.Query) || !string.IsNullOrEmpty(baseUrl.Fragment)))
        {
            failures.Add("Collabora:BaseUrl must be an absolute http(s) URL without a query or fragment.");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            failures.Add("Collabora:Timeout must be positive.");
        }

        if (options.MaxSourceBytes <= 0)
        {
            failures.Add("Collabora:MaxSourceBytes must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
