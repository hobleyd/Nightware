using System.Diagnostics;
using System.Globalization;

using OpenTelemetry;

namespace Nightware.Api.Observability;

/// <summary>
/// Applies <see cref="UrlRedaction"/> to every exported span before it leaves the process: the server span's
/// <c>url.path</c> (a share id) and the client span's <c>url.full</c> (a Nextcloud public-share token), plus the
/// older semantic-convention names and the display name, which instrumentation may build from the path.
/// </summary>
/// <remarks>
/// A processor rather than the instrumentations' enrich hooks: on .NET 9+ the runtime and ASP.NET Core set
/// these tags themselves, and <c>OnEnd</c> sees the final tags whichever component wrote them.
/// </remarks>
internal sealed class RedactingSpanProcessor : BaseProcessor<Activity>
{
    private static readonly string[] UrlTags = ["url.path", "url.full", "http.target", "http.url"];

    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);
        foreach (var tag in UrlTags)
        {
            if (data.GetTagItem(tag) is { } value
                && Convert.ToString(value, CultureInfo.InvariantCulture) is { } text
                && UrlRedaction.Redact(text) is var redacted
                && !ReferenceEquals(redacted, text))
            {
                data.SetTag(tag, redacted);
            }
        }

        data.DisplayName = UrlRedaction.Redact(data.DisplayName);
    }
}
