using System.Net;
using System.Net.Http.Headers;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Core.Errors;

public sealed class JmapErrorsTests
{
    [Theory]
    [InlineData("cannotCalculateChanges", ErrorKind.SyncStateGone)]
    [InlineData("tooManyChanges", ErrorKind.SyncStateGone)]
    [InlineData("accountNotFound", ErrorKind.NotFound)]
    [InlineData("fromAccountNotFound", ErrorKind.NotFound)]
    [InlineData("forbidden", ErrorKind.Forbidden)]
    [InlineData("accountReadOnly", ErrorKind.Forbidden)]
    [InlineData("accountNotSupportedByMethod", ErrorKind.Unsupported)]
    [InlineData("unknownMethod", ErrorKind.Unsupported)]
    [InlineData("unsupportedFilter", ErrorKind.Unsupported)]
    [InlineData("unsupportedSort", ErrorKind.Unsupported)]
    [InlineData("invalidArguments", ErrorKind.Validation)]
    [InlineData("anchorNotFound", ErrorKind.Validation)]
    [InlineData("requestTooLarge", ErrorKind.PayloadTooLarge)]
    [InlineData("stateMismatch", ErrorKind.PreconditionFailed)]
    [InlineData("rateLimit", ErrorKind.Throttled)]
    [InlineData("serverUnavailable", ErrorKind.BackendUnavailable)]
    [InlineData("serverFail", ErrorKind.Unknown)]
    [InlineData("serverPartialFail", ErrorKind.Unknown)]
    [InlineData("invalidResultReference", ErrorKind.Unknown)]
    [InlineData("somethingNew", ErrorKind.Unknown)]
    public void Method_errors_map_to_error_kinds(string type, ErrorKind expected)
    {
        var error = JmapErrors.FromMethodError(new MethodError { Type = type, Description = "detail" }, "Email/get");

        error.Kind.ShouldBe(expected);
        error.Message.ShouldContain("Email/get");
        error.Message.ShouldContain(type);
    }

    [Fact]
    public void Sync_state_gone_uses_the_graph_code()
    {
        JmapErrors.FromMethodError(new MethodError { Type = "cannotCalculateChanges" }, "Email/changes").Code.ShouldBe("syncStateNotFound");
    }

    [Theory]
    [InlineData("notFound", ErrorKind.NotFound)]
    [InlineData("blobNotFound", ErrorKind.NotFound)]
    [InlineData("forbidden", ErrorKind.Forbidden)]
    [InlineData("forbiddenFrom", ErrorKind.Forbidden)]
    [InlineData("forbiddenMailFrom", ErrorKind.Forbidden)]
    [InlineData("forbiddenToSend", ErrorKind.Forbidden)]
    [InlineData("overQuota", ErrorKind.PayloadTooLarge)]
    [InlineData("tooLarge", ErrorKind.PayloadTooLarge)]
    [InlineData("rateLimit", ErrorKind.Throttled)]
    [InlineData("invalidPatch", ErrorKind.Validation)]
    [InlineData("singleton", ErrorKind.Validation)]
    [InlineData("tooManyKeywords", ErrorKind.Validation)]
    [InlineData("tooManyMailboxes", ErrorKind.Validation)]
    [InlineData("invalidEmail", ErrorKind.Validation)]
    [InlineData("tooManyRecipients", ErrorKind.Validation)]
    [InlineData("noRecipients", ErrorKind.Validation)]
    [InlineData("invalidRecipients", ErrorKind.Validation)]
    [InlineData("willDestroy", ErrorKind.Conflict)]
    [InlineData("mailboxHasChild", ErrorKind.Conflict)]
    [InlineData("mailboxHasEmail", ErrorKind.Conflict)]
    [InlineData("alreadyExists", ErrorKind.Conflict)]
    [InlineData("cannotUnsend", ErrorKind.Conflict)]
    [InlineData("stateMismatch", ErrorKind.PreconditionFailed)]
    [InlineData("whatever", ErrorKind.Unknown)]
    public void Set_errors_map_to_error_kinds(string type, ErrorKind expected)
    {
        var error = JmapErrors.FromSetError(new SetError { Type = type }, "M1");

        error.Kind.ShouldBe(expected);
        error.Message.ShouldContain("M1");
    }

    [Fact]
    public void Invalid_properties_name_the_properties()
    {
        var error = JmapErrors.FromSetError(new SetError { Type = "invalidProperties", Properties = ["name", "parentId"] }, "k1");

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Target.ShouldBe("name,parentId");
    }

    [Fact]
    public void Over_quota_has_a_quota_code()
    {
        JmapErrors.FromSetError(new SetError { Type = "overQuota" }, "x").Code.ShouldBe("ErrorQuotaExceeded");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ErrorKind.BackendAuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, ErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, ErrorKind.NotFound)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, ErrorKind.PayloadTooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorKind.Throttled)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorKind.BackendUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, ErrorKind.BackendUnavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ErrorKind.BackendUnavailable)]
    [InlineData(HttpStatusCode.Conflict, ErrorKind.Unknown)]
    public void Http_statuses_map_to_error_kinds(HttpStatusCode status, ErrorKind expected)
    {
        JmapErrors.FromStatus(status, 7, "ctx").Kind.ShouldBe(expected);
    }

    [Fact]
    public void Retry_after_is_kept_for_throttling_and_unavailability()
    {
        JmapErrors.FromStatus(HttpStatusCode.TooManyRequests, 7, "x").RetryAfterSeconds.ShouldBe(7);
        JmapErrors.FromStatus(HttpStatusCode.ServiceUnavailable, 9, "x").RetryAfterSeconds.ShouldBe(9);
    }

    [Theory]
    [InlineData("urn:ietf:params:jmap:error:unknownCapability", null, ErrorKind.Unsupported)]
    [InlineData("urn:ietf:params:jmap:error:notJSON", null, ErrorKind.Unknown)]
    [InlineData("urn:ietf:params:jmap:error:notRequest", null, ErrorKind.Unknown)]
    [InlineData("urn:ietf:params:jmap:error:limit", "maxSizeRequest", ErrorKind.PayloadTooLarge)]
    [InlineData("urn:ietf:params:jmap:error:limit", "maxSizeUpload", ErrorKind.PayloadTooLarge)]
    [InlineData("urn:ietf:params:jmap:error:limit", "maxConcurrentRequests", ErrorKind.Throttled)]
    [InlineData("urn:ietf:params:jmap:error:limit", "maxConcurrentUpload", ErrorKind.Throttled)]
    [InlineData("urn:ietf:params:jmap:error:limit", "maxCallsInRequest", ErrorKind.Unknown)]
    [InlineData("about:blank", null, ErrorKind.Unknown)]
    public void Problem_details_map_to_error_kinds(string type, string? limit, ErrorKind expected)
    {
        var problem = new ProblemDetails { Type = type, Status = 400, Detail = "d", Limit = limit };

        JmapErrors.FromProblem(problem, HttpStatusCode.BadRequest, null).Kind.ShouldBe(expected);
    }

    [Fact]
    public void Unknown_problem_types_fall_back_to_the_status()
    {
        var problem = new ProblemDetails { Type = "urn:ietf:params:jmap:error:new", Status = 503 };

        JmapErrors.FromProblem(problem, HttpStatusCode.ServiceUnavailable, 3).Kind.ShouldBe(ErrorKind.BackendUnavailable);
        JmapErrors.FromProblem(new ProblemDetails(), HttpStatusCode.Unauthorized, null).Kind.ShouldBe(ErrorKind.BackendAuthFailed);
    }

    [Fact]
    public void Retry_after_reads_deltas_and_dates()
    {
        var now = new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        using var delta = new HttpResponseMessage();
        delta.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
        using var date = new HttpResponseMessage();
        date.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(30.2));
        using var past = new HttpResponseMessage();
        past.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(-5));
        using var none = new HttpResponseMessage();

        JmapErrors.RetryAfterSeconds(delta, now).ShouldBe(12);
        JmapErrors.RetryAfterSeconds(date, now).ShouldBe(31);
        JmapErrors.RetryAfterSeconds(past, now).ShouldBe(0);
        JmapErrors.RetryAfterSeconds(none, now).ShouldBeNull();
    }
}
