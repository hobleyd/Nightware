using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.MailboxSettings;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.MailboxSettings;

public sealed class JmapMailboxSettingsAccessTests : IAsyncDisposable
{
    private readonly JmapTestHost _host = new();

    public JmapMailboxSettingsAccessTests() => _host.StubSession();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMailboxSettingsAccess Access() => _host.Services.CreateScope().ServiceProvider.GetRequiredService<IMailboxSettingsAccess>();

    [Fact]
    public void Is_registered_as_the_port() =>
        Access().ShouldBeOfType<JmapMailboxSettingsAccess>();

    [Fact]
    public async Task The_callers_own_mailbox_is_allowed_from_the_session_alone()
    {
        (await Access().CheckAsync(new MailboxRef(JmapTestHost.AliceAddress), Ct)).IsSuccess.ShouldBeTrue();

        _host.Requests("/jmap/", "POST").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("nobody@nightware.localhost", ErrorKind.NotFound)]
    [InlineData("bob@nightware.localhost", ErrorKind.Forbidden)]
    public async Task A_mailbox_not_shared_with_vacation_rights_is_refused(string address, ErrorKind expected)
    {
        // The recorded session shares bob's mail with alice, but not his VacationResponse.
        (await Access().CheckAsync(new MailboxRef(address), Ct)).Error!.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task A_rejected_credential_is_not_access()
    {
        _host.Server.Reset();
        _host.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/jmap/session").UsingGet())
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(401));

        (await Access().CheckAsync(new MailboxRef(JmapTestHost.AliceAddress), Ct)).IsSuccess.ShouldBeFalse();
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
