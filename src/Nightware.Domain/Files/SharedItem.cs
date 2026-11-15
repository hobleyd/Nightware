namespace Nightware.Domain.Files;

/// <summary>
/// A link to a document on one of this server's file sites (the <c>filesBaseUrls</c> it advertises in
/// discovery), as found in a message body.
/// </summary>
/// <param name="Url">The link exactly as the client sent it (decoded from Graph's <c>u!</c> share id).</param>
/// <param name="SitePath">
/// The link's path below the site's base URL, without a leading slash and still percent-encoded
/// (e.g. <c>s/AbCdEfGh</c>, <c>index.php/f/385</c>). Only the file backend gives it meaning; the host
/// has already been checked, so a backend never needs to (and must never) contact <see cref="Url"/>.
/// </param>
public sealed record ShareLink(Uri Url, string SitePath);

public enum ContentFormat
{
    Original,
    Pdf,
}

/// <summary>A file or folder reached through a sharing link (Graph <c>driveItem</c>).</summary>
public sealed record SharedItem(string Id, string Name, long Size)
{
    public const string PdfMimeType = "application/pdf";

    public string? MimeType { get; init; }

    public bool IsFolder { get; init; }

    public string? WebUrl { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    /// <summary>Already a PDF (by type, or by name when the server reports only a generic type), so needs no conversion.</summary>
    public bool IsPdf =>
        !IsFolder
        && (string.Equals(MimeType, PdfMimeType, StringComparison.OrdinalIgnoreCase)
            || Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
}
