using Nightware.Hosting.Observability;

namespace Nightware.Hosting.Tests.Observability;

public sealed class UrlRedactionTests
{
    [Theory]
    [InlineData("/v1.0/shares/u!aHR0cHM6Ly9j/driveItem", "/v1.0/shares/{redacted}/driveItem")]
    [InlineData("/beta/SHARES/u!aHR0cHM6Ly9j/driveItem/$value", "/beta/SHARES/{redacted}/driveItem/$value")]
    [InlineData("/v1.0/shares/u!aHR0cHM6Ly9j", "/v1.0/shares/{redacted}")]
    [InlineData("RequestPath:/v1.0/shares/u!abc/driveItem RequestId:42", "RequestPath:/v1.0/shares/{redacted}/driveItem RequestId:42")]
    [InlineData("http://nextcloud/public.php/dav/files/AbCdEf/x.txt", "http://nextcloud/public.php/dav/files/{redacted}/x.txt")]
    [InlineData("for the request path '/v1.0/shares/u!abc/driveItem'", "for the request path '/v1.0/shares/{redacted}/driveItem'")]
    [InlineData("/v1.0/shares/{redacted}/driveItem", "/v1.0/shares/{redacted}/driveItem")]
    // A share id sent as standard base64 can carry a literal '/': the whole id, tail included, is redacted.
    [InlineData("/v1.0/shares/u!aHR0cHM6/Ly9jbG91ZC90b2tlbg/driveItem", "/v1.0/shares/{redacted}/driveItem")]
    [InlineData("/v1.0/shares/u!aHR0cHM6/Ly9jbG91ZC90b2tlbg", "/v1.0/shares/{redacted}")]
    [InlineData("RequestPath:/v1.0/shares/u!a/b/driveItem/content RequestId:42", "RequestPath:/v1.0/shares/{redacted}/driveItem/content RequestId:42")]
    public void Replaces_share_ids_and_public_share_tokens(string text, string expected) =>
        UrlRedaction.Redact(text).ShouldBe(expected);

    [Theory]
    [InlineData("/v1.0/me/messages/AAMk")]
    [InlineData("/v1.0/users/a@b.c/todo/lists/l/tasks/t/attachmentSessions/s/content")]
    [InlineData("/remote.php/dav/files/alice/x.txt")]
    [InlineData("")]
    public void Leaves_everything_else_alone(string text) =>
        UrlRedaction.Redact(text).ShouldBeSameAs(text);

    [Fact]
    public void A_url_loses_its_query_as_httpclient_logging_does() =>
        UrlRedaction.Redact(new Uri("http://nextcloud:8080/public.php/dav/files/AbCdEf?x=secret"))
            .ShouldBe("http://nextcloud:8080/public.php/dav/files/{redacted}?*");
}
