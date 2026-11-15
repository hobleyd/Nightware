using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

using Nightware.Api.Auth;
using Nightware.Api.Features.Tasks.Dto;
using Nightware.Api.Features.Tasks.Upload;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Json;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Identity;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Api.Features.Tasks;

/// <summary>
/// Microsoft To Do (<c>{mailbox}/todo/lists…</c>): lists, tasks (list, create, patch) and task
/// attachments (list, inline create, upload session, <c>$value</c>). Per the contract there is no
/// list create, no task <c>GET</c>/<c>DELETE</c>, and no attachment delete.
/// </summary>
public static partial class TasksEndpoints
{
    private const string Lists = "/todo/lists";
    private const string Tasks = Lists + "/{listId}/tasks";
    private const string Task = Tasks + "/{taskId}";
    private const string Attachments = Task + "/attachments";

    /// <summary>
    /// Maps the Tasks routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds Tasks; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapTasksEndpoints(this RouteGroupBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var protector = ((IEndpointRouteBuilder)graph).ServiceProvider.GetRequiredService<UploadUrlProtector>();

        graph.MapMailboxGet(Lists, ListListsAsync);
        graph.MapMailboxGet(Tasks, ListTasksAsync);
        graph.MapMailboxPost(Tasks, CreateTaskAsync);
        graph.MapMailboxPatch(Task, UpdateTaskAsync);
        graph.MapMailboxGet(Attachments, ListAttachmentsAsync);
        graph.MapMailboxPost(Attachments, AddAttachmentAsync);
        graph.MapMailboxGet(Attachments + "/{attachmentId}/$value", GetAttachmentContentAsync);
        graph.MapMailboxPost(Attachments + "/createUploadSession", (Mailbox mailbox, string listId, string taskId, JsonObject? body,
                BeginTaskAttachmentUpload useCase, IUserContext caller, GraphLinkBuilder links, HttpRequest request, CancellationToken cancellationToken) =>
            BeginUploadAsync(mailbox, listId, taskId, body, useCase, caller, links, request, protector, cancellationToken));

        // The upload URL is PUT without a bearer: SelfAuthorisingUrlMiddleware recovers the caller's
        // token from it (UploadUrlCredentialSource) before authentication, so the route is authorised
        // like any other. It always names the mailbox by address, so only the /users form exists.
        graph.MapPut(UploadUrl.RoutePattern, UploadAsync);
        return graph;
    }

    private static string ListsContext(Mailbox mailbox) => $"{mailbox.ContextSegment}/todo/lists";

    private static string TasksContext(Mailbox mailbox, string listId) => $"{ListsContext(mailbox)}('{listId}')/tasks";

    private static string AttachmentsContext(Mailbox mailbox, string listId, string taskId) => $"{TasksContext(mailbox, listId)}('{taskId}')/attachments";

    private static async Task<IResult> ListListsAsync(Mailbox mailbox, ODataQuery odata, ListTaskLists useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, TodoDtoMapper.ToDto, ListsContext(mailbox), odata.Query));

    private static async Task<IResult> ListTasksAsync(Mailbox mailbox, string listId, ODataQuery odata, ListTasks useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, TodoDtoMapper.ToDto, TasksContext(mailbox, listId), odata.Query));

    private static async Task<IResult> CreateTaskAsync(Mailbox mailbox, string listId, JsonObject? body, CreateTask useCase, CancellationToken cancellationToken)
    {
        var draft = TodoRequests.ReadTask(body);
        return draft.IsSuccess
            ? GraphResults.Match(
                await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), draft.Value, cancellationToken).ConfigureAwait(false),
                task => GraphResults.Created(TodoDtoMapper.ToDto(task), TasksContext(mailbox, listId)))
            : GraphResults.Error(draft.Error);
    }

    private static async Task<IResult> UpdateTaskAsync(
        Mailbox mailbox, string listId, string taskId, JsonObject? body, ODataQuery odata, UpdateTask useCase, CancellationToken cancellationToken)
    {
        var patch = TodoRequests.ReadTask(body);
        return patch.IsSuccess
            ? GraphResults.Match(
                await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), patch.Value, cancellationToken).ConfigureAwait(false),
                task => GraphResults.Entity(TodoDtoMapper.ToDto(task), TasksContext(mailbox, listId), odata.Query.Select))
            : GraphResults.Error(patch.Error);
    }

    private static async Task<IResult> ListAttachmentsAsync(
        Mailbox mailbox, string listId, string taskId, ODataQuery odata, ListTaskAttachments useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), cancellationToken).ConfigureAwait(false),
            attachments => GraphResults.Collection(attachments.Select(TodoDtoMapper.ToDto), AttachmentsContext(mailbox, listId, taskId), odata.Query));

    private static async Task<IResult> AddAttachmentAsync(
        Mailbox mailbox, string listId, string taskId, JsonObject? body, AddTaskAttachment useCase, CancellationToken cancellationToken)
    {
        var attachment = TodoRequests.ReadAttachment(body);
        return attachment.IsSuccess
            ? GraphResults.Match(
                await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), attachment.Value, cancellationToken).ConfigureAwait(false),
                created => GraphResults.Created(TodoDtoMapper.ToDto(created), AttachmentsContext(mailbox, listId, taskId)))
            : GraphResults.Error(attachment.Error);
    }

    private static async Task<IResult> GetAttachmentContentAsync(
        Mailbox mailbox, string listId, string taskId, string attachmentId, GetTaskAttachmentContent useCase, CancellationToken cancellationToken)
    {
        var content = await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), new AttachmentId(attachmentId), cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return GraphResults.Error(content.Error);
        }

        var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
        return Results.Stream(stream, content.Value.ContentType, content.Value.FileName);
    }

    private static async Task<IResult> BeginUploadAsync(
        Mailbox mailbox, string listId, string taskId, JsonObject? body, BeginTaskAttachmentUpload useCase, IUserContext caller,
        GraphLinkBuilder links, HttpRequest request, UploadUrlProtector protector, CancellationToken cancellationToken)
    {
        var item = TodoRequests.ReadUploadRequest(body);
        if (!item.IsSuccess)
        {
            return GraphResults.Error(item.Error);
        }

        var (name, contentType, size) = item.Value;
        var session = await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), name, contentType, size, cancellationToken).ConfigureAwait(false);
        if (!session.IsSuccess)
        {
            return GraphResults.Error(session.Error);
        }

        var bearer = caller.BearerToken;
        var expires = AccessTokenExpiry(bearer) is { } tokenExpiry && tokenExpiry < session.Value.ExpirationDateTime ? tokenExpiry : session.Value.ExpirationDateTime;
        var binding = new UploadUrlBinding(mailbox.Ref.Address, listId, taskId, session.Value.Token);
        var uploadUrl = UploadUrl.Build(links.VersionRoot(request), binding, protector.Seal(new UploadTicket(bearer, name, size, expires), binding));
        if (uploadUrl.Length > UploadUrl.MaxRequestLine - 64)
        {
            // An oversized access token would produce a URL the server refuses to read back.
            return GraphResults.Error(new Error(ErrorKind.Unknown, "generalException", "The upload URL would exceed the request-line limit."));
        }

        return GraphResults.Created(new UploadSessionDto { UploadUrl = uploadUrl, ExpirationDateTime = expires }, "microsoft.graph.uploadSession");
    }

    /// <remarks>
    /// Deliberately binds no <see cref="Mailbox"/>: resolving one may consult the directory, which must
    /// not be reachable before the upload URL has authenticated the request. The mailbox, list and
    /// task in the path are bound to the sealed credential, and the store re-checks the target.
    /// </remarks>
    private static async Task<IResult> UploadAsync(
        string sessionId, HttpRequest request, UploadTaskAttachmentContent useCase, CancellationToken cancellationToken)
    {
        var range = ParseContentRange(request.Headers[HeaderNames.ContentRange].ToString());
        if (!range.IsSuccess)
        {
            return GraphResults.Error(range.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(sessionId, range.Value, request.Body, cancellationToken).ConfigureAwait(false),
            attachment => GraphResults.Created(TodoDtoMapper.ToDto(attachment), "microsoft.graph.taskFileAttachment"));
    }

    /// <summary><c>bytes first-last/total</c>; null when absent.</summary>
    internal static Result<ContentRange?> ParseContentRange(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return Result<ContentRange?>.Success(null);
        }

        var match = ContentRangePattern().Match(header);
        return match.Success
               && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var first)
               && long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var last)
               && long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var total)
            ? new ContentRange(first, last, total)
            : Error.Validation("Content-Range must be 'bytes first-last/total'.", "Content-Range");
    }

    /// <summary>The access token's <c>exp</c>, when it is a readable JWT; validation happens elsewhere.</summary>
    internal static DateTimeOffset? AccessTokenExpiry(string token) => AccessTokens.ExpiryOf(token);

    [GeneratedRegex(@"^\s*bytes\s+(\d+)-(\d+)/(\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ContentRangePattern();
}
