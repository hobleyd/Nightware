using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nightware.Adapters.Dav.Tasks.Attachments;
using Nightware.Application.Tasks;

namespace Nightware.Adapters.Dav.Tasks;

internal static class DavTasksServiceCollectionExtensions
{
    /// <summary>Registers this feature's port implementations. Owned by Phase 5B.</summary>
    public static IServiceCollection AddDavTasks(this IServiceCollection services)
    {
        services.TryAddScoped<DavTaskCollections>();
        services.TryAddScoped<DavTaskStore>();
        services.TryAddScoped<TaskAttachmentFiles>();
        services.TryAddScoped<DavTaskListReader>();
        services.TryAddScoped<ITaskListReader>(static sp => sp.GetRequiredService<DavTaskListReader>());
        services.TryAddScoped<ITaskListStore>(static sp => sp.GetRequiredService<DavTaskListReader>());
        services.TryAddScoped<IChecklistStore, DavChecklistStore>();
        services.TryAddScoped<ILinkedResourceStore, DavLinkedResourceStore>();
        services.TryAddScoped<ITaskDelta, DavTaskDelta>();
        services.TryAddScoped<ITaskStore>(sp => sp.GetRequiredService<DavTaskStore>());
        services.TryAddScoped<ITaskAttachmentStore, DavTaskAttachmentStore>();
        return services;
    }
}
