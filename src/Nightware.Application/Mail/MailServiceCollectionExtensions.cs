using Microsoft.Extensions.DependencyInjection;
using Nightware.Application.Mail.Read;
using Nightware.Application.Mail.Write;

namespace Nightware.Application.Mail;

public static class MailServiceCollectionExtensions
{
    /// <summary>Registers the Mail use cases: read side (Phase 2), write side (Phase 3) and id translation (phase 19).</summary>
    public static IServiceCollection AddMailUseCases(this IServiceCollection services) =>
        services.AddMailReadUseCases().AddMailWriteUseCases().AddScoped<TranslateExchangeIds>();
}
