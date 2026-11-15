using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Tasks;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Tasks;

using TaskStatus = Nightware.Domain.Tasks.TaskStatus;

namespace Nightware.Api.Tests.Features.Tasks;

public sealed class TasksEndpointsTests : IAsyncLifetime
{
    private static readonly TaskListId List = new("list-1");
    private static readonly TaskId Task1 = new("task-1");
    private static readonly TodoTask Reply = new(Task1, "Reply to Alex")
    {
        BodyText = "See the email.",
        DueDateTime = new DateTimeTimeZone(new DateTime(2026, 6, 5), "UTC"),
        CreatedDateTime = new DateTimeOffset(2026, 6, 1, 10, 5, 0, TimeSpan.Zero).AddTicks(1234567),
        LastModifiedDateTime = new DateTimeOffset(2026, 6, 1, 10, 5, 0, TimeSpan.Zero),
    };

    private static readonly TaskAttachment Thread = new(new AttachmentId("att-1"), "thread.eml", "message/rfc822", 245);

    private readonly ITaskListReader _lists = Substitute.For<ITaskListReader>();
    private readonly ITaskStore _tasks = Substitute.For<ITaskStore>();
    private readonly ITaskAttachmentStore _attachments = Substitute.For<ITaskAttachmentStore>();
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services.AddSingleton(_lists).AddSingleton(_tasks).AddSingleton(_attachments));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/v1.0/me", TestHost.UserEmail)]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com")]
    [InlineData("/v1.0/users/shared%40example.com", "shared@example.com")]
    public async Task Lists_task_lists_in_both_forms(string prefix, string mailbox)
    {
        _lists.ListAsync(new MailboxRef(mailbox), Arg.Is<ItemQuery>(q => q.Top == 100), Arg.Any<CancellationToken>())
            .Returns(new Page<TaskList>([new TaskList(List, "Tasks") { WellKnownListName = "defaultList" }, new TaskList(new TaskListId("l2"), "Shared") { IsOwner = false, IsShared = true }]));

        var response = await _host.AuthorizedClient().GetAsync(prefix + "/todo/lists?$top=100", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var value = (await response.ReadObjectAsync())["value"]!.AsArray();
        JsonNode.DeepEquals(value[0], JsonNode.Parse("""{"id":"list-1","displayName":"Tasks","isOwner":true,"isShared":false,"wellknownListName":"defaultList"}""")).ShouldBeTrue(value[0]!.ToJsonString());
        value[1].Str("wellknownListName").ShouldBe("none");
        value[1]!["isShared"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Lists_tasks_with_the_clients_query_and_graph_date_shapes()
    {
        _tasks.ListAsync(new MailboxRef(TestHost.UserEmail), List,
                Arg.Is<ItemQuery>(q => q.Top == 200 && q.OrderBy.Single().Property == "createdDateTime" && q.Filter is FilterNode.Compare), Arg.Any<CancellationToken>())
            .Returns(new Page<TodoTask>([Reply], "200"));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/todo/lists/list-1/tasks?$top=200&$orderby=createdDateTime desc&$filter=status ne 'completed'", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        var task = body["value"]![0]!.AsObject();
        JsonNode.DeepEquals(task, JsonNode.Parse("""
            {"id":"task-1","title":"Reply to Alex","status":"notStarted","importance":"normal","isReminderOn":false,"hasAttachments":false,
             "createdDateTime":"2026-06-01T10:05:00.1234567Z","lastModifiedDateTime":"2026-06-01T10:05:00.0000000Z",
             "body":{"contentType":"text","content":"See the email."},
             "dueDateTime":{"dateTime":"2026-06-05T00:00:00.0000000","timeZone":"UTC"}}
            """)).ShouldBeTrue(task.ToJsonString());
        body["@odata.context"]!.GetValue<string>().ShouldBe(TestHost.GraphBase + "/$metadata#users('me%40example.com')/todo/lists('list-1')/tasks");
        body["@odata.nextLink"]!.GetValue<string>().ShouldEndWith("$skiptoken=200");
    }

    [Fact]
    public async Task An_unsupported_filter_is_a_400_from_the_store()
    {
        _tasks.ListAsync(Arg.Any<MailboxRef>(), List, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Error.Validation("not supported"));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/todo/lists/list-1/tasks?$filter=importance eq 'high'", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/v1.0/me", TestHost.UserEmail)]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com")]
    public async Task Creates_a_task(string prefix, string mailbox)
    {
        _tasks.CreateAsync(new MailboxRef(mailbox), List, Arg.Is<TaskDraft>(d => d.Title == "Reply to Alex" && d.Importance == TaskImportance.High
                && d.BodyText == "See the email." && d.DueDateTime == new DateTimeTimeZone(new DateTime(2026, 6, 5), "UTC")), Arg.Any<CancellationToken>())
            .Returns(Reply with { Importance = TaskImportance.High });

        var response = await _host.AuthorizedClient().PostAsJsonAsync(prefix + "/todo/lists/list-1/tasks", new
        {
            title = "Reply to Alex",
            importance = "high",
            body = new { content = "See the email.", contentType = "text" },
            dueDateTime = new { dateTime = "2026-06-05T00:00:00", timeZone = "UTC" },
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.ReadObjectAsync()).Str("importance").ShouldBe("high");
    }

    [Theory]
    [InlineData("""{"title":"x","importance":"urgent"}""")]
    [InlineData("""{"title":"x","importance":"1"}""")]
    [InlineData("""{"title":"x","status":7}""")]
    [InlineData("""{"title":"x","dueDateTime":{"dateTime":"tomorrow","timeZone":"UTC"}}""")]
    [InlineData("""{"title":5}""")]
    [InlineData("""{"importance":"low"}""")]
    public async Task Rejects_invalid_tasks(string json)
    {
        var response = await _host.AuthorizedClient().PostAsync("/v1.0/me/todo/lists/list-1/tasks", Json(json), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("invalidRequest");
        await _tasks.DidNotReceiveWithAnyArgs().CreateAsync(default, default, null!, Ct);
    }

    [Fact]
    public async Task Patches_the_status()
    {
        _tasks.UpdateAsync(new MailboxRef(TestHost.UserEmail), List, Task1, new TaskDraft { Status = TaskStatus.Completed }, Arg.Any<CancellationToken>())
            .Returns(Reply with { Status = TaskStatus.Completed, CompletedDateTime = new DateTimeTimeZone(new DateTime(2026, 6, 2, 8, 30, 0), "UTC") });

        var response = await _host.AuthorizedClient().PatchAsync("/v1.0/me/todo/lists/list-1/tasks/task-1", Json("""{"status":"completed"}"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.Str("status").ShouldBe("completed");
        body.Str("completedDateTime.dateTime").ShouldBe("2026-06-02T00:00:00.0000000");
    }

    [Fact]
    public async Task An_explicit_null_due_date_clears_it()
    {
        _tasks.UpdateAsync(new MailboxRef("shared@example.com"), List, Task1, new TaskDraft { ClearDueDateTime = true }, Arg.Any<CancellationToken>())
            .Returns(Reply with { DueDateTime = null });

        var response = await _host.AuthorizedClient().PatchAsync("/v1.0/users/shared@example.com/todo/lists/list-1/tasks/task-1", Json("""{"dueDateTime":null}"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadObjectAsync()).ContainsKey("dueDateTime").ShouldBeFalse();
    }

    [Fact]
    public async Task Patching_a_due_date_sends_it()
    {
        _tasks.UpdateAsync(Arg.Any<MailboxRef>(), List, Task1, new TaskDraft { DueDateTime = new DateTimeTimeZone(new DateTime(2026, 7, 1), "UTC") }, Arg.Any<CancellationToken>())
            .Returns(Reply);

        var response = await _host.AuthorizedClient().PatchAsync("/v1.0/me/todo/lists/list-1/tasks/task-1", Json("""{"dueDateTime":{"dateTime":"2026-07-01T00:00:00","timeZone":"UTC"}}"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_missing_task_is_404()
    {
        _tasks.UpdateAsync(Arg.Any<MailboxRef>(), List, Task1, Arg.Any<TaskDraft>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));

        var response = await _host.AuthorizedClient().PatchAsync("/v1.0/me/todo/lists/list-1/tasks/task-1", Json("""{"status":"completed"}"""), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lists_attachments()
    {
        _attachments.ListAsync(new MailboxRef(TestHost.UserEmail), List, Task1, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<TaskAttachment>>.Success([Thread]));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/todo/lists/list-1/tasks/task-1/attachments", Ct);

        var item = (await response.ReadObjectAsync())["value"]![0]!.AsObject();
        JsonNode.DeepEquals(item, JsonNode.Parse("""{"@odata.type":"#microsoft.graph.taskFileAttachment","id":"att-1","name":"thread.eml","contentType":"message/rfc822","size":245}""")).ShouldBeTrue(item.ToJsonString());
    }

    [Fact]
    public async Task Attaches_inline_content()
    {
        var bytes = Encoding.UTF8.GetBytes("From: a@example.com\r\n\r\nhi");
        _attachments.CreateAsync(new MailboxRef("shared@example.com"), List, Task1, Arg.Is<NewAttachment>(a => a.Name == "thread.eml" && a.Content.ToArray().SequenceEqual(bytes)), Arg.Any<CancellationToken>())
            .Returns(Thread);

        var response = await _host.AuthorizedClient().PostAsJsonAsync("/v1.0/users/shared@example.com/todo/lists/list-1/tasks/task-1/attachments", new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.taskFileAttachment",
            ["name"] = "thread.eml",
            ["contentType"] = "message/rfc822",
            ["contentBytes"] = Convert.ToBase64String(bytes),
            ["size"] = bytes.Length,
        }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.ReadObjectAsync()).Str("id").ShouldBe("att-1");
    }

    [Theory]
    [InlineData("""{"name":"a","contentBytes":"not base64!"}""")]
    [InlineData("""{"name":"a"}""")]
    [InlineData("""{"@odata.type":"#microsoft.graph.taskReferenceAttachment","name":"a","contentBytes":"aGk="}""")]
    public async Task Rejects_invalid_attachments(string json)
    {
        var response = await _host.AuthorizedClient().PostAsync("/v1.0/me/todo/lists/list-1/tasks/task-1/attachments", Json(json), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Streams_an_attachments_bytes()
    {
        _attachments.GetContentAsync(new MailboxRef(TestHost.UserEmail), List, Task1, new AttachmentId("att-1"), Arg.Any<CancellationToken>())
            .Returns(new BlobContent("message/rfc822", 2, _ => Task.FromResult<Stream>(new MemoryStream("hi"u8.ToArray()))) { FileName = "thread.eml" });

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/todo/lists/list-1/tasks/task-1/attachments/att-1/$value", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("message/rfc822");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("hi");
    }

    [Fact]
    public async Task A_missing_attachment_is_404()
    {
        _attachments.GetContentAsync(Arg.Any<MailboxRef>(), List, Task1, Arg.Any<AttachmentId>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/users/shared@example.com/todo/lists/list-1/tasks/task-1/attachments/x/$value", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Upload_session_then_bearerless_put_completes_as_the_caller()
    {
        var session = GivenSession("shared@example.com");
        string? seenUser = null;
        _attachments.PutChunkAsync(Arg.Is<UploadSession>(s => s.Token == session.Token), Arg.Is(0L), Arg.Is(4095L), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seenUser = _host.App.Services.GetRequiredService<IHttpContextAccessor>().HttpContext!.User.FindFirst("email")!.Value;
                return new UploadProgress([], Thread with { Size = 4096 });
            });
        var token = TestHost.Token(expires: DateTime.UtcNow.AddMinutes(5));

        var created = await _host.AuthorizedClient(token).PostAsync("/v1.0/users/shared@example.com/todo/lists/list-1/tasks/task-1/attachments/createUploadSession", Json(UploadRequest), Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await created.ReadObjectAsync();
        var uploadUrl = body.Str("uploadUrl")!;
        uploadUrl.ShouldStartWith(TestHost.GraphBase + "/users/shared@example.com/todo/lists/list-1/tasks/task-1/attachmentSessions/");
        uploadUrl.ShouldNotContain(token);
        body["nextExpectedRanges"]!.AsArray().Single()!.GetValue<string>().ShouldBe("0-");
        var expires = DateTimeOffset.Parse(body.Str("expirationDateTime")!, System.Globalization.CultureInfo.InvariantCulture);
        expires.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(5).AddSeconds(1), "the URL lives no longer than the access token");

        var put = await PutAsync(uploadUrl, 4096, "bytes 0-4095/4096");

        put.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await put.ReadObjectAsync())["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.taskFileAttachment");
        seenUser.ShouldBe(TestHost.UserEmail);
    }

    [Fact]
    public async Task The_upload_url_works_without_content_range()
    {
        var session = GivenSession(TestHost.UserEmail);
        _attachments.PutChunkAsync(Arg.Is<UploadSession>(s => s.Token == session.Token), Arg.Is(0L), Arg.Is(UploadTaskAttachmentContent.ToEnd), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new UploadProgress([], Thread));

        var put = await PutAsync(await CreateUploadUrlAsync("/v1.0/me"), 10, null);

        put.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    public static TheoryData<string, Func<string, string>> Tampered() => new()
    {
        { "flipped token", url => url[..^3] + (url[^3] == 'A' ? 'B' : 'A') + url[^2..] },
        { "no token", url => url[..url.IndexOf('?', StringComparison.Ordinal)] },
        { "other session", url => url.Replace("/attachmentSessions/", "/attachmentSessions/x", StringComparison.Ordinal) },
        { "other task", url => url.Replace("/tasks/task-1/", "/tasks/task-2/", StringComparison.Ordinal) },
        { "other mailbox", url => url.Replace("/users/me@example.com/", "/users/bob@example.com/", StringComparison.Ordinal) },
        { "beta root", url => url.Replace("/v1.0/", "/beta/", StringComparison.Ordinal) },
    };

    [Theory]
    [MemberData(nameof(Tampered))]
    public async Task A_changed_upload_url_is_refused_with_401(string change, Func<string, string> tamper)
    {
        GivenSession(TestHost.UserEmail);
        var url = tamper(await CreateUploadUrlAsync("/v1.0/me"));

        var put = await PutAsync(url, 4096, "bytes 0-4095/4096");

        if (change == "beta root")
        {
            // The binding names the route, not the version root: /beta serves the same route.
            put.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
            return;
        }

        put.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, change);
        (await put.ReadObjectAsync()).Str("error.code").ShouldBe("InvalidAuthenticationToken");
        await _attachments.DidNotReceiveWithAnyArgs().PutChunkAsync(null!, default, default, null!, Ct);
    }

    [Fact]
    public async Task The_upload_url_is_not_a_bearer_for_other_requests()
    {
        GivenSession(TestHost.UserEmail);
        var url = await CreateUploadUrlAsync("/v1.0/me");
        using var anonymous = _host.App.GetTestClient();

        var get = await anonymous.GetAsync(Relative(url).Replace("/attachmentSessions/", "/x/", StringComparison.Ordinal), Ct);
        var post = await anonymous.PostAsync(Relative(url), new ByteArrayContent(new byte[4096]), Ct);

        get.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        post.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_expired_access_token_inside_a_live_url_fails_validation()
    {
        GivenSession(TestHost.UserEmail);
        var expired = TestHost.Token(expires: DateTime.UtcNow.AddMinutes(-10));
        var protector = new Api.Features.Tasks.Upload.UploadUrlProtector(
            Microsoft.Extensions.Options.Options.Create(new Api.Graph.Json.DeltaLinkOptions { SigningKey = "test-delta-key" }), TimeProvider.System);
        var binding = new Api.Features.Tasks.Upload.UploadUrlBinding(TestHost.UserEmail, "list-1", "task-1", "session-token");
        var url = Api.Features.Tasks.Upload.UploadUrl.Build("/v1.0", binding,
            protector.Seal(new Api.Features.Tasks.Upload.UploadTicket(expired, "big.eml", 4096, DateTimeOffset.UtcNow.AddMinutes(5)), binding));

        var put = await PutAsync(url, 4096, null);

        put.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_partial_range_is_400()
    {
        GivenSession(TestHost.UserEmail);
        var url = await CreateUploadUrlAsync("/v1.0/me");

        var put = await PutAsync(url, 1024, "bytes 0-1023/4096");

        put.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_malformed_range_is_400()
    {
        GivenSession(TestHost.UserEmail);
        var url = await CreateUploadUrlAsync("/v1.0/me");
        using var anonymous = _host.App.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, Relative(url)) { Content = new ByteArrayContent(new byte[4]) };
        request.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */4");

        var put = await anonymous.SendAsync(request, Ct);

        put.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_store_failure_on_put_is_mapped()
    {
        GivenSession(TestHost.UserEmail);
        _attachments.PutChunkAsync(Arg.Any<UploadSession>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", "too big"));

        var put = await PutAsync(await CreateUploadUrlAsync("/v1.0/me"), 4096, "bytes 0-4095/4096");

        put.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_bearer_also_authorises_the_put()
    {
        var session = GivenSession(TestHost.UserEmail);
        _attachments.PutChunkAsync(Arg.Is<UploadSession>(s => s.Token == session.Token), Arg.Is(0L), Arg.Any<long>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new UploadProgress([], Thread));

        var put = await _host.AuthorizedClient().PutAsync($"/v1.0/users/{TestHost.UserEmail}/todo/lists/list-1/tasks/task-1/attachmentSessions/{session.Token}/content", new ByteArrayContent(new byte[4]), Ct);

        put.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("""{"attachmentItem":{"attachmentType":"reference","name":"a","size":1}}""")]
    [InlineData("""{"name":"a"}""")]
    [InlineData("""{"attachmentItem":{"name":"a","size":"big"}}""")]
    public async Task Rejects_invalid_upload_requests(string json)
    {
        var response = await _host.AuthorizedClient().PostAsync("/v1.0/me/todo/lists/list-1/tasks/task-1/attachments/createUploadSession", Json(json), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_session_failures_are_mapped()
    {
        _attachments.BeginUploadAsync(Arg.Any<MailboxRef>(), List, Task1, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Error.NotFound("no task"));

        var response = await _host.AuthorizedClient().PostAsync("/v1.0/me/todo/lists/list-1/tasks/task-1/attachments/createUploadSession", Json(UploadRequest), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Me_has_no_bearerless_upload_route()
    {
        using var anonymous = _host.App.GetTestClient();

        var put = await anonymous.PutAsync("/v1.0/me/todo/lists/list-1/tasks/task-1/attachmentSessions/s/content", new ByteArrayContent([1]), Ct);

        put.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("bytes 0-9/10", 0L, 9L, 10L)]
    [InlineData("BYTES 5-9/20", 5L, 9L, 20L)]
    public void Parses_content_range(string header, long first, long last, long total) =>
        Api.Features.Tasks.TasksEndpoints.ParseContentRange(header).Value.ShouldBe(new ContentRange(first, last, total));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void A_missing_content_range_is_null(string? header) =>
        Api.Features.Tasks.TasksEndpoints.ParseContentRange(header).Value.ShouldBeNull();

    [Theory]
    [InlineData("bytes */10")]
    [InlineData("bytes 0-9")]
    [InlineData("items 0-9/10")]
    public void Rejects_malformed_content_range(string header) =>
        Api.Features.Tasks.TasksEndpoints.ParseContentRange(header).Error!.Target.ShouldBe("Content-Range");

    [Fact]
    public void Reads_the_access_tokens_expiry()
    {
        var expires = DateTime.UtcNow.AddMinutes(7);

        Api.Features.Tasks.TasksEndpoints.AccessTokenExpiry(TestHost.Token(expires: expires))!.Value.ShouldBe(new DateTimeOffset(expires, TimeSpan.Zero), TimeSpan.FromSeconds(1));
        Api.Features.Tasks.TasksEndpoints.AccessTokenExpiry("not-a-jwt").ShouldBeNull();
    }

    private const string UploadRequest = """
        {"attachmentItem":{"@odata.type":"#microsoft.graph.fileAttachmentUploadProperties","attachmentType":"file","contentType":"message/rfc822","name":"big.eml","size":4096}}
        """;

    private UploadSession GivenSession(string mailbox)
    {
        var session = new UploadSession("session-token", DateTimeOffset.UtcNow.AddMinutes(15), 4096);
        _attachments.BeginUploadAsync(new MailboxRef(mailbox), List, Task1, "big.eml", "message/rfc822", 4096, Arg.Any<CancellationToken>()).Returns(session);
        return session;
    }

    private async Task<string> CreateUploadUrlAsync(string prefix)
    {
        var created = await _host.AuthorizedClient().PostAsync(prefix + "/todo/lists/list-1/tasks/task-1/attachments/createUploadSession", Json(UploadRequest), Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.ReadObjectAsync()).Str("uploadUrl")!;
    }

    private async Task<HttpResponseMessage> PutAsync(string url, int length, string? contentRange)
    {
        using var anonymous = _host.App.GetTestClient();
        var content = new ByteArrayContent(new byte[length]);
        content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        if (contentRange is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Range", contentRange);
        }

        return await anonymous.PutAsync(Relative(url), content, Ct);
    }

    private static string Relative(string url) => url.StartsWith(TestHost.GraphBase, StringComparison.Ordinal) ? "/v1.0" + url[TestHost.GraphBase.Length..] : url;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
