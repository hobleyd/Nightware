using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Mail.Write;

public static class MailWriteServiceCollectionExtensions
{
    /// <summary>Registers the mail write use cases. Owned by Phase 3.</summary>
    public static IServiceCollection AddMailWriteUseCases(this IServiceCollection services) => services;
}
