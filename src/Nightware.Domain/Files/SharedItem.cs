namespace Nightware.Domain.Files;

/// <summary>A sharing link as the client sends it: Graph's <c>u!</c>-encoded share id or the raw URL.</summary>
public sealed record ShareLink(string ShareIdOrUrl);

public enum ContentFormat
{
    Original,
    Pdf,
}

/// <summary>A file or folder reached through a sharing link (Graph <c>driveItem</c>).</summary>
public sealed record SharedItem(string Id, string Name, long Size)
{
    public string? MimeType { get; init; }

    public bool IsFolder { get; init; }

    public string? WebUrl { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }
}
