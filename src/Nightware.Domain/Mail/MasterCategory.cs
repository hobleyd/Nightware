namespace Nightware.Domain.Mail;

/// <summary>Graph <c>outlookCategory</c>: a named colour in the mailbox's category list.</summary>
/// <param name="Color"><c>none</c> or <c>preset0</c>…<c>preset24</c>.</param>
public sealed record MasterCategory(string Id, string DisplayName, string Color);
