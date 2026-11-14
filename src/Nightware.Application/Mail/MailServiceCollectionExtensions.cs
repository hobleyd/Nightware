using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Mail;

public static class MailServiceCollectionExtensions
{
    /// <summary>Registers the Mail use cases. Owned by the phase that builds Mail.</summary>
    public static IServiceCollection AddMailUseCases(this IServiceCollection services) => services;
}
