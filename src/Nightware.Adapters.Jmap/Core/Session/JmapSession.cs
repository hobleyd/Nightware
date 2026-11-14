using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Nightware.Adapters.Jmap.Core.Protocol;

namespace Nightware.Adapters.Jmap.Core.Session;

/// <summary>The server limits the client honours (core capability, with configured fallbacks).</summary>
internal sealed record JmapLimits(int MaxObjectsInGet, int MaxCallsInRequest, int MaxObjectsInSet, long MaxSizeUpload, long MaxSizeRequest);

/// <summary>
/// A fetched JMAP Session with its URLs resolved for this deployment and its limits parsed.
/// Cached per credential by <see cref="JmapSessionProvider"/>.
/// </summary>
internal sealed partial class JmapSession
{
    private JmapSession(JmapSessionDocument document, Uri apiUrl, string uploadUrl, string downloadUrl, JmapLimits limits)
    {
        Document = document;
        ApiUrl = apiUrl;
        UploadUrlTemplate = uploadUrl;
        DownloadUrlTemplate = downloadUrl;
        Limits = limits;
    }

    public JmapSessionDocument Document { get; }

    public Uri ApiUrl { get; }

    public string UploadUrlTemplate { get; }

    public string DownloadUrlTemplate { get; }

    public JmapLimits Limits { get; }

    public string State => Document.State;

    public string Username => Document.Username;

    public IReadOnlyDictionary<string, JmapAccountInfo> Accounts => Document.Accounts;

    /// <summary>The primary account for <paramref name="capability"/>, or null.</summary>
    public string? PrimaryAccountId(string capability = JmapCapabilities.Mail) =>
        Document.PrimaryAccounts.TryGetValue(capability, out var id) ? id : null;

    public bool HasCapability(string capability) => Document.Capabilities.ContainsKey(capability);

    /// <summary>An account-level capability object (e.g. <see cref="MailAccountCapability"/>), or null when absent.</summary>
    public T? AccountCapability<T>(string accountId, string capability, JsonTypeInfo<T> type)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(type);
        return Document.Accounts.TryGetValue(accountId, out var account)
            && account.AccountCapabilities.TryGetValue(capability, out var value)
            && value.ValueKind == JsonValueKind.Object
            ? value.Deserialize(type)
            : null;
    }

    /// <summary>The upload URL for an account (RFC 8620 §6.1).</summary>
    public Uri UploadUrl(string accountId) => new(Expand(UploadUrlTemplate, ("accountId", accountId)));

    /// <summary>The download URL for a blob (RFC 8620 §6.2).</summary>
    public Uri DownloadUrl(string accountId, string blobId, string name, string type) =>
        new(Expand(DownloadUrlTemplate, ("accountId", accountId), ("blobId", blobId), ("name", name), ("type", type)));

    /// <summary>
    /// Builds a session from its document. Relative URLs resolve against <paramref name="sessionUrl"/>;
    /// with <paramref name="useSessionOrigin"/> absolute ones are moved onto its scheme/host/port.
    /// </summary>
    public static JmapSession Create(JmapSessionDocument document, Uri sessionUrl, bool useSessionOrigin, int maxObjectsInGetFallback, int maxCallsInRequestFallback)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sessionUrl);
        if (string.IsNullOrEmpty(document.ApiUrl) || string.IsNullOrEmpty(document.UploadUrl) || string.IsNullOrEmpty(document.DownloadUrl))
        {
            throw new JsonException("The JMAP session document lacks apiUrl/uploadUrl/downloadUrl.");
        }

        var core = document.Capabilities.TryGetValue(JmapCapabilities.Core, out var coreJson) && coreJson.ValueKind == JsonValueKind.Object
            ? coreJson.Deserialize(JmapJsonContext.Default.CoreCapability)
            : null;
        var limits = new JmapLimits(
            core?.MaxObjectsInGet > 0 ? core.MaxObjectsInGet : maxObjectsInGetFallback,
            core?.MaxCallsInRequest > 0 ? core.MaxCallsInRequest : maxCallsInRequestFallback,
            core?.MaxObjectsInSet > 0 ? core.MaxObjectsInSet : maxObjectsInGetFallback,
            core?.MaxSizeUpload ?? 0,
            core?.MaxSizeRequest ?? 0);

        var origin = sessionUrl.GetLeftPart(UriPartial.Authority);
        string Resolve(string url)
        {
            var match = OriginPattern().Match(url);
            if (!match.Success)
            {
                return origin + (url.StartsWith('/') ? url : "/" + url);
            }

            return useSessionOrigin ? origin + match.Groups["rest"].Value : url;
        }

        return new JmapSession(document, new Uri(Resolve(document.ApiUrl)), Resolve(document.UploadUrl), Resolve(document.DownloadUrl), limits);
    }

    /// <summary>RFC 6570 level-1 expansion: <c>{name}</c> → percent-encoded value.</summary>
    internal static string Expand(string template, params ReadOnlySpan<(string Name, string Value)> variables)
    {
        var result = template;
        foreach (var (name, value) in variables)
        {
            result = result.Replace("{" + name + "}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        }

        return result;
    }

    [GeneratedRegex(@"^(?<origin>[A-Za-z][A-Za-z0-9+.-]*://[^/?#]*)(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex OriginPattern();
}
