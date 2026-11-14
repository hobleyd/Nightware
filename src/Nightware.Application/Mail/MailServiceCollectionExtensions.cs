using Microsoft.Extensions.DependencyInjection;
using Nightware.Application.Mail.Read;
using Nightware.Application.Mail.Write;

namespace Nightware.Application.Mail;

public static class MailServiceCollectionExtensions
{
    /// <summary>Registers the Mail use cases: read side (Phase 2) and write side (Phase 3).</summary>
    public static IServiceCollection AddMailUseCases(this IServiceCollection services) =>
        services.AddMailReadUseCases().AddMailWriteUseCases();
}
