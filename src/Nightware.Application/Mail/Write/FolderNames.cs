using Nightware.Domain.Kernel;

namespace Nightware.Application.Mail.Write;

internal static class FolderNames
{
    public static bool IsValid(string? displayName) => !string.IsNullOrWhiteSpace(displayName);

    public static Error Invalid() => Error.Validation("A folder needs a displayName.", "displayName");
}
