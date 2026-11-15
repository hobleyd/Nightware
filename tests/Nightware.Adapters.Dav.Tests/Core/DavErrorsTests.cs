using System.Net;
using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Core;

public sealed class DavErrorsTests
{
    [Theory]
    [InlineData(400, ErrorKind.Validation)]
    [InlineData(401, ErrorKind.BackendAuthFailed)]
    [InlineData(403, ErrorKind.Forbidden)]
    [InlineData(404, ErrorKind.NotFound)]
    [InlineData(405, ErrorKind.Conflict)]
    [InlineData(409, ErrorKind.Conflict)]
    [InlineData(410, ErrorKind.NotFound)]
    [InlineData(412, ErrorKind.PreconditionFailed)]
    [InlineData(413, ErrorKind.PayloadTooLarge)]
    [InlineData(415, ErrorKind.Validation)]
    [InlineData(423, ErrorKind.Conflict)]
    [InlineData(429, ErrorKind.Throttled)]
    [InlineData(500, ErrorKind.BackendUnavailable)]
    [InlineData(502, ErrorKind.BackendUnavailable)]
    [InlineData(503, ErrorKind.BackendUnavailable)]
    [InlineData(507, ErrorKind.PayloadTooLarge)]
    [InlineData(418, ErrorKind.Unknown)]
    public void Status_codes_map(int status, ErrorKind kind) =>
        DavErrors.Map((HttpStatusCode)status, null, null).Kind.ShouldBe(kind);

    [Theory]
    [InlineData(CredentialStrategy.PassThrough, ErrorKind.BackendAuthFailed)]
    [InlineData(CredentialStrategy.TokenExchange, ErrorKind.BackendUnavailable)]
    [InlineData(CredentialStrategy.ServiceAccount, ErrorKind.BackendUnavailable)]
    public void A_401_is_a_reauthentication_only_for_the_callers_own_token(CredentialStrategy strategy, ErrorKind kind)
    {
        DavErrors.Map(HttpStatusCode.Unauthorized, null, null, DavRequestIntent.Default, strategy).Kind.ShouldBe(kind);
        DavErrors.Unauthorized(strategy).Kind.ShouldBe(kind);
    }

    [Fact]
    public void Create_only_412_is_a_conflict()
    {
        var error = DavErrors.Map(HttpStatusCode.PreconditionFailed, null, null, DavRequestIntent.CreateOnly);
        error.Kind.ShouldBe(ErrorKind.Conflict);
        error.Code.ShouldBe(DavErrors.ItemExistsCode);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(412)]
    public void Invalid_sync_token_is_sync_state_gone(int status) =>
        DavErrors.Map((HttpStatusCode)status, "<d:error xmlns:d=\"DAV:\"><d:valid-sync-token/></d:error>", null).Kind.ShouldBe(ErrorKind.SyncStateGone);

    [Fact]
    public void Uid_conflict_and_max_size_preconditions_win_over_status()
    {
        DavErrors.Map(HttpStatusCode.Forbidden, "<d:error xmlns:d=\"DAV:\" xmlns:cal=\"urn:ietf:params:xml:ns:caldav\"><cal:no-uid-conflict><d:href>/x.ics</d:href></cal:no-uid-conflict></d:error>", null)
            .Code.ShouldBe(DavErrors.UidConflictCode);
        DavErrors.Map(HttpStatusCode.Forbidden, "<d:error xmlns:d=\"DAV:\" xmlns:card=\"urn:ietf:params:xml:ns:carddav\"><card:max-resource-size/></d:error>", null)
            .Kind.ShouldBe(ErrorKind.PayloadTooLarge);
    }

    [Fact]
    public void Retry_after_is_carried_for_throttling_and_unavailability()
    {
        DavErrors.Map(HttpStatusCode.TooManyRequests, null, 30).RetryAfterSeconds.ShouldBe(30);
        DavErrors.Map(HttpStatusCode.ServiceUnavailable, null, 120).RetryAfterSeconds.ShouldBe(120);
    }

    [Fact]
    public void Retry_after_accepts_delta_seconds_and_http_dates()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using var delta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        delta.Headers.TryAddWithoutValidation("Retry-After", "17");
        DavErrors.RetryAfterSeconds(delta, time).ShouldBe(17);

        using var date = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        date.Headers.TryAddWithoutValidation("Retry-After", "Sun, 15 Nov 2026 12:01:30 GMT");
        DavErrors.RetryAfterSeconds(date, time).ShouldBe(90);

        using var none = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        DavErrors.RetryAfterSeconds(none, time).ShouldBeNull();
    }
}
