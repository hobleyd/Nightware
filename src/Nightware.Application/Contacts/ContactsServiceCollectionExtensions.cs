using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Contacts;

public static class ContactsServiceCollectionExtensions
{
    /// <summary>Registers the Contacts use cases. Owned by the phase that builds Contacts.</summary>
    public static IServiceCollection AddContactsUseCases(this IServiceCollection services) => services;
}
