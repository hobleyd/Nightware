using Nightware.Adapters.Jmap.Administration;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;

namespace Nightware.Adapters.Jmap.Tests.Administration;

public sealed class StalwartBackendStatusTests : IAsyncDisposable
{
    private readonly StalwartManagementTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private StalwartBackendStatus Status() => _host.Get<IEnumerable<IBackendStatus>>().OfType<StalwartBackendStatus>().Single();

    [Fact]
    public async Task Healthy_when_the_principal_can_query_accounts()
    {
        _host.Stub("x:Account/query", """[["x:Account/query",{"ids":["a1"],"position":0},"c0"]]""");

        (await Status().CheckAsync(Ct)).ShouldBe(new BackendHealth(BackendKind.Mail, Healthy: true));

        var call = _host.Calls().Single();
        call[0]!.GetValue<string>().ShouldBe("x:Account/query");
        call[1]!["limit"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task Unhealthy_when_the_credential_is_rejected()
    {
        _host.StubStatus(401);

        var health = await Status().CheckAsync(Ct);

        health.Healthy.ShouldBeFalse();
        health.Backend.ShouldBe(BackendKind.Mail);
        health.Detail!.ShouldContain("management credential");
    }

    [Fact]
    public async Task Unhealthy_when_the_principal_lacks_the_permission()
    {
        _host.Stub("x:Account/query", """[["error",{"type":"forbidden","description":"sysAccountQuery"},"c0"]]""");

        var health = await Status().CheckAsync(Ct);

        health.Healthy.ShouldBeFalse();
        health.Detail!.ShouldContain("forbidden");
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}