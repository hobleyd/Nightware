using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.OnlineMeetings.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Identity;
using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Api.Features.OnlineMeetings;

/// <summary>
/// Online meetings over Talk rooms, with their recordings and transcripts (phase 26, ADR 0009 D7). Every route is the
/// caller's own (<c>/me</c>, or <c>/users/{x}</c> naming the caller), since the backend acts as the caller.
/// </summary>
public static class OnlineMeetingsEndpoints
{
    private const string Meetings = "/onlineMeetings";
    private const string Meeting = Meetings + "/{meetingId}";

    public static RouteGroupBuilder MapOnlineMeetingsEndpoints(this RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapPost(Meetings, CreateAsync);
            scope.MapGet(Meetings, FindAsync);
            scope.MapGet(Meeting, GetAsync);
            scope.MapPatch(Meeting, UpdateAsync);
            scope.MapDelete(Meeting, DeleteAsync);
            scope.MapGet(Meeting + "/recordings", ListRecordingsAsync);
            scope.MapGet(Meeting + "/recordings/{artifactId}", GetRecordingAsync);
            scope.MapGet(Meeting + "/recordings/{artifactId}/content", RecordingContentAsync);
            scope.MapGet(Meeting + "/transcripts", ListTranscriptsAsync);
            scope.MapGet(Meeting + "/transcripts/{artifactId}", GetTranscriptAsync);
            scope.MapGet(Meeting + "/transcripts/{artifactId}/content", TranscriptContentAsync);
        }

        return graph;
    }

    private static async Task<IResult> CreateAsync(
        Mailbox mailbox, JsonObject? body, IUserContext caller, ODataQuery odata, CreateOnlineMeeting useCase, CancellationToken cancellationToken)
    {
        if (OnlineMeetingRequests.OwnMeetings(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var meeting = OnlineMeetingRequests.ReadNew(body);
        if (!meeting.IsSuccess)
        {
            return GraphResults.Error(meeting.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(meeting.Value, cancellationToken).ConfigureAwait(false),
            created => GraphResults.Created(OnlineMeetingDto.From(created), Context(mailbox), odata.Query.Select));
    }

    private static async Task<IResult> FindAsync(
        Mailbox mailbox, IUserContext caller, ODataQuery odata, FindOnlineMeetingByJoinUrl useCase, CancellationToken cancellationToken)
    {
        if (OnlineMeetingRequests.OwnMeetings(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var url = OnlineMeetingRequests.JoinUrlFilter(odata.Query);
        if (!url.IsSuccess)
        {
            return GraphResults.Error(url.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(url.Value, cancellationToken).ConfigureAwait(false),
            found => GraphResults.Collection(found.Select(OnlineMeetingDto.From), Context(mailbox), odata.Query));
    }

    private static async Task<IResult> GetAsync(
        Mailbox mailbox, string meetingId, IUserContext caller, ODataQuery odata, GetOnlineMeeting useCase, CancellationToken cancellationToken)
    {
        if (OnlineMeetingRequests.OwnMeetings(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(meetingId, cancellationToken).ConfigureAwait(false),
            meeting => GraphResults.Entity(OnlineMeetingDto.From(meeting), Context(mailbox), odata.Query.Select));
    }

    private static async Task<IResult> UpdateAsync(
        Mailbox mailbox, string meetingId, JsonObject? body, IUserContext caller, ODataQuery odata, UpdateOnlineMeeting useCase, CancellationToken cancellationToken)
    {
        if (OnlineMeetingRequests.OwnMeetings(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var change = OnlineMeetingRequests.ReadChange(body);
        if (!change.IsSuccess)
        {
            return GraphResults.Error(change.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(meetingId, change.Value, cancellationToken).ConfigureAwait(false),
            meeting => GraphResults.Entity(OnlineMeetingDto.From(meeting), Context(mailbox), odata.Query.Select));
    }

    private static async Task<IResult> DeleteAsync(Mailbox mailbox, string meetingId, IUserContext caller, DeleteOnlineMeeting useCase, CancellationToken cancellationToken)
    {
        if (OnlineMeetingRequests.OwnMeetings(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        return GraphResults.Match(await useCase.ExecuteAsync(meetingId, cancellationToken).ConfigureAwait(false));
    }

    private static Task<IResult> ListRecordingsAsync(
        Mailbox mailbox, string meetingId, IUserContext caller, HttpRequest request, GraphLinkBuilder links, ODataQuery odata, ListMeetingArtifacts useCase,
        CancellationToken cancellationToken) =>
        ListArtifactsAsync(mailbox, meetingId, MeetingArtifactKind.Recording, caller, request, links, odata, useCase, cancellationToken);

    private static Task<IResult> ListTranscriptsAsync(
        Mailbox mailbox, string meetingId, IUserContext caller, HttpRequest request, GraphLinkBuilder links, ODataQuery odata, ListMeetingArtifacts useCase,
        CancellationToken cancellationToken) =>
        ListArtifactsAsync(mailbox, meetingId, MeetingArtifactKind.Transcript, caller, request, links, odata, useCase, cancellationToken);

    private static Task<IResult> GetRecordingAsync(
        Mailbox mailbox, string meetingId, string artifactId, IUserContext caller, HttpRequest request, GraphLinkBuilder links, ODataQuery odata,
        GetMeetingArtifact useCase, CancellationToken cancellationToken) =>
        GetArtifactAsync(mailbox, meetingId, MeetingArtifactKind.Recording, artifactId, caller, request, links, odata, useCase, cancellationToken);

    private static Task<IResult> GetTranscriptAsync(
        Mailbox mailbox, string meetingId, string artifactId, IUserContext caller, HttpRequest request, GraphLinkBuilder links, ODataQuery odata,
        GetMeetingArtifact useCase, CancellationToken cancellationToken) =>
        GetArtifactAsync(mailbox, meetingId, MeetingArtifactKind.Transcript, artifactId, caller, request, links, odata, useCase, cancellationToken);

    private static Task<IResult> RecordingContentAsync(
        Mailbox mailbox, string meetingId, string artifactId, IUserContext caller, HttpRequest request, DownloadMeetingArtifact useCase, CancellationToken cancellationToken) =>
        ContentAsync(mailbox, meetingId, MeetingArtifactKind.Recording, artifactId, caller, request, useCase, cancellationToken);

    private static Task<IResult> TranscriptContentAsync(
        Mailbox mailbox, string meetingId, string artifactId, IUserContext caller, HttpRequest request, DownloadMeetingArtifact useCase, CancellationToken cancellationToken) =>
        ContentAsync(mailbox, meetingId, MeetingArtifactKind.Transcript, artifactId, caller, request, useCase, cancellationToken);

    private static async Task<IResult> ListArtifactsAsync(
        Mailbox mailbox, string meetingId, MeetingArtifactKind kind, IUserContext caller, HttpRequest request, GraphLinkBuilder links, ODataQuery odata,
        ListMeetingArtifacts useCase, CancellationToken cancellationToken)
    {
        if (OnlineMeetingRequests.OwnMeetings(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var query = odata.Query;
        if (query.Filter is not null || query.OrderBy.Count > 0 || query.Skip is not null || query.SkipToken is not null || query.Search is not null)
        {
            return GraphResults.Error(Error.Validation("Recordings and transcripts are listed with $select and $top only."));
        }

        var listed = await useCase.ExecuteAsync(meetingId, kind, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(listed, artifacts =>
        {
            var url = MeetingUrl(links, request, meetingId);
            var dtos = artifacts.Select(a => (object)MeetingArtifactDto.From(a.Meeting, a.Artifact, url));
            return GraphResults.Collection(query.Top is { } top ? dtos.Take(top) : dtos, ArtifactsContext(mailbox, meetingId, kind), query);
        });
    }

    private static async Task<IResult> GetArtifactAsync(
        Mailbox mailbox, string meetingId, MeetingArtifactKind kind, string artifactId, IUserContext caller, HttpRequest request, GraphLinkBuilder links,
        ODataQuery odata, GetMeetingArtifact useCase, CancellationToken cancellationToken)
    {
        if (OnlineMeetingRequests.OwnMeetings(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(meetingId, kind, artifactId, cancellationToken).ConfigureAwait(false),
            found => GraphResults.Entity(
                MeetingArtifactDto.From(found.Meeting, found.Artifact, MeetingUrl(links, request, meetingId)), ArtifactsContext(mailbox, meetingId, kind), odata.Query.Select));
    }

    /// <summary>
    /// The bytes, streamed as they come from the file store. A transcript is WebVTT, the only format offered:
    /// <c>$format</c> may ask for <c>text/vtt</c>, anything else is 400.
    /// </summary>
    private static async Task<IResult> ContentAsync(
        Mailbox mailbox, string meetingId, MeetingArtifactKind kind, string artifactId, IUserContext caller, HttpRequest request,
        DownloadMeetingArtifact useCase, CancellationToken cancellationToken)
    {
        if (OnlineMeetingRequests.OwnMeetings(mailbox, caller) is { } denied)
        {
            return GraphResults.Error(denied);
        }

        var format = request.Query["$format"];
        if (format.Count > 1 || (format.Count == 1 && (kind == MeetingArtifactKind.Recording || !string.Equals(format[0], "text/vtt", StringComparison.OrdinalIgnoreCase))))
        {
            return GraphResults.Error(Error.Validation("Only $format=text/vtt is offered, for transcripts.", "$format"));
        }

        var content = await useCase.ExecuteAsync(meetingId, kind, artifactId, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return GraphResults.Error(content.Error);
        }

        var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
        return Results.Stream(stream, content.Value.ContentType, content.Value.FileName);
    }

    private static string Context(Mailbox mailbox) => $"{mailbox.ContextSegment}/onlineMeetings";

    private static string ArtifactsContext(Mailbox mailbox, string meetingId, MeetingArtifactKind kind) =>
        $"{mailbox.ContextSegment}/onlineMeetings('{Uri.EscapeDataString(meetingId)}')/{(kind == MeetingArtifactKind.Recording ? "recordings" : "transcripts")}";

    private static string MeetingUrl(GraphLinkBuilder links, HttpRequest request, string meetingId) =>
        $"{links.VersionRoot(request)}/me/onlineMeetings/{Uri.EscapeDataString(meetingId)}";
}
