using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.TestSupport;

internal sealed class FakeUserContext : IUserContext
{
    public Principal Principal { get; set; } = new("5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11", "alice@nightware.localhost", "alice@nightware.localhost", "Alice Anderson");

    public string BearerToken { get; set; } = Jwt.WithExpiry(DateTimeOffset.UtcNow.AddMinutes(15));
}
