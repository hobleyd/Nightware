using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Files.Drives;

namespace Nightware.Application.Files;

public static class FilesServiceCollectionExtensions
{
    /// <summary>Registers the Files use cases (Phase 7: documents linked from mail).</summary>
    public static IServiceCollection AddFilesUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<GetSharedItem>();
        services.TryAddScoped<PrepareSharedItemDownload>();
        services.TryAddScoped<DownloadSharedItem>();

        // Phase 20: the caller's OneDrive over Nextcloud Files (ADR 0008).
        services.TryAddScoped<GetDrive>();
        services.TryAddScoped<GetDriveItem>();
        services.TryAddScoped<ListDriveItemChildren>();
        services.TryAddScoped<PrepareDriveItemDownload>();
        services.TryAddScoped<DownloadDriveItem>();
        services.TryAddScoped<UploadDriveItemContent>();
        services.TryAddScoped<CreateDriveFolder>();
        services.TryAddScoped<UpdateDriveItem>();
        services.TryAddScoped<DeleteDriveItem>();
        services.TryAddScoped<CopyDriveItem>();
        services.TryAddScoped<SearchDrive>();
        services.TryAddScoped<QueryDriveItems>();
        services.TryAddScoped<ListRecentDriveItems>();
        services.TryAddScoped<ListSharedWithMe>();
        services.TryAddScoped<ListDriveItemVersions>();
        services.TryAddScoped<OpenDriveItemVersion>();
        services.TryAddScoped<RestoreDriveItemVersion>();
        services.TryAddScoped<CheckOutDriveItem>();
        services.TryAddScoped<CheckInDriveItem>();
        services.TryAddScoped<GetSpecialFolder>();
        services.TryAddScoped<GetDriveDelta>();
        services.TryAddScoped<OpenDriveThumbnail>();
        services.TryAddScoped<BeginDriveUpload>();
        services.TryAddScoped<UploadDriveChunk>();
        services.TryAddScoped<GetDriveUpload>();
        services.TryAddScoped<CancelDriveUpload>();
        services.TryAddScoped<CreateSharingLink>();
        services.TryAddScoped<InviteToDriveItem>();
        services.TryAddScoped<ListDriveItemPermissions>();
        services.TryAddScoped<RemoveDriveItemShare>();
        return services;
    }
}
