using Nightware.Adapters.Jmap.Core.Protocol;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>How a message-collection request runs against JMAP. Built by <see cref="MessageQueryTranslator"/>.</summary>
internal abstract record MessageQueryPlan
{
    private MessageQueryPlan()
    {
    }

    /// <summary>
    /// <c>$filter=conversationId eq|in …</c>. JMAP <c>Email/query</c> has no thread filter, so this is
    /// <c>Thread/get</c> then <c>Email/get</c> with explicit ids; <paramref name="ExcludedFolderIds"/>
    /// (<c>parentFolderId ne</c>) are applied to <c>mailboxIds</c> afterwards.
    /// </summary>
    public sealed record Conversation(IReadOnlyList<string> ThreadIds, IReadOnlyList<string> ExcludedFolderIds) : MessageQueryPlan;

    /// <summary><c>Email/query</c> with a filter and sort; <paramref name="Paged"/> lists emit a skip token.</summary>
    public sealed record Query(EmailFilter? Filter, IReadOnlyList<Comparator> Sort, bool Paged) : MessageQueryPlan;
}
