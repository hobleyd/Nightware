namespace Nightware.Domain.Directory;

/// <summary>
/// A group in the directory (Graph <c>group</c>): one of an entity's groups (ADR 0003). Its key is the
/// <paramref name="MailNickname"/>; a group with a list has a <see cref="Mail"/> address, and a group with a files facet
/// grants access to documents, which is what Graph calls security-enabled.
/// </summary>
public sealed record DirectoryGroup(string Id, string DisplayName, string MailNickname)
{
    /// <summary>The list's address; null for a group without one.</summary>
    public string? Mail { get; init; }

    /// <summary>The list's other addresses (bare, without <see cref="Mail"/>).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Whether the group grants access to documents (its files facet).</summary>
    public bool SecurityEnabled { get; init; }

    public bool MailEnabled => Mail is not null;
}

/// <summary>
/// The organization (Graph <c>organization</c>): an entity the signed-in user belongs to. <paramref name="Domains"/> are
/// its mail domains, the default one first.
/// </summary>
public sealed record DirectoryOrganization(string Id, string DisplayName, IReadOnlyList<string> Domains);
