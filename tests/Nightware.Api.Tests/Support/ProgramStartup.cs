using Microsoft.AspNetCore.Mvc.Testing;

namespace Nightware.Api.Tests.Support;

/// <summary>Why the real <c>Program</c> failed to start.</summary>
/// <remarks>
/// <c>WebApplicationFactory</c> can lose that reason: when <c>Program</c> fails to start, its entry
/// point disposes the host while <c>DeferredHost.StartAsync</c> is still reading the host's services,
/// and the <see cref="ObjectDisposedException"/> from that race is thrown instead. A fresh factory
/// gets another chance to report the real failure.
/// </remarks>
internal static class ProgramStartup
{
    private const int Attempts = 5;

    public static Exception Failure(Func<WebApplicationFactory<Program>> create)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var factory = create();
            var error = Should.Throw<Exception>(() => factory.CreateClient());
            if (error is not ObjectDisposedException { ObjectName: nameof(IServiceProvider) } || attempt == Attempts)
            {
                return error;
            }
        }
    }
}
