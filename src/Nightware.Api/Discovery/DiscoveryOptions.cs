using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Nightware.Api.Discovery;

/// <summary>
/// The <c>/.well-known/nightware</c> document NightMail reads when a user adds a
/// NightMail Server account (configuration section <c>Discovery</c>). Every member is
/// serialised as-is, so adjusting the contract is a change to this class only.
/// </summary>
/// <remarks>
/// The client requires every field, absolute <c>https</c> URLs (plain <c>http</c> only to
/// <c>localhost</c>/<c>127.0.0.1</c>/<c>::1</c>), and follows <c>@odata.nextLink</c>s only on
/// the scheme, host and port of <see cref="GraphBaseUrl"/> — so it must be the public URL of
/// this service's <c>/v1.0</c> root.
/// </remarks>
public sealed class DiscoveryOptions : IValidatableObject
{
    public const string SectionName = "Discovery";

    [Required]
    [Url]
    public string GraphBaseUrl { get; set; } = string.Empty;

    [Required]
    [Url]
    public string GraphBetaBaseUrl { get; set; } = string.Empty;

    [Required]
    [Url]
    public string AuthorizationEndpoint { get; set; } = string.Empty;

    [Required]
    [Url]
    public string TokenEndpoint { get; set; } = string.Empty;

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [MinLength(1)]
    [JsonPropertyName("scopes")]
    public IList<string> Scopes { get; init; } = [];

    /// <summary>
    /// Optional. Public base URLs of this server's file sites (Nextcloud, e.g. <c>https://cloud.example.com</c>).
    /// NightMail previews a <c>{base}/s/{token}</c> or <c>{base}/f/{fileId}</c> link in a body through
    /// <c>/shares</c> only on these hosts, and the façade resolves sharing links only below them. Absent (not
    /// configured) means the feature is off, and the member is left out of the document.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<string>? FilesBaseUrls { get; set; }

    /// <summary>
    /// Optional. The change stream's absolute URL (ADR 0007), which NightMail opens with the account's bearer token
    /// and therefore uses only on the scheme, host and port of <see cref="GraphBaseUrl"/>. Left unset it is derived
    /// from <see cref="GraphBaseUrl"/> (<c>…/v1.0</c> → <c>…/nightware/v1/changes</c>) while
    /// <c>ChangeStream:Enabled</c>; with the stream off it is left out of the document.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChangesUrl { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ChangesUrl is not null
            && (!Uri.TryCreate(ChangesUrl, UriKind.Absolute, out var changes)
                || (changes.Scheme != Uri.UriSchemeHttps && changes.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(changes.Query) || !string.IsNullOrEmpty(changes.Fragment) || !string.IsNullOrEmpty(changes.UserInfo)))
        {
            yield return new ValidationResult(
                $"Discovery:ChangesUrl '{ChangesUrl}' must be an absolute http(s) URL without a query, fragment or credentials.",
                [nameof(ChangesUrl)]);
        }

        foreach (var url in FilesBaseUrls ?? [])
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                yield return new ValidationResult(
                    $"Discovery:FilesBaseUrls entry '{url}' must be an absolute http(s) URL without a query, fragment or credentials.",
                    [nameof(FilesBaseUrls)]);
            }
        }
    }
}
