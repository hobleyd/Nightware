using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Contacts;

/// <summary>Changes to the contacts of one folder since a token (the adapter's own).</summary>
public interface IContactDelta
{
    /// <param name="token">Null for the initial sync: every contact in the folder, and a token for what follows.</param>
    Task<Result<DeltaPage<Contact>>> DeltaAsync(MailboxRef mailbox, ContactFolderId folder, DeltaToken? token, CancellationToken cancellationToken);
}

/// <summary>Graph <c>GET contactFolders/{id}/contacts/delta</c>.</summary>
public sealed class SyncFolderContacts(IContactFolderStore folders, IContactDelta delta)
{
    public async Task<Result<DeltaPage<Contact>>> ExecuteAsync(MailboxRef mailbox, string folderId, DeltaToken? token, CancellationToken cancellationToken)
    {
        var folder = await folders.GetAsync(mailbox, folderId, cancellationToken).ConfigureAwait(false);
        return folder.IsSuccess ? await delta.DeltaAsync(mailbox, folder.Value.Id, token, cancellationToken).ConfigureAwait(false) : folder.Error;
    }
}

/// <summary>
/// Graph <c>GET contactFolders/delta</c>. An address-book home has no sync token of its own, and a user has a
/// handful of folders, so the token carries a snapshot (each folder's id and a hash of its name): the next
/// call reports folders that are new or renamed, and <c>@removed</c> for those gone.
/// </summary>
public sealed class SyncContactFolders(IContactFolderStore folders)
{
    private const char FieldSeparator = '\u001F';
    private const char EntrySeparator = '\u001E';
    private const string Version = "f1";

    public async Task<Result<DeltaPage<ContactFolder>>> ExecuteAsync(MailboxRef mailbox, DeltaToken? token, CancellationToken cancellationToken)
    {
        Dictionary<string, string>? previous = null;
        if (token is { } t && (previous = Decode(t)) is null)
        {
            return Error.SyncStateGone("The delta token is invalid or has expired; start again without one.");
        }

        var current = await folders.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return current.Error;
        }

        var entries = new List<DeltaEntry<ContactFolder>>();
        foreach (var folder in current.Value)
        {
            if (previous is null || !previous.TryGetValue(folder.Id.Value, out var hash) || hash != Hash(folder.DisplayName))
            {
                entries.Add(new DeltaEntry<ContactFolder>.Upsert(folder));
            }
        }

        if (previous is not null)
        {
            var now = current.Value.Select(f => f.Id.Value).ToHashSet(StringComparer.Ordinal);
            entries.AddRange(previous.Keys.Where(id => !now.Contains(id)).Select(id => new DeltaEntry<ContactFolder>.Removed(id, RemovedReason.Deleted)));
        }

        return DeltaPage<ContactFolder>.Done(entries, Encode(current.Value));
    }

    private static string Hash(string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)), 0, 8);

    internal static DeltaToken Encode(IEnumerable<ContactFolder> folders) =>
        new(Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            Version + EntrySeparator + string.Join(EntrySeparator, folders.Select(f => f.Id.Value + FieldSeparator + Hash(f.DisplayName))))));

    internal static Dictionary<string, string>? Decode(DeltaToken token)
    {
        try
        {
            var entries = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token.Value)).Split(EntrySeparator);
            if (entries[0] != Version)
            {
                return null;
            }

            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries.Skip(1).Where(e => e.Length > 0))
            {
                var fields = entry.Split(FieldSeparator);
                if (fields.Length != 2)
                {
                    return null;
                }

                snapshot[fields[0]] = fields[1];
            }

            return snapshot;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
