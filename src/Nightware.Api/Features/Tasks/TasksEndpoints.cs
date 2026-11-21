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
/// Microsoft To Do (<c>{mailbox}/todo/lists…</c>): lists (list, get, create, rename, delete), tasks (list,
/// get, create, patch, delete), task attachments (list, get, inline create, upload session, <c>$value</c>,
/// delete), checklist items and linked resources.
/// </summary>
public static partial class TasksEndpoints
{
    private const string Lists = "/todo/lists";
    private const string Tasks = Lists + "/{listId}/tasks";
    private const string Task = Tasks + "/{taskId}";
    private const string Attachments = Task + "/attachments";
    private const string Checklist = Task + "/checklistItems";
    private const string Linked = Task + "/linkedResources";

    /// <summary>
    /// Maps the Tasks routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds Tasks; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapTasksEndpoints(this RouteGroupBuilder graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet(Lists + "/delta", ListsDeltaAsync);
            scope.MapGet(Tasks + "/delta", TasksDeltaAsync);
            scope.MapGet(Lists, ListListsAsync);
            scope.MapPost(Lists, CreateListAsync);
            scope.MapGet(Lists + "/{listId}", GetListAsync);
            scope.MapPatch(Lists + "/{listId}", RenameListAsync);
            scope.MapDelete(Lists + "/{listId}", DeleteListAsync);
            scope.MapGet(Task, GetTaskAsync);
            scope.MapDelete(Task, DeleteTaskAsync);
            scope.MapGet(Attachments + "/{attachmentId}", GetAttachmentAsync);
            scope.MapDelete(Attachments + "/{attachmentId}", DeleteAttachmentAsync);
            scope.MapGet(Checklist, ListChecklistAsync);
            scope.MapPost(Checklist, CreateChecklistItemAsync);
            scope.MapGet(Checklist + "/{itemId}", GetChecklistItemAsync);
            scope.MapPatch(Checklist + "/{itemId}", UpdateChecklistItemAsync);
            scope.MapDelete(Checklist + "/{itemId}", DeleteChecklistItemAsync);
            scope.MapGet(Linked, ListLinkedAsync);
            scope.MapPost(Linked, CreateLinkedAsync);
            scope.MapGet(Linked + "/{resourceId}", GetLinkedAsync);
            scope.MapPatch(Linked + "/{resourceId}", UpdateLinkedAsync);
            scope.MapDelete(Linked + "/{resourceId}", DeleteLinkedAsync);
            scope.MapGet(Tasks, ListTasksAsync).WithCount();
            scope.MapPost(Tasks, CreateTaskAsync);
            scope.MapPatch(Task, UpdateTaskAsync);
            scope.MapGet(Attachments, ListAttachmentsAsync);
            scope.MapPost(Attachments, AddAttachmentAsync);
            scope.MapGet(Attachments + "/{attachmentId}/$value", GetAttachmentContentAsync);
            scope.MapPost(Attachments + "/createUploadSession", BeginUploadAsync);
        }

        // The upload URL is PUT without a bearer: SelfAuthorisingUrlMiddleware recovers the caller's
        // token from it before authentication, so the route is authorised like any other — and by
        // nothing else (UploadUrl.Kind is URL-only). It always names the mailbox by address, so only
        // the /users form exists.
        graph.MapPut(UploadUrl.RoutePattern, UploadAsync).AuthorisedBy(UploadUrl.Kind);
        return graph;
    }

    private static string ListsContext(Mailbox mailbox) => $"{mailbox.ContextSegment}/todo/lists";

    private static string TasksContext(Mailbox mailbox, string listId) => $"{ListsContext(mailbox)}('{listId}')/tasks";

    private static string AttachmentsContext(Mailbox mailbox, string listId, string taskId) => $"{TasksContext(mailbox, listId)}('{taskId}')/attachments";

    private static async Task<IResult> ListListsAsync(Mailbox mailbox, ODataQuery odata, ListTaskLists useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, odata.Query, cancellationToken).ConfigureAwait(false),
            page => GraphResults.Page(page, TodoDtoMapper.ToDto, ListsContext(mailbox), odata.Query));

    private static async Task<IResult> ListsDeltaAsync(
        Mailbox mailbox, ODataQuery odata, HttpContext http, DeltaLinkCodec links, SyncTaskLists useCase, CancellationToken cancellationToken)
    {
        var cursor = links.ReadCursor(http.Request.Query, odata.Query);
        if (!cursor.IsSuccess)
        {
            return GraphResults.Error(cursor.Error);
        }

        var page = await useCase.ExecuteAsync(mailbox.Ref, cursor.Value.Token, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p => GraphResults.Delta(p, TodoDtoMapper.ToDto, cursor.Value, "microsoft.graph.todoTaskList", "#microsoft.graph.todoTaskList"));
    }

    private static async Task<IResult> TasksDeltaAsync(
        Mailbox mailbox, string listId, ODataQuery odata, HttpContext http, DeltaLinkCodec links, SyncTasks useCase, CancellationToken cancellationToken)
    {
        var cursor = links.ReadCursor(http.Request.Query, odata.Query);
        if (!cursor.IsSuccess)
        {
            return GraphResults.Error(cursor.Error);
        }

        var page = await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), cursor.Value.Token, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p => GraphResults.Delta(p, TodoDtoMapper.ToDto, cursor.Value, "microsoft.graph.todoTask", "#microsoft.graph.todoTask"));
    }

    private static async Task<IResult> CreateListAsync(Mailbox mailbox, TodoTaskListWriteDto? body, TaskListsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.CreateAsync(mailbox.Ref, body?.DisplayName, cancellationToken).ConfigureAwait(false),
            list => GraphResults.Created(TodoDtoMapper.ToDto(list), ListsContext(mailbox)));

    private static async Task<IResult> GetListAsync(Mailbox mailbox, string listId, TaskListsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.GetAsync(mailbox.Ref, new TaskListId(listId), cancellationToken).ConfigureAwait(false),
            list => GraphResults.Entity(TodoDtoMapper.ToDto(list), ListsContext(mailbox)));

    private static async Task<IResult> RenameListAsync(Mailbox mailbox, string listId, TodoTaskListWriteDto? body, TaskListsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.RenameAsync(mailbox.Ref, new TaskListId(listId), body?.DisplayName, cancellationToken).ConfigureAwait(false),
            list => GraphResults.Entity(TodoDtoMapper.ToDto(list), ListsContext(mailbox)));

    private static async Task<IResult> DeleteListAsync(Mailbox mailbox, string listId, TaskListsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.DeleteAsync(mailbox.Ref, new TaskListId(listId), cancellationToken).ConfigureAwait(false), GraphResults.NoContent);

    private static async Task<IResult> GetTaskAsync(Mailbox mailbox, string listId, string taskId, ODataQuery odata, GetTask useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), cancellationToken).ConfigureAwait(false),
            task => GraphResults.Entity(TodoDtoMapper.ToDto(task), TasksContext(mailbox, listId), odata.Query.Select));

    private static async Task<IResult> DeleteTaskAsync(Mailbox mailbox, string listId, string taskId, DeleteTask useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), cancellationToken).ConfigureAwait(false),
            GraphResults.NoContent);

    private static async Task<IResult> GetAttachmentAsync(
        Mailbox mailbox, string listId, string taskId, string attachmentId, GetTaskAttachment useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), new AttachmentId(attachmentId), cancellationToken).ConfigureAwait(false),
            found => GraphResults.Entity(
                TodoDtoMapper.ToDto(found.Attachment) with { ContentBytes = Convert.ToBase64String(found.Content) }, AttachmentsContext(mailbox, listId, taskId)));

    private static async Task<IResult> DeleteAttachmentAsync(
        Mailbox mailbox, string listId, string taskId, string attachmentId, DeleteTaskAttachment useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), new AttachmentId(attachmentId), cancellationToken).ConfigureAwait(false),
            GraphResults.NoContent);

    private static string ChecklistContext(Mailbox mailbox, string listId, string taskId) => $"{TasksContext(mailbox, listId)}('{taskId}')/checklistItems";

    private static string LinkedContext(Mailbox mailbox, string listId, string taskId) => $"{TasksContext(mailbox, listId)}('{taskId}')/linkedResources";

    private static async Task<IResult> ListChecklistAsync(Mailbox mailbox, string listId, string taskId, ChecklistItemsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ListAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), cancellationToken).ConfigureAwait(false),
            items => GraphResults.Collection(items.Select(ChecklistItemDto.From), ChecklistContext(mailbox, listId, taskId), Application.Common.Query.ItemQuery.Default));

    private static async Task<IResult> CreateChecklistItemAsync(
        Mailbox mailbox, string listId, string taskId, ChecklistItemDto? body, ChecklistItemsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.CreateAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), body?.DisplayName, body?.IsChecked, cancellationToken).ConfigureAwait(false),
            item => GraphResults.Created(ChecklistItemDto.From(item), ChecklistContext(mailbox, listId, taskId)));

    private static async Task<IResult> GetChecklistItemAsync(
        Mailbox mailbox, string listId, string taskId, string itemId, ChecklistItemsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.GetAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), itemId, cancellationToken).ConfigureAwait(false),
            item => GraphResults.Entity(ChecklistItemDto.From(item), ChecklistContext(mailbox, listId, taskId)));

    private static async Task<IResult> UpdateChecklistItemAsync(
        Mailbox mailbox, string listId, string taskId, string itemId, ChecklistItemDto? body, ChecklistItemsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.UpdateAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), itemId, body?.DisplayName, body?.IsChecked, cancellationToken).ConfigureAwait(false),
            item => GraphResults.Entity(ChecklistItemDto.From(item), ChecklistContext(mailbox, listId, taskId)));

    private static async Task<IResult> DeleteChecklistItemAsync(
        Mailbox mailbox, string listId, string taskId, string itemId, ChecklistItemsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.DeleteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), itemId, cancellationToken).ConfigureAwait(false),
            GraphResults.NoContent);

    private static async Task<IResult> ListLinkedAsync(Mailbox mailbox, string listId, string taskId, LinkedResourcesUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ListAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), cancellationToken).ConfigureAwait(false),
            items => GraphResults.Collection(items.Select(LinkedResourceDto.From), LinkedContext(mailbox, listId, taskId), Application.Common.Query.ItemQuery.Default));

    private static async Task<IResult> CreateLinkedAsync(
        Mailbox mailbox, string listId, string taskId, LinkedResourceDto? body, LinkedResourcesUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.CreateAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), (body ?? new LinkedResourceDto()).ToDomain(string.Empty), cancellationToken).ConfigureAwait(false),
            resource => GraphResults.Created(LinkedResourceDto.From(resource), LinkedContext(mailbox, listId, taskId)));

    private static async Task<IResult> GetLinkedAsync(
        Mailbox mailbox, string listId, string taskId, string resourceId, LinkedResourcesUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.GetAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), resourceId, cancellationToken).ConfigureAwait(false),
            resource => GraphResults.Entity(LinkedResourceDto.From(resource), LinkedContext(mailbox, listId, taskId)));

    private static async Task<IResult> UpdateLinkedAsync(
        Mailbox mailbox, string listId, string taskId, string resourceId, LinkedResourceDto? body, LinkedResourcesUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.UpdateAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), resourceId, (body ?? new LinkedResourceDto()).ToDomain(resourceId), cancellationToken).ConfigureAwait(false),
            resource => GraphResults.Entity(LinkedResourceDto.From(resource), LinkedContext(mailbox, listId, taskId)));

    private static async Task<IResult> DeleteLinkedAsync(
        Mailbox mailbox, string listId, string taskId, string resourceId, LinkedResourcesUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.DeleteAsync(mailbox.Ref, new TaskListId(listId), new TaskId(taskId), resourceId, cancellationToken).ConfigureAwait(false),
            GraphResults.NoContent);

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
        GraphLinkBuilder links, HttpRequest request, SelfAuthorisingUrlSealer sealer, CancellationToken cancellationToken)
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

        var ticket = sealer.TicketFor(UploadUrl.Kind, caller.BearerToken, request.HttpContext.User, session.Value.ExpirationDateTime) with { Name = name, Size = size };
        var binding = new UploadUrlBinding(mailbox.Ref.Address, listId, taskId, session.Value.Token);
        var uploadUrl = UploadUrl.Build(links.VersionRoot(request), binding, sealer.Seal(UploadUrl.Kind, ticket, binding.BoundTo));
        if (!SelfAuthorisingUrl.FitsRequestLine(uploadUrl))
        {
            // An oversized access token would produce a URL the server refuses to read back.
            return GraphResults.Error(new Error(ErrorKind.Unknown, "generalException", "The upload URL would exceed the request-line limit."));
        }

        return GraphResults.Created(new UploadSessionDto { UploadUrl = uploadUrl, ExpirationDateTime = ticket.ExpiresAt }, "microsoft.graph.uploadSession");
    }

    /// <remarks>
    /// Deliberately binds no <see cref="Mailbox"/>: resolving one may consult the directory, which must
    /// not be reachable before the upload URL has authenticated the request. The mailbox, list, task and
    /// session in the path are bound to the sealed ticket, which is the only way in (<see cref="UploadUrl.Kind"/>
    /// is URL-only) and names the one file the session was opened for.
    /// </remarks>
    private static async Task<IResult> UploadAsync(
        string sessionId, HttpRequest request, UploadTaskAttachmentContent useCase, CancellationToken cancellationToken)
    {
        if (request.HttpContext.Features.Get<SealedTicket>() is not { Name: { } name, Size: { } size })
        {
            // Unreachable through authentication; a 401 rather than trusting an unsealed session.
            return Results.Challenge();
        }

        var range = ParseContentRange(request.Headers[HeaderNames.ContentRange].ToString());
        if (!range.IsSuccess)
        {
            return GraphResults.Error(range.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(sessionId, new ExpectedUpload(name, size), range.Value, request.Body, cancellationToken).ConfigureAwait(false),
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

    [GeneratedRegex(@"^\s*bytes\s+(\d+)-(\d+)/(\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ContentRangePattern();
}
