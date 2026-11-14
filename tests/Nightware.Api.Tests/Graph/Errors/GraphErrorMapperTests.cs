using Nightware.Api.Graph.Errors;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Graph.Errors;

public sealed class GraphErrorMapperTests
{
    public static TheoryData<ErrorKind, int, string> Canonical => new()
    {
        { ErrorKind.Unknown, 500, "generalException" },
        { ErrorKind.NotFound, 404, "ErrorItemNotFound" },
        { ErrorKind.Conflict, 409, "conflict" },
        { ErrorKind.Validation, 400, "invalidRequest" },
        { ErrorKind.Unsupported, 501, "notImplemented" },
        { ErrorKind.Unauthorized, 401, "InvalidAuthenticationToken" },
        { ErrorKind.Forbidden, 403, "ErrorAccessDenied" },
        { ErrorKind.SyncStateGone, 410, "syncStateNotFound" },
        { ErrorKind.PayloadTooLarge, 413, "requestEntityTooLarge" },
        { ErrorKind.Throttled, 429, "TooManyRequests" },
        { ErrorKind.BackendUnavailable, 503, "serviceNotAvailable" },
        { ErrorKind.BackendAuthFailed, 401, "InvalidAuthenticationToken" },
        { ErrorKind.PreconditionFailed, 412, "preconditionFailed" },
    };

    [Fact]
    public void Every_error_kind_has_a_mapping()
    {
        var mapped = Canonical.Select(row => row.Data.Item1).ToHashSet();

        Enum.GetValues<ErrorKind>().ShouldAllBe(kind => mapped.Contains(kind));
    }

    [Theory]
    [MemberData(nameof(Canonical))]
    public void Generic_codes_map_to_the_canonical_status_and_code(ErrorKind kind, int status, string code)
    {
        var descriptor = GraphErrorMapper.Map(new Error(kind, string.Empty, "detail"));

        descriptor.StatusCode.ShouldBe(status);
        descriptor.Code.ShouldBe(code);
        GraphErrorMapper.StatusFor(kind).ShouldBe(status);
    }

    [Fact]
    public void Domain_factories_produce_graph_codes()
    {
        GraphErrorMapper.Map(Error.NotFound("x")).Code.ShouldBe("ErrorItemNotFound");
        GraphErrorMapper.Map(Error.Forbidden("x")).Code.ShouldBe("ErrorAccessDenied");
        GraphErrorMapper.Map(Error.Validation("x")).Code.ShouldBe("invalidRequest");
        GraphErrorMapper.Map(Error.Unsupported("x")).Code.ShouldBe("notImplemented");
        GraphErrorMapper.Map(Error.Conflict("x")).Code.ShouldBe("conflict");
        GraphErrorMapper.Map(Error.SyncStateGone("x")).StatusCode.ShouldBe(410);
        GraphErrorMapper.Map(Error.BackendAuthFailed("x")).StatusCode.ShouldBe(401);
        GraphErrorMapper.Map(Error.BackendUnavailable("x")).StatusCode.ShouldBe(503);
    }

    [Theory]
    [InlineData(ErrorKind.NotFound, "Request_ResourceNotFound")]
    [InlineData(ErrorKind.Conflict, "ErrorFolderExists")]
    [InlineData(ErrorKind.Validation, "ErrorInvalidRecipients")]
    [InlineData(ErrorKind.Forbidden, "ErrorAccessDenied")]
    [InlineData(ErrorKind.PayloadTooLarge, "ErrorMessageSizeExceeded")]
    public void Specific_codes_pass_through_for_descriptive_kinds(ErrorKind kind, string code)
    {
        GraphErrorMapper.Map(new Error(kind, code, "m")).Code.ShouldBe(code);
    }

    [Theory]
    [InlineData(ErrorKind.SyncStateGone)]
    [InlineData(ErrorKind.BackendAuthFailed)]
    [InlineData(ErrorKind.Unauthorized)]
    [InlineData(ErrorKind.Throttled)]
    [InlineData(ErrorKind.BackendUnavailable)]
    [InlineData(ErrorKind.Unknown)]
    public void Semantic_kinds_always_use_the_canonical_code(ErrorKind kind)
    {
        GraphErrorMapper.Map(new Error(kind, "somethingElse", "m")).Code.ShouldNotBe("somethingElse");
    }

    [Theory]
    [InlineData(ErrorKind.Throttled)]
    [InlineData(ErrorKind.BackendUnavailable)]
    public void Throttling_and_unavailability_carry_retry_after(ErrorKind kind)
    {
        GraphErrorMapper.Map(new Error(kind, "c", "m")).RetryAfterSeconds.ShouldBe(GraphErrorMapper.DefaultRetryAfterSeconds);
        GraphErrorMapper.Map(new Error(kind, "c", "m") { RetryAfterSeconds = 42 }).RetryAfterSeconds.ShouldBe(42);
        GraphErrorMapper.Map(new Error(kind, "c", "m") { RetryAfterSeconds = 0 }).RetryAfterSeconds.ShouldBe(1);
    }

    [Fact]
    public void Other_kinds_have_no_retry_after()
    {
        GraphErrorMapper.Map(Error.NotFound("x") with { RetryAfterSeconds = 9 }).RetryAfterSeconds.ShouldBeNull();
    }

    [Fact]
    public void Unknown_errors_never_leak_their_message()
    {
        var descriptor = GraphErrorMapper.Map(new Error(ErrorKind.Unknown, "x", "NullReferenceException at Foo.cs:12"));

        descriptor.Message.ShouldBe("An unexpected error occurred.");
    }

    [Fact]
    public void Empty_messages_get_a_default()
    {
        GraphErrorMapper.Map(new Error(ErrorKind.NotFound, "x", " ")).Message.ShouldNotBeNullOrWhiteSpace();
        GraphErrorMapper.Map(new Error(ErrorKind.Unsupported, "x", string.Empty)).Message.ShouldNotBeNullOrWhiteSpace();
        GraphErrorMapper.Map(new Error(ErrorKind.Conflict, "x", string.Empty)).Message.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Out_of_range_kinds_are_treated_as_unknown()
    {
        GraphErrorMapper.Map(new Error((ErrorKind)999, "x", "m")).StatusCode.ShouldBe(500);
        GraphErrorMapper.StatusFor((ErrorKind)999).ShouldBe(500);
    }
}
