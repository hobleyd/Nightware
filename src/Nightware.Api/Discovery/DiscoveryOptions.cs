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
public sealed class DiscoveryOptions
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
}
