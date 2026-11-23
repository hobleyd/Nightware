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
/// <see cref="IMeetingArtifacts"/> over the caller's files (ADR 0009 D7). Talk's recording server uploads a recording to
/// <c>{attachment folder}/Recording/{token}/Recording yyyy-MM-dd HH-mm-ss.{webm|ogg}</c> in the files of whoever started
/// it, and the transcription writes <c>{same name}.md</c> beside it (a summary is <c>{name} - summary.md</c>, left out).
/// The folder is named after the room, which is how Talk itself tells which room a file belongs to.
/// <list type="bullet">
/// <item>An artifact's id is the file id. Get and content look the id up in that folder, so no other file is reachable here.</item>
/// <item><c>createdDateTime</c> is the start in the file name (the recorder's clock, UTC); <c>endDateTime</c> when the
/// recording was uploaded. A recording and its transcript share the recording's file id as <c>contentCorrelationId</c>.</item>
/// <item>Content streams the file as the drive does. A transcript is Talk's Markdown, served as WebVTT: Talk keeps no cue
/// times, so each paragraph is one cue spanning the recording, and the AI warning Talk appends (localised, always the last
/// paragraph) is dropped.</item>
/// </list>
/// </summary>
internal sealed partial class TalkMeetingArtifacts(IDavClient dav, DriveHomeLocator homes) : IMeetingArtifacts
{
    private const string DefaultAttachmentFolder = "/Talk";
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

    /// <summary>A file in the meeting's recording folder, classified.</summary>
    private sealed record ArtifactFile(MeetingArtifact Artifact, string Href, string Name, string? ContentType);

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

        var folder = await RecordingFolderAsync(home.Value, token, cancellationToken).ConfigureAwait(false);
        var listing = await dav.PropFindAsync(folder, DavDepth.One, Properties, cancellationToken).ConfigureAwait(false);
        if (!listing.IsSuccess)
        {
            // Nothing was ever recorded for the room.
            return listing.Error.Kind == ErrorKind.NotFound ? Result<IReadOnlyList<ArtifactFile>>.Success([]) : listing.Error;
        }

        var files = listing.Value.Responses
            .Where(r => r.IsFound && !r.HasResourceType(DavNames.Collection) && r.Text(DriveItemMapping.FileId) is { Length: > 0 })
            .Select(r => (Response: r, Name: DriveItemMapping.LastSegment(r.Href)))
            .ToList();

        var recordings = files
            .Where(f => IsMedia(f.Response.Text(DavNames.GetContentType)))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f.Name), f => f, StringComparer.Ordinal);

        var artifacts = new List<ArtifactFile>();
        foreach (var (response, name) in files)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var type = response.Text(DavNames.GetContentType);
            var id = response.Text(DriveItemMapping.FileId)!;
            if (IsMedia(type))
            {
                artifacts.Add(new ArtifactFile(
                    new MeetingArtifact(id, MeetingArtifactKind.Recording, StartOf(stem) ?? Uploaded(response), Uploaded(response)) { ContentCorrelationId = id },
                    response.Href, name, type));
            }
            else if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && (recordings.ContainsKey(stem) || StartOf(stem) is not null))
            {
                recordings.TryGetValue(stem, out var recording);
                var end = recording.Response is { } of ? Uploaded(of) : Uploaded(response);
                artifacts.Add(new ArtifactFile(
                    new MeetingArtifact(id, MeetingArtifactKind.Transcript, StartOf(stem) ?? end, end) { ContentCorrelationId = recording.Response?.Text(DriveItemMapping.FileId) },
                    response.Href, name, type));
            }
        }

        return Result<IReadOnlyList<ArtifactFile>>.Success([.. artifacts.OrderByDescending(a => a.Artifact.Created).ThenByDescending(a => a.Artifact.Id, StringComparer.Ordinal)]);

        static bool IsMedia(string? type) =>
            type is not null && (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
    }

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
            && TalkJson.Str(attachments, "folder") is { Length: > 0 } configured)
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
}
