using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Tasks;

public static class TasksServiceCollectionExtensions
{
    /// <summary>Registers the Tasks use cases. Owned by the phase that builds Tasks.</summary>
    public static IServiceCollection AddTasksUseCases(this IServiceCollection services) => services;
}
