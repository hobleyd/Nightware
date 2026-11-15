using Nightware.Domain.Kernel;

namespace Nightware.Application.Tasks;

/// <summary>Microsoft To Do's attachment limits, which NightMail sizes its requests by.</summary>
public static class TaskAttachmentLimits
{
    /// <summary>Largest attachment sent inline as <c>contentBytes</c>; larger ones use an upload session.</summary>
    public const long MaxInlineBytes = 3 * 1024 * 1024;

    /// <summary>Largest attachment To Do accepts at all.</summary>
    public const long MaxBytes = 25 * 1024 * 1024;

    /// <summary>Longest file name accepted (a common file-system limit).</summary>
    public const int MaxNameLength = 255;

    /// <summary>Why <paramref name="name"/> cannot name an attachment, or null when it can.</summary>
    public static Error? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? Error.Validation("An attachment needs a name.", "name")
            : name.Length > MaxNameLength
                ? Error.Validation($"An attachment name may have at most {MaxNameLength} characters.", "name")
                : null;

    /// <summary>Why a file of <paramref name="name"/> and <paramref name="size"/> bytes cannot be uploaded, or null when it can.</summary>
    public static Error? Validate(string? name, long size) =>
        ValidateName(name)
        ?? (size <= 0
            ? Error.Validation("The attachment size must be positive.", "size")
            : size > MaxBytes
                ? new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", $"Task attachments may be at most {MaxBytes} bytes.")
                : null);
}
