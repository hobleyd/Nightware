using System.Text.Json;

using Nightware.Adapters.Jmap.Administration;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Tests.Support;

namespace Nightware.Adapters.Jmap.Tests.Administration;

public sealed class ManagedAccountTests
{
    private static JmapSession Console() =>
        JmapSession.Create(JsonSerializer.Deserialize(JmapTestHost.Session(), JmapJsonContext.Default.JmapSessionDocument)!, new Uri("http://stalwart:8080/jmap/session"), true, 500, 16);

    [Fact]
    public void The_target_account_is_primary_with_the_servers_mail_sieve_and_vacation_capabilities()
    {
        var console = Console();

        var account = ManagedAccount.For(console, "x9", "support@acme.example");

        (account.AccountId, account.IsPrimary, account.IsReadOnly).ShouldBe(("x9", true, false));
        foreach (var capability in new[] { JmapCapabilities.Mail, JmapCapabilities.VacationResponse })
        {
            account.Session.HasAccountCapability("x9", capability).ShouldBe(console.HasCapability(capability), capability);
        }

        account.Session.PrimaryAccountId(JmapCapabilities.Mail).ShouldBe("x9");
        account.Session.UploadUrl("x9").ShouldBe(console.UploadUrl("x9"), "the server's own URLs are kept");
    }
}
