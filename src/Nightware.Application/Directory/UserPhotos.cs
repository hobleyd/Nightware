using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>Rules shared by the photo use cases and the stores behind them.</summary>
public static class UserPhotos
{
    /// <summary>Graph's code for a user without a photo (404 on <c>…/photo/$value</c>).</summary>
    public const string NoPhotoCode = "ImageNotFound";

    /// <summary>Graph's limit on a photo upload (profilephoto-update).</summary>
    public const int MaxUploadBytes = 4 * 1024 * 1024;

    /// <summary>The user exists but has no photo; distinct from an unknown user, whose metadata is a 404 too.</summary>
    public static Error None() => new(ErrorKind.NotFound, NoPhotoCode, "The user has no photo.");

    public static bool IsNone(Error error) => error is { Kind: ErrorKind.NotFound, Code: NoPhotoCode };
}
