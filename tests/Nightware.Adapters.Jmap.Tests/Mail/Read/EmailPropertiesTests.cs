using System.Text.Json;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Read;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class EmailPropertiesTests
{
    private static JmapAccount Account(string session)
    {
        var parsed = JmapSession.Create(
            JsonSerializer.Deserialize(session, JmapJsonContext.Default.JmapSessionDocument)!,
            new Uri("http://stalwart:8080/jmap/session"),
            useSessionOrigin: true,
            500,
            16);
        return new JmapAccount("c", "alice", IsPrimary: true, IsReadOnly: false, parsed);
    }

    [Fact]
    public void Metadata_is_asked_for_and_declared_only_where_the_account_keeps_it()
    {
        var plain = Account(Fixtures.AliceSession);
        var keeping = Account(JmapTestHost.SessionWithMetadata());

        plain.SupportsMetadata.ShouldBeFalse();
        keeping.SupportsMetadata.ShouldBeTrue();
        EmailProperties.ListRequest(plain).Properties!.ShouldNotContain(EmailProperties.MetadataProperty);
        EmailProperties.ListRequest(keeping).Properties!.ShouldContain(EmailProperties.MetadataProperty);
        EmailProperties.FullRequest(keeping, "e1", MessageFetch.Default).Properties!.ShouldContain(EmailProperties.MetadataProperty);
        EmailProperties.FullRequest(plain, "e1", MessageFetch.Default).Properties!.ShouldNotContain(EmailProperties.MetadataProperty);
        EmailProperties.Get(plain).ShouldBeSameAs(JmapMethods.EmailGet);
        EmailProperties.Get(keeping).Capabilities.ShouldContain(JmapCapabilities.Metadata);
    }
}
