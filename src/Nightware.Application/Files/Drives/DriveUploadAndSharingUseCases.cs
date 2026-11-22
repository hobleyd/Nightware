using Nightware.Application.Mail.Write;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Files.Drives;

/// <summary>Graph <c>POST …:/{name}:/createUploadSession</c>.</summary>
public sealed class BeginDriveUpload(IDriveUploads uploads)
{
    public async Task<Result<DriveUploadState>> ExecuteAsync(DriveUploadFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!DriveItemRef.IsValidName(file.Name))
        {
            return DriveErrors.InvalidName("item.name");
        }

        if (file.Size < 0)
        {
            return Error.Validation("fileSize can't be negative.", "item.fileSize");
        }

        return file.Size > DriveLimits.UploadSessionMaxBytes
            ? DriveErrors.TooLarge(DriveLimits.UploadSessionMaxBytes)
            : await uploads.BeginAsync(file, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>PUT {uploadUrl}</c> for a drive upload: one chunk, named by its <c>Content-Range</c>.</summary>
public sealed class UploadDriveChunk(IDriveUploads uploads)
{
    public async Task<Result<DriveUploadProgress>> ExecuteAsync(string sessionId, ContentRangeHeader range, Stream chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(chunk);
        var length = range.Last - range.First + 1;
        if (range.First < 0 || length <= 0 || range.Last >= range.Total)
        {
            return Error.Validation("The Content-Range is not a range of the file.", "Content-Range");
        }

        if (range.Total > DriveLimits.UploadSessionMaxBytes)
        {
            return DriveErrors.TooLarge(DriveLimits.UploadSessionMaxBytes);
        }

        return length > DriveLimits.MaxChunkBytes
            ? Error.Validation($"A chunk can be at most {DriveLimits.MaxChunkBytes} bytes.", "Content-Range")
            : await uploads.AppendAsync(sessionId, range.First, length, range.Total, chunk, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>GET {uploadUrl}</c>: the session's next expected range.</summary>
public sealed class GetDriveUpload(IDriveUploads uploads)
{
    public Task<Result<DriveUploadState>> ExecuteAsync(string sessionId, CancellationToken cancellationToken) =>
        uploads.GetAsync(sessionId, cancellationToken);
}

/// <summary>Graph <c>DELETE {uploadUrl}</c>: drops the session and what it staged.</summary>
public sealed class CancelDriveUpload(IDriveUploads uploads)
{
    public Task<Result> ExecuteAsync(string sessionId, CancellationToken cancellationToken) =>
        uploads.CancelAsync(sessionId, cancellationToken);
}

/// <summary>Graph <c>POST …/createLink</c>: an anonymous <c>view</c> or <c>edit</c> link (ADR 0008 D6).</summary>
public sealed class CreateSharingLink(IDriveSharing sharing)
{
    public async Task<Result<DriveShare>> ExecuteAsync(
        DriveItemRef item, string? type, string? scope, DateTimeOffset? expiration, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var linkType = type?.ToLowerInvariant();
        if (linkType is not ("view" or "edit"))
        {
            return linkType is null
                ? Error.Validation("type is required ('view' or 'edit').", "type")
                : DriveErrors.NotSupported($"Link type '{type}' is not supported; use 'view' or 'edit'.", "type");
        }

        if (scope is not null && !string.Equals(scope, "anonymous", StringComparison.OrdinalIgnoreCase))
        {
            return DriveErrors.NotSupported($"Link scope '{scope}' is not supported; links are 'anonymous'.", "scope");
        }

        if (item.IsRoot)
        {
            return Error.Forbidden("The root folder can't be shared.");
        }

        return await sharing.CreateLinkAsync(item, new SharingLinkRequest(linkType, expiration, string.IsNullOrEmpty(password) ? null : password), cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Graph <c>POST …/invite</c>: shares an item with directory users.</summary>
public sealed class InviteToDriveItem(IDriveSharing sharing)
{
    public const int MaxRecipients = 50;

    public async Task<Result<IReadOnlyList<DriveShare>>> ExecuteAsync(DriveItemRef item, DriveInvitation invitation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(invitation);
        if (invitation.Recipients.Count is 0 or > MaxRecipients || invitation.Recipients.Any(string.IsNullOrWhiteSpace))
        {
            return Error.Validation($"recipients needs 1 to {MaxRecipients} email addresses.", "recipients");
        }

        if (invitation.Roles.Count == 0 || invitation.Roles.Any(static r => r is not ("read" or "write")))
        {
            return DriveErrors.NotSupported("roles must be 'read' or 'write'.", "roles");
        }

        if (item.IsRoot)
        {
            return Error.Forbidden("The root folder can't be shared.");
        }

        return await sharing.InviteAsync(item, invitation, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>GET …/permissions</c>.</summary>
public sealed class ListDriveItemPermissions(IDriveSharing sharing)
{
    public Task<Result<IReadOnlyList<DriveShare>>> ExecuteAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return sharing.ListPermissionsAsync(item, cancellationToken);
    }
}

/// <summary>Graph <c>DELETE …/permissions/{id}</c>.</summary>
public sealed class RemoveDriveItemShare(IDriveSharing sharing)
{
    public Task<Result> ExecuteAsync(DriveItemRef item, string permissionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionId);
        return sharing.DeletePermissionAsync(item, permissionId, cancellationToken);
    }
}
