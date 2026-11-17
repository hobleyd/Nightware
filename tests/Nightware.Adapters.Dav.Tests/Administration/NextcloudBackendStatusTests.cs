using Nightware.Application.Administration;
using Nightware.Domain.Administration;

namespace Nightware.Adapters.Dav.Tests.Administration;

public sealed class NextcloudBackendStatusTests : IDisposable
{
    private const string CurrentUser = "/ocs/v1.php/cloud/user";

    private readonly NextcloudAdminServer _nextcloud = new();

    public void Dispose() => _nextcloud.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IBackendStatus Status => _nextcloud.Get<IBackendStatus>();

    [Fact]
    public async Task Healthy_when_the_console_account_signs_in()
    {
        _nextcloud.Answer("GET", CurrentUser, 200, 100, """{"id":"nightware-admin","enabled":true,"backend":"Database","groups":["admin"]}""");

        (await Status.CheckAsync(Ct)).ShouldBe(new BackendHealth(BackendKind.Groupware, true));

        var request = _nextcloud.Requests.Single().RequestMessage!;
        request.Headers!["Authorization"].ShouldBe([NextcloudAdminServer.BasicHeader]);
        request.Headers["OCS-APIRequest"].ShouldBe(["true"]);
        request.Query!["format"].ShouldBe(["json"]);
    }

    [Theory]
    [InlineData("""{"id":"nightware-admin","groups":["users"]}""")]
    [InlineData("""{"id":"nightware-admin","groups":[]}""")]
    [InlineData("""{"id":"nightware-admin"}""")]
    public async Task Unhealthy_when_the_console_account_is_not_an_administrator(string data)
    {
        // It would sign in, but see no other user (998), so every user would look Missing.
        _nextcloud.Answer("GET", CurrentUser, 200, 100, data);

        var health = await Status.CheckAsync(Ct);

        health.Healthy.ShouldBeFalse();
        health.Detail!.ShouldContain("'admin' group");
    }

    [Fact]
    public async Task Unhealthy_when_the_credential_is_rejected()
    {
        _nextcloud.Answer("GET", CurrentUser, 401, 997, message: "Unauthorised");

        var health = await Status.CheckAsync(Ct);

        health.Healthy.ShouldBeFalse();
        health.Backend.ShouldBe(BackendKind.Groupware);
        health.Detail!.ShouldContain("credential");
        health.Detail!.ShouldNotContain(NextcloudAdminServer.Password);
    }

    [Fact]
    public async Task Unhealthy_on_a_server_error()
    {
        _nextcloud.AnswerRaw("GET", CurrentUser, 500, "oops", "text/plain");

        (await Status.CheckAsync(Ct)).Healthy.ShouldBeFalse();
    }

    [Fact]
    public async Task Unhealthy_and_no_exception_when_unreachable()
    {
        var status = Status;
        _nextcloud.Server.Stop();

        var health = await status.CheckAsync(Ct);

        health.Healthy.ShouldBeFalse();
        health.Detail.ShouldNotBeNullOrWhiteSpace();
    }
}
