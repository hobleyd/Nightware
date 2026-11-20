using System.Text;
using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Application.Mail.Categories;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Categories;

/// <summary>
/// The master category list in the Inbox's shared JMAP metadata, <c>nightware.ms/masterCategories</c>
/// (ADR 0006 D5): an account-level home Stalwart offers no other way. The namespace is written whole, keeping
/// whatever else it holds, and Stalwart caps it at 8192 bytes (about 80 categories).
/// </summary>
internal sealed class JmapMasterCategoryStore(IJmapClient jmap) : IMasterCategoryStore
{
    private const string Key = "masterCategories";

    public async Task<Result<IReadOnlyList<MasterCategory>?>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var inbox = await InboxAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!inbox.IsSuccess)
        {
            return inbox.Error;
        }

        return inbox.Value.Namespace?[Key] is JsonArray list
            ? Result<IReadOnlyList<MasterCategory>?>.Success([.. list.OfType<JsonObject>().Select(Read).OfType<MasterCategory>()])
            : Result<IReadOnlyList<MasterCategory>?>.Success(null);
    }

    public async Task<Result> SaveAsync(MailboxRef mailbox, IReadOnlyList<MasterCategory> categories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var inbox = await InboxAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!inbox.IsSuccess)
        {
            return inbox.Error;
        }

        var (account, mailboxId, existing) = inbox.Value;
        if (account.IsReadOnly)
        {
            return Error.Forbidden($"The signed-in user cannot change the categories of '{mailbox.Address}'.");
        }

        var ns = existing?.DeepClone().AsObject() ?? [];
        ns[Key] = new JsonArray([.. categories.Select(static c => (JsonNode)new JsonObject { ["id"] = c.Id, ["displayName"] = c.DisplayName, ["color"] = c.Color })]);
        if (Encoding.UTF8.GetByteCount(ns.ToJsonString()) > MessageMetadata.MaxBytes)
        {
            return Error.Validation("The mailbox has too many categories to store; delete some first.", "displayName");
        }

        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.MailboxSetWithMetadata, new MailboxSetRequest(account.AccountId)
        {
            Update = new Dictionary<string, PatchObject>(StringComparer.Ordinal)
            {
                [mailboxId] = new Patch().Set($"metadata/{MessageMetadata.Namespace}", ns).Build(),
            },
        });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Then(s => s.Updated(mailboxId));
    }

    private async Task<Result<(JmapAccount Account, string MailboxId, JsonObject? Namespace)>> InboxAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        if (!account.Value.SupportsMetadata)
        {
            return Error.Unsupported("This mail server cannot keep a category list.");
        }

        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.MailboxGetWithMetadata, new GetRequest(account.Value.AccountId) { Properties = ["id", "role", "metadata"] });
        var mailboxes = (await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false)).Bind(r => r.Get(get));
        if (!mailboxes.IsSuccess)
        {
            return mailboxes.Error;
        }

        return MailboxRoles.Find(mailboxes.Value.List, WellKnownFolder.Inbox) is { Id: { } id } inbox
            ? (account.Value, id, inbox.Metadata?[MessageMetadata.Namespace] as JsonObject)
            : JmapErrors.Unexpected("The mailbox has no Inbox to keep its categories in.");
    }

    private static MasterCategory? Read(JsonObject json) =>
        json["id"] is JsonValue id && id.TryGetValue<string>(out var idText)
        && json["displayName"] is JsonValue name && name.TryGetValue<string>(out var nameText)
            ? new MasterCategory(idText, nameText, json["color"] is JsonValue c && c.TryGetValue<string>(out var color) ? color : MasterCategories.NoColor)
            : null;
}
