using System.Text;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Jmap.Tests.Core.Session;

public sealed class JmapSessionTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Session_is_fetched_with_the_credential_and_cached_across_requests()
    {
        await using var host = new JmapTestHost();
        host.StubSession();

        var first = await host.Client().GetSessionAsync(Ct);
        var second = await host.Client().GetSessionAsync(Ct);

        first.IsSuccess.ShouldBeTrue();
        second.Value.ShouldBeSameAs(first.Value);
        var fetches = host.Requests("/jmap/session", "GET");
        fetches.Count.ShouldBe(1);
        JmapTestHost.Authorization(fetches[0]).ShouldBe("Bearer alice-token");
    }

    [Fact]
    public async Task Sessions_are_cached_per_credential_identity()
    {
        await using var host = new JmapTestHost();
        host.StubSession();

        await host.Client().GetSessionAsync(Ct);
        host.UseCredential(new BackendCredential("Bearer", "bob-token", CredentialStrategy.TokenExchange));
        await host.Client().GetSessionAsync(Ct);

        host.Requests("/jmap/session", "GET").Select(JmapTestHost.Authorization).ShouldBe(["Bearer alice-token", "Bearer bob-token"]);
    }

    [Fact]
    public async Task Session_urls_are_moved_onto_the_configured_origin()
    {
        await using var host = new JmapTestHost();
        host.StubSession();

        var session = (await host.Client().GetSessionAsync(Ct)).Value;

        session.ApiUrl.ShouldBe(new Uri($"{host.Server.Url}/jmap/"));
        session.UploadUrl("c").ShouldBe(new Uri($"{host.Server.Url}/jmap/upload/c/"));
        session.Limits.MaxObjectsInGet.ShouldBe(500);
        session.Limits.MaxCallsInRequest.ShouldBe(16);
        session.Limits.MaxSizeUpload.ShouldBe(50_000_000);
    }

    [Fact]
    public async Task Well_known_redirect_is_followed_with_the_credential_reapplied()
    {
        await using var host = new JmapTestHost(sessionPath: "/.well-known/jmap");
        host.Server.Given(Request.Create().WithPath("/.well-known/jmap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(307).WithHeader("Location", "/jmap/session"));
        host.StubSession();

        var session = await host.Client().GetSessionAsync(Ct);

        session.IsSuccess.ShouldBeTrue();
        session.Value.ApiUrl.ShouldBe(new Uri($"{host.Server.Url}/jmap/"));
        JmapTestHost.Authorization(host.Requests("/jmap/session", "GET").ShouldHaveSingleItem()).ShouldBe("Bearer alice-token");
    }

    [Fact]
    public async Task Redirects_to_another_origin_are_not_followed()
    {
        await using var host = new JmapTestHost(sessionPath: "/.well-known/jmap");
        host.Server.Given(Request.Create().WithPath("/.well-known/jmap").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", "https://evil.example/jmap/session"));

        var session = await host.Client().GetSessionAsync(Ct);

        session.Error!.Kind.ShouldBe(ErrorKind.Unknown);
        host.Server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Redirect_loops_stop()
    {
        await using var host = new JmapTestHost(sessionPath: "/loop");
        host.Server.Given(Request.Create().WithPath("/loop").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(307).WithHeader("Location", "/loop"));

        (await host.Client().GetSessionAsync(Ct)).IsSuccess.ShouldBeFalse();
        host.Server.LogEntries.Count.ShouldBe(4);
    }

    [Fact]
    public async Task Session_401_is_backend_auth_failed()
    {
        await using var host = new JmapTestHost();
        host.Server.Given(Request.Create().WithPath("/jmap/session").UsingGet()).RespondWith(Response.Create().WithStatusCode(401));

        var session = await host.Client().GetSessionAsync(Ct);

        session.Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
    }

    [Fact]
    public async Task Failed_session_fetches_are_not_cached()
    {
        await using var host = new JmapTestHost();
        host.Server.Given(Request.Create().WithPath("/jmap/session").UsingGet())
            .InScenario("s").WillSetStateTo("up")
            .RespondWith(Response.Create().WithStatusCode(503));
        host.Server.Given(Request.Create().WithPath("/jmap/session").UsingGet())
            .InScenario("s").WhenStateIs("up")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(Fixtures.AliceSession));

        (await host.Client().GetSessionAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await host.Client().GetSessionAsync(Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Unreadable_session_document_is_backend_unavailable()
    {
        await using var host = new JmapTestHost();
        host.StubSession("{\"capabilities\":{}}");

        (await host.Client().GetSessionAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Credential_failures_are_returned_without_calling_the_server()
    {
        await using var host = new JmapTestHost();
        host.Credentials.ForAsync(Backend.Mail, Arg.Any<CancellationToken>())
            .Returns(Result<BackendCredential>.Failure(Error.BackendAuthFailed("exchange failed")));

        (await host.Client().GetSessionAsync(Ct)).Error!.Message.ShouldBe("exchange failed");
        host.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_credential_is_requested_once_per_scope()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        var client = host.Client();

        await client.GetSessionAsync(Ct);
        await client.GetSessionAsync(Ct);

        await host.Credentials.Received(1).ForAsync(Backend.Mail, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Basic_master_user_credentials_reach_the_server()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.UseCredential(new BackendCredential("Basic", "admin:secret", CredentialStrategy.ServiceAccount, JmapTestHost.AliceAddress));

        await host.Client().GetSessionAsync(Ct);

        var header = JmapTestHost.Authorization(host.Requests("/jmap/session", "GET")[0])!;
        header.ShouldStartWith("Basic ");
        Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..])).ShouldBe("alice@nightware.localhost%admin:secret");
    }

    [Fact]
    public void Create_resolves_relative_urls_and_can_keep_advertised_origins()
    {
        var document = new JmapSessionDocument
        {
            ApiUrl = "/jmap/",
            UploadUrl = "jmap/upload/{accountId}/",
            DownloadUrl = "https://public.example/jmap/download/{accountId}/{blobId}/{name}?accept={type}",
        };

        var kept = JmapSession.Create(document, new Uri("http://stalwart:8080/jmap/session"), useSessionOrigin: false, 7, 3);

        kept.ApiUrl.ShouldBe(new Uri("http://stalwart:8080/jmap/"));
        kept.UploadUrl("a").ShouldBe(new Uri("http://stalwart:8080/jmap/upload/a/"));
        kept.DownloadUrl("a", "b", "x.pdf", "application/pdf").ShouldBe(new Uri("https://public.example/jmap/download/a/b/x.pdf?accept=application%2Fpdf"));
        kept.Limits.MaxObjectsInGet.ShouldBe(7);
        kept.Limits.MaxCallsInRequest.ShouldBe(3);
    }

    [Fact]
    public void Template_values_are_percent_encoded()
    {
        JmapSession.Expand("/d/{accountId}/{blobId}/{name}?accept={type}", ("accountId", "c"), ("blobId", "B 1"), ("name", "Q3 report/v2.pdf"), ("type", "text/plain; charset=utf-8"))
            .ShouldBe("/d/c/B%201/Q3%20report%2Fv2.pdf?accept=text%2Fplain%3B%20charset%3Dutf-8");
    }

    [Fact]
    public void Create_rejects_documents_without_urls()
    {
        Should.Throw<System.Text.Json.JsonException>(() => JmapSession.Create(new JmapSessionDocument(), new Uri("http://x/"), true, 1, 1));
    }

    [Fact]
    public void Account_capabilities_are_typed()
    {
        var document = System.Text.Json.JsonSerializer.Deserialize(Fixtures.AliceSession, JmapJsonContext.Default.JmapSessionDocument)!;
        var session = JmapSession.Create(document, new Uri("http://x/jmap/session"), true, 1, 1);

        session.AccountCapability("c", JmapCapabilities.Mail, JmapJsonContext.Default.MailAccountCapability)!.MaxSizeMailboxName.ShouldBe(255);
        session.AccountCapability("d", JmapCapabilities.Submission, JmapJsonContext.Default.SubmissionAccountCapability).ShouldBeNull();
        session.AccountCapability("zz", JmapCapabilities.Mail, JmapJsonContext.Default.MailAccountCapability).ShouldBeNull();
        session.HasCapability(JmapCapabilities.Sieve).ShouldBeTrue();
        session.PrimaryAccountId(JmapCapabilities.VacationResponse).ShouldBe("c");
        session.PrimaryAccountId("urn:x").ShouldBeNull();
    }
}
