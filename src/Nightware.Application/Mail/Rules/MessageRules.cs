using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Rules;

/// <summary>Where a mailbox's Inbox rules are kept. The use cases own ordering and validation; the store keeps the list.</summary>
public interface IMessageRuleStore
{
    /// <summary>The rules in sequence order.</summary>
    Task<Result<IReadOnlyList<MessageRule>>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    /// <summary>Replaces the rules (already in sequence order, numbered 1…n) and puts them into effect.</summary>
    Task<Result> SaveAsync(MailboxRef mailbox, IReadOnlyList<MessageRule> rules, CancellationToken cancellationToken);
}

/// <summary>What the rule use cases share: ordering and the checks every saved rule passes.</summary>
public static class MessageRules
{
    /// <summary>Sieve allows one redirect per message on Stalwart (<c>maxNumberRedirects</c>).</summary>
    public const int MaxRedirects = 1;

    /// <summary>
    /// The rules ordered by sequence (ties keep their order) and renumbered 1…n. <paramref name="moved"/>, when
    /// given, goes before any rule with the same sequence: a client that sets sequence 1 means "first".
    /// </summary>
    public static IReadOnlyList<MessageRule> Ordered(IEnumerable<MessageRule> rules, MessageRule? moved = null) =>
        [.. rules
            .Select((rule, index) => (rule, index))
            .OrderBy(static r => r.rule.Sequence)
            .ThenBy(r => moved is not null && r.rule.Id == moved.Id ? 0 : 1)
            .ThenBy(static r => r.index)
            .Select(static (r, i) => r.rule with { Sequence = i + 1 })];

    public static Result Validate(MessageRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(rule.DisplayName))
        {
            return Error.Validation("A rule needs a displayName.", "displayName");
        }

        if (rule.Actions.IsEmpty)
        {
            return Error.Validation("A rule needs at least one action.", "actions");
        }

        if (rule.Actions.ForwardTo.Count + rule.Actions.RedirectTo.Count > MaxRedirects)
        {
            return Error.Validation("A rule can forward or redirect to one address only on this server.", "actions");
        }

        return Result.Success();
    }

    internal static Error NotFound(string id) => Error.NotFound($"The rule '{id}' does not exist.");
}

/// <summary>Graph <c>GET mailFolders/inbox/messageRules</c>.</summary>
public sealed class ListMessageRules(IMessageRuleStore store)
{
    public Task<Result<IReadOnlyList<MessageRule>>> ExecuteAsync(MailboxRef mailbox, CancellationToken cancellationToken) =>
        store.ListAsync(mailbox, cancellationToken);
}

/// <summary>Graph <c>GET mailFolders/inbox/messageRules/{id}</c>.</summary>
public sealed class GetMessageRule(IMessageRuleStore store)
{
    public async Task<Result<MessageRule>> ExecuteAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken)
    {
        var rules = await store.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return rules.Bind(list => list.FirstOrDefault(r => r.Id == id) is { } rule ? Result<MessageRule>.Success(rule) : MessageRules.NotFound(id));
    }
}

/// <summary>Graph <c>POST mailFolders/inbox/messageRules</c>: the rule takes its place by sequence.</summary>
public sealed class CreateMessageRule(IMessageRuleStore store)
{
    public async Task<Result<MessageRule>> ExecuteAsync(MailboxRef mailbox, MessageRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var created = rule with { Id = Guid.NewGuid().ToString("N"), DisplayName = rule.DisplayName.Trim() };
        var valid = MessageRules.Validate(created);
        if (!valid.IsSuccess)
        {
            return valid.Error;
        }

        var rules = await store.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!rules.IsSuccess)
        {
            return rules.Error;
        }

        var ordered = MessageRules.Ordered([.. rules.Value, created], created);
        var saved = await store.SaveAsync(mailbox, ordered, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? ordered.Single(r => r.Id == created.Id) : saved.Error;
    }
}

/// <summary>Graph <c>PATCH mailFolders/inbox/messageRules/{id}</c>: <paramref name="change"/> maps the stored rule to the updated one.</summary>
public sealed class UpdateMessageRule(IMessageRuleStore store)
{
    public async Task<Result<MessageRule>> ExecuteAsync(MailboxRef mailbox, string id, Func<MessageRule, Result<MessageRule>> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        var rules = await store.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!rules.IsSuccess)
        {
            return rules.Error;
        }

        var existing = rules.Value.FirstOrDefault(r => r.Id == id);
        if (existing is null)
        {
            return MessageRules.NotFound(id);
        }

        var updated = change(existing).Map(r => r with { Id = existing.Id, DisplayName = r.DisplayName.Trim() });
        if (!updated.IsSuccess)
        {
            return updated.Error;
        }

        var valid = MessageRules.Validate(updated.Value);
        if (!valid.IsSuccess)
        {
            return valid.Error;
        }

        var moved = updated.Value.Sequence != existing.Sequence ? updated.Value : null;
        var ordered = MessageRules.Ordered(rules.Value.Select(r => r.Id == id ? updated.Value : r), moved);
        var saved = await store.SaveAsync(mailbox, ordered, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? ordered.Single(r => r.Id == id) : saved.Error;
    }
}

/// <summary>Graph <c>DELETE mailFolders/inbox/messageRules/{id}</c>.</summary>
public sealed class DeleteMessageRule(IMessageRuleStore store)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, string id, CancellationToken cancellationToken)
    {
        var rules = await store.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!rules.IsSuccess)
        {
            return rules.Error;
        }

        return rules.Value.Any(r => r.Id == id)
            ? await store.SaveAsync(mailbox, MessageRules.Ordered(rules.Value.Where(r => r.Id != id)), cancellationToken).ConfigureAwait(false)
            : MessageRules.NotFound(id);
    }
}
