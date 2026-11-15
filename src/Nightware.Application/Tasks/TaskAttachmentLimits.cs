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

    internal static Domain.Kernel.Error? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? Domain.Kernel.Error.Validation("An attachment needs a name.", "name")
            : name.Length > MaxNameLength
                ? Domain.Kernel.Error.Validation($"An attachment name may have at most {MaxNameLength} characters.", "name")
                : null;
}
