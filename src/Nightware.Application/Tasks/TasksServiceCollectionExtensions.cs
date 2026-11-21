using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Tasks;

public static class TasksServiceCollectionExtensions
{
    /// <summary>Registers the Tasks use cases. Owned by the phase that builds Tasks.</summary>
    public static IServiceCollection AddTasksUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<ListTaskLists>();
        services.TryAddScoped<ListTasks>();
        services.TryAddScoped<CreateTask>();
        services.TryAddScoped<UpdateTask>();
        services.TryAddScoped<ListTaskAttachments>();
        services.TryAddScoped<GetTaskAttachmentContent>();
        services.TryAddScoped<AddTaskAttachment>();
        services.TryAddScoped<BeginTaskAttachmentUpload>();
        services.TryAddScoped<UploadTaskAttachmentContent>();
        services.TryAddScoped<TaskListsUseCase>();
        services.TryAddScoped<GetTask>();
        services.TryAddScoped<DeleteTask>();
        services.TryAddScoped<GetTaskAttachment>();
        services.TryAddScoped<DeleteTaskAttachment>();
        services.TryAddScoped<ChecklistItemsUseCase>();
        services.TryAddScoped<LinkedResourcesUseCase>();
        return services;
    }
}
