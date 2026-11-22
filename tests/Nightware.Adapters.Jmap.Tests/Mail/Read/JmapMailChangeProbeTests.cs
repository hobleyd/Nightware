using System.Text.Json.Nodes;

using Nightware.Adapters.Jmap.Mail.Read;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Application.Changes;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class JmapMailChangeProbeTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private readonly FakeJmapServer _jmap = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private JmapMailChangeProbe Probe() => new(_jmap.Client());

    [Fact]
    public async Task The_state_is_the_email_and_mailbox_states_read_in_one_request_of_no_ids()
    {
        _jmap.On("Email/get", """{"accountId":"c","state":"e9","list":[],"notFound":[]}""");
        _jmap.On("Mailbox/get", """{"accountId":"c","state":"m4","list":[],"notFound":[]}""");

        var state = await Probe().StateAsync(Alice, Ct);

        state.Value.ShouldBe("e9|m4");
        Probe().Area.ShouldBe(ChangeArea.Mail);
        _jmap.Requests().ShouldHaveSingleItem();
        _jmap.Single("Email/get")["ids"]!.AsArray().ShouldBeEmpty();
        _jmap.Single("Mailbox/get")["ids"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_method_error_is_the_probes_error()
    {
        _jmap.On("Email/get", _ => [("error", JsonNode.Parse("""{"type":"accountNotFound"}""")!)]);
        _jmap.On("Mailbox/get", """{"accountId":"c","state":"m4","list":[],"notFound":[]}""");

        (await Probe().StateAsync(Alice, Ct)).IsSuccess.ShouldBeFalse();
    }
}
