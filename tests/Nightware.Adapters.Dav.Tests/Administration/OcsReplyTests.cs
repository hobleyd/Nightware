using System.Net;

using Nightware.Adapters.Dav.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Administration;

public sealed class OcsReplyTests
{
    [Theory]
    [InlineData(false, 200, 100, true)]
    [InlineData(false, 200, 200, false)]
    [InlineData(true, 200, 200, true)]
    [InlineData(true, 200, 100, false)]
    [InlineData(false, 200, 404, false)]
    [InlineData(true, 404, 998, false)]
    public void Success_is_the_versions_status_code(bool v2, int http, int ocs, bool expected) =>
        OcsReply.Parse(v2 ? OcsVersion.V2 : OcsVersion.V1, (HttpStatusCode)http, NextcloudAdminServer.Envelope(ocs)).Succeeded.ShouldBe(expected);

    [Fact]
    public void Parses_meta_and_data()
    {
        var reply = OcsReply.Parse(OcsVersion.V1, HttpStatusCode.OK, NextcloudAdminServer.Envelope(100, """{"id":"alice"}"""));

        reply.IsEnvelope.ShouldBeTrue();
        reply.StatusCode.ShouldBe(100);
        reply.Message.ShouldBe("OK");
        reply.Data!["id"]!.GetValue<string>().ShouldBe("alice");
    }

    [Fact]
    public void A_string_status_code_is_read()
    {
        var reply = OcsReply.Parse(OcsVersion.V1, HttpStatusCode.OK, """{"ocs":{"meta":{"status":"failure","statuscode":"998","message":""},"data":[]}}""");

        reply.IsOcsNotFound.ShouldBeTrue();
        reply.Message.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Not Found</html>")]
    [InlineData("""{"error":"x"}""")]
    [InlineData("""{"ocs":{"meta":{}}}""")]
    [InlineData("[1,2]")]
    public void A_body_that_is_not_an_envelope_keeps_only_the_http_status(string body)
    {
        var reply = OcsReply.Parse(OcsVersion.V2, HttpStatusCode.NotFound, body);

        reply.IsEnvelope.ShouldBeFalse();
        reply.IsOcsNotFound.ShouldBeFalse();
        reply.Succeeded.ShouldBeFalse();
        reply.ToError("missing").Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Theory]
    [InlineData(200, 404, ErrorKind.NotFound)]
    [InlineData(200, 998, ErrorKind.NotFound)]
    [InlineData(404, 998, ErrorKind.NotFound)]
    [InlineData(401, 997, ErrorKind.BackendUnavailable)]
    [InlineData(200, 403, ErrorKind.BackendUnavailable)]
    [InlineData(200, 101, ErrorKind.Validation)]
    [InlineData(400, 400, ErrorKind.Validation)]
    [InlineData(409, 409, ErrorKind.Conflict)]
    [InlineData(500, 996, ErrorKind.BackendUnavailable)]
    [InlineData(500, 999, ErrorKind.BackendUnavailable)]
    [InlineData(503, 503, ErrorKind.BackendUnavailable)]
    [InlineData(429, 429, ErrorKind.Throttled)]
    [InlineData(412, 412, ErrorKind.BackendUnavailable)]
    public void Maps_ocs_failures(int http, int ocs, ErrorKind kind) =>
        OcsReply.Parse(OcsVersion.V2, (HttpStatusCode)http, NextcloudAdminServer.Envelope(ocs, message: "detail")).ToError("missing").Kind.ShouldBe(kind);

    [Fact]
    public void A_bare_401_is_the_consoles_credential_not_the_users()
    {
        var error = OcsReply.Parse(OcsVersion.V1, HttpStatusCode.Unauthorized, "").ToError("missing");

        error.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        error.Message.ShouldContain("admin console's own credential");
    }

    [Fact]
    public void Retry_after_is_carried_on_throttling()
    {
        var error = OcsReply.Parse(OcsVersion.V1, HttpStatusCode.TooManyRequests, "", retryAfterSeconds: 30).ToError("missing");

        error.RetryAfterSeconds.ShouldBe(30);
    }
}
