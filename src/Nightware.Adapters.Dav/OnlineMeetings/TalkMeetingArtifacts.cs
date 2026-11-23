using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Files;
using Nightware.Adapters.Dav.Files.Drive;
using Nightware.Adapters.Dav.Talk;
using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Adapters.Dav.OnlineMeetings;

/// <summary>
/// <see cref="IMeetingArtifacts"/> over the conversation's shared files and the caller's own (ADR 0009 D7). Talk's
/// recording server uploads a recording to <c>{attachment folder}/Recording/{token}/Recording yyyy-MM-dd HH-mm-ss.{webm|ogg}</c>
/// in the files of whoever started it, and the transcription writes <c>{same name}.md</c> beside it (a summary is
/// <c>{name} - summary.md</c>, left out). The stack's <c>nightware_talk</c> Nextcloud app shares each one with the
/// conversation (a room share, so Talk posts it into the chat), which is how every participant reads them.
/// <list type="bullet">
/// <item>Listed: the files shared into the conversation (Talk's shared items, as the caller sees them) whose names are
/// the recording server's, plus anything still only in the caller's own recording folder for the room (phase 26's
/// reading, for a file the app hasn't shared, or a stack without it). The two are joined by file id.</item>
/// <item>An artifact's id is the file id, which is the same for every participant (a share is the owner's file). Get
/// and content look the id up among the meeting's artifacts, so no other file is reachable here.</item>
/// <item><c>createdDateTime</c> is the start in the file name (the recorder's clock, UTC); <c>endDateTime</c> when the
/// recording was uploaded (its last modification, as a participant sees it). A recording and its transcript share the
/// recording's file id as <c>contentCorrelationId</c>.</item>
/// <item>Content streams the file at the caller's own path to it, as the drive does. A transcript is Talk's Markdown,
/// served as WebVTT: Talk keeps no cue times, so each paragraph is one cue spanning the recording, and the AI warning
/// Talk appends (localised, always the last paragraph) is dropped.</item>
/// </list>
/// </summary>
internal sealed partial class TalkMeetingArtifacts(IDavClient dav, DriveHomeLocator homes) : IMeetingArtifacts
{
    private const string DefaultAttachmentFolder = "/Talk";

    /// <summary>
    /// Talk's shared-item categories a recording or transcript can land in: <c>recording</c> (Talk's own "Share to
    /// chat"), and <c>audio</c>, <c>media</c> (video) and <c>file</c> (a transcript) for a plain room share, as the
    /// <c>nightware_talk</c> app makes.
    /// </summary>
    private static readonly string[] SharedCategories = ["recording", "audio", "media", "file"];

    /// <summary>
    /// A page of shared items, and how many pages of each category are read at most. Talk would give 200, but a file's
    /// message is about a kilobyte (more with a caption) and the OCS client reads at most 64 KiB of a reply.
    /// </summary>
    internal const int SharedPageSize = 20;
    private const int SharedPages = 50;

    private static readonly XName UploadTime = DriveItemMapping.FilesNs + "upload_time";
    private static readonly XName[] Properties =
        [DriveItemMapping.FileId, DavNames.GetContentType, DavNames.GetContentLength, DavNames.GetLastModified, UploadTime, DavNames.ResourceType];

    public async Task<Result<IReadOnlyList<MeetingArtifact>>> ListAsync(string meetingId, MeetingArtifactKind kind, CancellationToken cancellationToken)
    {
        var files = await FilesAsync(meetingId, cancellationToken).ConfigureAwait(false);
        return files.IsSuccess
            ? Result<IReadOnlyList<MeetingArtifact>>.Success([.. files.Value.Where(f => f.Artifact.Kind == kind).Select(f => f.Artifact)])
            : files.Error;
    }

    public async Task<Result<BlobContent>> OpenAsync(string meetingId, MeetingArtifactKind kind, string artifactId, CancellationToken cancellationToken)
    {
        var files = await FilesAsync(meetingId, cancellationToken).ConfigureAwait(false);
        if (!files.IsSuccess)
        {
            return files.Error;
        }

        if (files.Value.FirstOrDefault(f => f.Artifact.Kind == kind && f.Artifact.Id == artifactId) is not { } file)
        {
            return Error.NotFound(kind == MeetingArtifactKind.Recording ? "The meeting has no such recording." : "The meeting has no such transcript.");
        }

        if (kind == MeetingArtifactKind.Transcript)
        {
            var text = await dav.GetAsync(file.Href, cancellationToken).ConfigureAwait(false);
            if (!text.IsSuccess)
            {
                return text.Error;
            }

            var vtt = Encoding.UTF8.GetBytes(TranscriptVtt.FromTalkMarkdown(text.Value.Body, file.Artifact.End - file.Artifact.Created));
            return new BlobContent("text/vtt", vtt.Length, _ => Task.FromResult<Stream>(new MemoryStream(vtt))) { FileName = Path.ChangeExtension(file.Name, ".vtt") };
        }

        var download = await dav.OpenReadAsync(file.Href, cancellationToken).ConfigureAwait(false);
        if (!download.IsSuccess)
        {
            return download.Error;
        }

        Stream body = new ResponseStream(download.Value.Content, download.Value);
        return new BlobContent(file.ContentType ?? download.Value.ContentType, download.Value.Length, _ => Task.FromResult(body)) { FileName = file.Name };
    }

    /// <summary>A meeting's file, classified.</summary>
    private sealed record ArtifactFile(MeetingArtifact Artifact, string Href, string Name, string? ContentType);

    /// <summary>A file as the caller reaches it: in their own recording folder, or shared into the conversation.</summary>
    private sealed record FoundFile(string Id, string Name, string Href, string? ContentType, DateTimeOffset? Uploaded, bool Shared);

    private async Task<Result<IReadOnlyList<ArtifactFile>>> FilesAsync(string meetingId, CancellationToken cancellationToken)
    {
        if (NextcloudOnlineMeetings.TokenOfId(meetingId) is not { } token)
        {
            return Error.NotFound("The online meeting does not exist, or you are not taking part in it.");
        }

        var home = await homes.CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var own = await OwnFilesAsync(home.Value, token, cancellationToken).ConfigureAwait(false);
        if (!own.IsSuccess)
        {
            return own.Error;
        }

        var shared = await SharedFilesAsync(home.Value, token, [.. own.Value.Select(f => f.Id)], cancellationToken).ConfigureAwait(false);
        if (!shared.IsSuccess)
        {
            return shared.Error;
        }

        return Result<IReadOnlyList<ArtifactFile>>.Success(Classify([.. own.Value, .. shared.Value]));
    }

    /// <summary>
    /// Recordings and transcripts among the files. A file in the caller's own recording folder is a recording if it is
    /// audio or video; one shared into the chat only if it also has the recording server's name (the chat holds any
    /// audio anyone shared). A transcript is a <c>.md</c> named after a recording, never a summary.
    /// </summary>
    private static List<ArtifactFile> Classify(IReadOnlyList<FoundFile> files)
    {
        var recordings = new Dictionary<string, FoundFile>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => IsMedia(f.ContentType) && (!f.Shared || StartOf(StemOf(f.Name)) is not null)))
        {
            recordings.TryAdd(StemOf(file.Name), file);
        }

        var artifacts = new List<ArtifactFile>();
        foreach (var file in files)
        {
            var stem = StemOf(file.Name);
            if (recordings.TryGetValue(stem, out var recording) && ReferenceEquals(recording, file))
            {
                artifacts.Add(new ArtifactFile(
                    new MeetingArtifact(file.Id, MeetingArtifactKind.Recording, StartOf(stem) ?? file.Uploaded, file.Uploaded) { ContentCorrelationId = file.Id },
                    file.Href, file.Name, file.ContentType));
            }
            else if (file.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                     && (StartOf(stem) is not null || (!file.Shared && recording is not null)))
            {
                var end = recording?.Uploaded ?? file.Uploaded;
                artifacts.Add(new ArtifactFile(
                    new MeetingArtifact(file.Id, MeetingArtifactKind.Transcript, StartOf(stem) ?? end, end) { ContentCorrelationId = recording?.Id },
                    file.Href, file.Name, file.ContentType));
            }
        }

        return [.. artifacts.OrderByDescending(a => a.Artifact.Created).ThenByDescending(a => a.Artifact.Id, StringComparer.Ordinal)];
    }

    private static bool IsMedia(string? type) =>
        type is not null && (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The caller's own recording folder for the room (phase 26): what they recorded, shared or not.</summary>
    private async Task<Result<IReadOnlyList<FoundFile>>> OwnFilesAsync(DriveHome home, string token, CancellationToken cancellationToken)
    {
        var folder = await RecordingFolderAsync(home, token, cancellationToken).ConfigureAwait(false);
        var listing = await dav.PropFindAsync(folder, DavDepth.One, Properties, cancellationToken).ConfigureAwait(false);
        if (!listing.IsSuccess)
        {
            // Nothing was ever recorded for the room by the caller.
            return listing.Error.Kind == ErrorKind.NotFound ? Result<IReadOnlyList<FoundFile>>.Success([]) : listing.Error;
        }

        return Result<IReadOnlyList<FoundFile>>.Success([.. listing.Value.Responses
            .Where(r => r.IsFound && !r.HasResourceType(DavNames.Collection) && r.Text(DriveItemMapping.FileId) is { Length: > 0 })
            .Select(r => Found(r, shared: false))]);
    }

    /// <summary>
    /// The files shared into the conversation that may be recordings or transcripts (Talk's shared items, any
    /// participant's view), except those already found in the caller's own folder (<paramref name="known"/>), each
    /// looked up at the caller's path to it.
    /// </summary>
    private async Task<Result<IReadOnlyList<FoundFile>>> SharedFilesAsync(DriveHome home, string token, HashSet<string> known, CancellationToken cancellationToken)
    {
        var candidates = new List<(string Id, string Path)>();
        foreach (var category in SharedCategories)
        {
            long lastKnown = 0;
            for (var page = 0; page < SharedPages; page++)
            {
                var query = new Dictionary<string, string>
                {
                    ["objectType"] = category,
                    ["limit"] = SharedPageSize.ToString(CultureInfo.InvariantCulture),
                };
                if (lastKnown > 0)
                {
                    query["lastKnownMessageId"] = lastKnown.ToString(CultureInfo.InvariantCulture);
                }

                var items = await dav.OcsGetAsync($"apps/spreed/api/v1/chat/{Uri.EscapeDataString(token)}/share", query, cancellationToken).ConfigureAwait(false);
                if (!items.IsSuccess)
                {
                    // Talk answers 404 for a conversation the caller has left; the meeting was read just before.
                    return items.Error.Kind == ErrorKind.NotFound ? Result<IReadOnlyList<FoundFile>>.Success([]) : items.Error;
                }

                var messages = Messages(items.Value).ToList();
                foreach (var message in messages)
                {
                    if (message.TryGetProperty("messageParameters", out var parameters)
                        && parameters.ValueKind == JsonValueKind.Object
                        && parameters.TryGetProperty("file", out var file)
                        && file.ValueKind == JsonValueKind.Object
                        && TalkJson.String(file, "id") is { Length: > 0 } id
                        && TalkJson.String(file, "path") is { Length: > 0 } path
                        && StartOf(StemOf(DriveItemMapping.LastSegment(path))) is not null
                        && !known.Contains(id)
                        && candidates.TrueForAll(c => c.Id != id))
                    {
                        candidates.Add((id, path));
                    }
                }

                // Older pages: below the oldest message of this one.
                lastKnown = messages.Count < SharedPageSize ? 0 : messages.Select(m => TalkJson.Long(m, "id") ?? 0).Where(n => n > 0).DefaultIfEmpty(0).Min();
                if (lastKnown <= 0)
                {
                    break;
                }
            }
        }

        var found = new List<FoundFile>();
        foreach (var (id, path) in candidates)
        {
            var href = home.Href + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
            var item = await dav.PropFindAsync(href, DavDepth.Zero, Properties, cancellationToken).ConfigureAwait(false);
            if (!item.IsSuccess)
            {
                if (item.Error.Kind == ErrorKind.NotFound)
                {
                    // Gone since it was shared, or not where Talk says: nothing to list or read.
                    continue;
                }

                return item.Error;
            }

            // The path Talk reports is the caller's; the file there must still be the shared one.
            if (item.Value.Responses.FirstOrDefault(r => r.IsFound && r.Text(DriveItemMapping.FileId) == id) is { } response)
            {
                found.Add(Found(response, shared: true));
            }
        }

        return Result<IReadOnlyList<FoundFile>>.Success(found);
    }

    /// <summary>Talk's shared items: an object keyed by message id, or an empty array.</summary>
    private static IEnumerable<JsonElement> Messages(JsonElement data) =>
        data.ValueKind switch
        {
            JsonValueKind.Object => data.EnumerateObject().Select(p => p.Value).Where(v => v.ValueKind == JsonValueKind.Object),
            JsonValueKind.Array => data.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object),
            _ => [],
        };

    private static FoundFile Found(DavResponse response, bool shared) =>
        new(response.Text(DriveItemMapping.FileId)!, DriveItemMapping.LastSegment(response.Href), response.Href, response.Text(DavNames.GetContentType), Uploaded(response), shared);

    /// <summary>
    /// A file's name without its extension or the <c> (2)</c> Nextcloud adds when a participant already has a file of
    /// that name where shares land, so a recording and its transcript pair up whatever each was renamed to.
    /// </summary>
    internal static string StemOf(string name) => Renamed().Replace(Path.GetFileNameWithoutExtension(name), string.Empty);

    /// <summary>
    /// <c>files/{uid}{attachment folder}/Recording/{token}/</c>. The attachment folder is the caller's Talk setting (its
    /// capabilities report it); a separately configured recording folder isn't visible over the API.
    /// </summary>
    private async Task<string> RecordingFolderAsync(DriveHome home, string token, CancellationToken cancellationToken)
    {
        var folder = DefaultAttachmentFolder;
        var capabilities = await dav.OcsGetAsync("cloud/capabilities", null, cancellationToken).ConfigureAwait(false);
        if (capabilities.IsSuccess
            && capabilities.Value.ValueKind == JsonValueKind.Object
            && capabilities.Value.TryGetProperty("capabilities", out var all)
            && all.ValueKind == JsonValueKind.Object
            && all.TryGetProperty("spreed", out var spreed)
            && spreed.ValueKind == JsonValueKind.Object
            && spreed.TryGetProperty("config", out var config)
            && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("attachments", out var attachments)
            && TalkJson.String(attachments, "folder") is { Length: > 0 } configured)
        {
            folder = configured;
        }

        var segments = folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Append("Recording").Append(token);
        return home.Href + string.Join('/', segments.Select(Uri.EscapeDataString)) + "/";
    }

    private static DateTimeOffset? Uploaded(DavResponse response)
    {
        if (long.TryParse(response.Text(UploadTime), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return DateTimeOffset.TryParse(response.Text(DavNames.GetLastModified), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified)
            ? modified.ToUniversalTime()
            : null;
    }

    /// <summary>The start the recording server put in the name (<c>Recording 2026-11-23 07-00-43</c>, UTC); null for any other name.</summary>
    internal static DateTimeOffset? StartOf(string stem) =>
        RecordingName().Match(stem) is { Success: true } match
        && DateTimeOffset.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)
            ? start.ToUniversalTime()
            : null;

    [GeneratedRegex(@"^Recording (\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex RecordingName();

    [GeneratedRegex(@" \(\d+\)$", RegexOptions.CultureInvariant)]
    private static partial Regex Renamed();
}
