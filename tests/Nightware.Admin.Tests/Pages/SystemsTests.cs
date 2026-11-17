using Nightware.Admin.Pages;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Tests.Pages;

public sealed class SystemsTests
{
    [Theory]
    [InlineData(BackendKind.Identity, "Sign-in (Keycloak)", "Sign-in")]
    [InlineData(BackendKind.Mail, "Mail (Stalwart)", "Mail")]
    [InlineData(BackendKind.Groupware, "Files & calendar (Nextcloud)", "Files & calendar")]
    public void Every_system_has_one_name_leading_with_what_it_does(BackendKind backend, string name, string shortName)
    {
        Systems.Name(backend).ShouldBe(name);
        Systems.ShortName(backend).ShouldBe(shortName);
    }
}
