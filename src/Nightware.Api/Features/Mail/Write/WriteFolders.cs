using Nightware.Api.Features.Mail.Read;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Features.Mail.Write;

/// <summary>A folder named by a write: a route segment or a body's <c>destinationId</c>, which may be missing.</summary>
internal static class WriteFolders
{
    public static Result<FolderRef> Parse(string? value, string target) =>
        string.IsNullOrWhiteSpace(value)
            ? Error.Validation($"{target} is required.", target)
            : FolderReference.Parse(value.Trim());
}
