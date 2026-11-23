using Microsoft.Extensions.Options;

namespace Nightware.Adapters.Dav.Talk;

/// <summary>
/// Configuration section <c>Talk</c>: online meetings in Nextcloud Talk (the <c>spreed</c> app). Optional —
/// without <see cref="PublicBaseUrl"/> an event asking to be online is saved without a meeting room.
/// </summary>
public sealed class TalkOptions
{
    public const string SectionName = "Talk";

    /// <summary>
    /// Nextcloud's public URL as attendees reach it, sub-path included (e.g. <c>https://cloud.example.com/cloud/</c>).
    /// A room's join URL is this followed by <c>call/{token}</c>. <c>Dav:BaseUrl</c> is usually an internal
    /// address, so it cannot stand in.
    /// </summary>
    public Uri? PublicBaseUrl { get; set; }

    /// <summary>
    /// Room name for an event without a subject.
    /// </summary>
    public string UntitledRoomName { get; set; } = "Online meeting";
}

/// <summary>Validates <see cref="TalkOptions"/> at startup.</summary>
internal sealed class TalkOptionsValidator : IValidateOptions<TalkOptions>
{
    public ValidateOptionsResult Validate(string? name, TalkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.PublicBaseUrl is { } url
            && (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment)))
        {
            failures.Add("Talk:PublicBaseUrl must be an absolute http(s) URL without a query or fragment.");
        }

        if (string.IsNullOrWhiteSpace(options.UntitledRoomName))
        {
            failures.Add("Talk:UntitledRoomName must not be empty.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
