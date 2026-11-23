using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.OnlineMeetings;

public static class OnlineMeetingsServiceCollectionExtensions
{
    /// <summary>Registers the OnlineMeetings use cases (phase 26, ADR 0009): online meetings over Talk rooms, with their recordings and transcripts.</summary>
    public static IServiceCollection AddOnlineMeetingsUseCases(this IServiceCollection services) => services;
}
