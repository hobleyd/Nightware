using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Jmap.Directory;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Directory;

/// <summary>
/// The recorded session shares bob's mailbox (account <c>d</c>, not personal) with alice: to her it is a shared mailbox,
/// as a Stalwart group account is to its members (phase 10 step 0).
/// </summary>
public sealed class JmapSharedMailboxDirectoryTests : IAsyncDisposable
{
    private const string Bob = "bob@nightware.localhost";
    private readonly JmapTestHost _host = new();

    public JmapSharedMailboxDirectoryTests() => _host.StubSession();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISharedMailboxDirectory Directory() => _host.Services.CreateScope().ServiceProvider.GetRequiredService<ISharedMailboxDirectory>();

    [Fact]
    public void Is_registered_as_the_port() => Directory().ShouldBeOfType<JmapSharedMailboxDirectory>();

    [Fact]
    public async Task A_shared_account_is_described_by_its_address_and_its_identity_s_name()
    {
        _host.StubApi($$"""[["Identity/get",{"accountId":"d","state":"s","list":[{"id":"b","name":"Bob's desk","email":"BOB@nightware.localhost"}],"notFound":[]},"c0"]]""");

        DirectoryUser user = (await Directory().GetAsync(" Bob@Nightware.localhost ", Ct)).Value;

        user.ShouldBe(new DirectoryUser(Bob, "Bob's desk", Bob) { Mail = Bob });
        var call = _host.ApiBodies().Single()["methodCalls"]![0]!;
        call[0]!.GetValue<string>().ShouldBe("Identity/get");
        call[1]!["accountId"]!.GetValue<string>().ShouldBe("d");
    }

    [Fact]
    public async Task Without_a_readable_identity_the_address_is_the_name()
    {
        _host.StubApi("""[["error",{"type":"forbidden"},"c0"]]""");

        (await Directory().GetAsync(Bob, Ct)).Value.DisplayName.ShouldBe(Bob);
    }

    [Theory]
    [InlineData("nobody@nightware.localhost")]
    [InlineData(JmapTestHost.AliceAddress)]
    public async Task Neither_an_unshared_mailbox_nor_the_caller_s_own_is_a_shared_mailbox(string address)
    {
        (await Directory().GetAsync(address, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _host.Requests("/jmap/", "POST").ShouldBeEmpty();
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
