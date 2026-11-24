using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.MailboxSettings;

/// <summary>
/// The senders an automatic reply set to <c>contactsOnly</c> answers outside the organisation (Graph: "only my
/// contacts"): the mailbox's contacts' addresses, read when the reply is saved and again when the façade changes a
/// contact. The mail filter can't read contacts, so the list is written into it and is as fresh as its last write.
/// </summary>
public sealed class AutoReplyKnownSenders(IContactStore contacts)
{
    /// <summary>At most this many addresses go into the filter; contacts past it aren't answered.</summary>
    public const int MaxAddresses = 1000;

    private const int PageSize = 200;
    private const int MaxPages = 50;

    /// <summary>The contacts' addresses outside <paramref name="internalDomains"/>, lower-case, each once, in listing order.</summary>
    public async Task<Result<IReadOnlyList<string>>> ForAsync(MailboxRef mailbox, IReadOnlyList<string> internalDomains, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(internalDomains);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;
        for (var page = 0; page < MaxPages && found.Count < MaxAddresses; page++)
        {
            var listed = await contacts.ListAsync(mailbox, new ItemQuery { Top = PageSize, SkipToken = token }, cancellationToken).ConfigureAwait(false);
            if (!listed.IsSuccess)
            {
                return listed.Error;
            }

            foreach (var address in listed.Value.Items.SelectMany(static c => c.EmailAddresses).Select(static a => Normalise(a.Address)).OfType<string>())
            {
                var domain = address[(address.LastIndexOf('@') + 1)..];
                if (found.Count < MaxAddresses && !internalDomains.Contains(domain, StringComparer.OrdinalIgnoreCase) && seen.Add(address))
                {
                    found.Add(address);
                }
            }

            token = listed.Value.SkipToken;
            if (token is null)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// A contact's address as it may go into the filter: one <c>@</c>, nothing either side empty, no white space,
    /// control, quote, backslash or list punctuation; lower-case. Anything else is left out (a contact's address is
    /// whatever its owner typed, and it ends up in a script).
    /// </summary>
    internal static string? Normalise(string? address)
    {
        var trimmed = address?.Trim();
        if (trimmed is null || trimmed.Length is < 3 or > 254)
        {
            return null;
        }

        var at = trimmed.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0 || at == trimmed.Length - 1 || trimmed.IndexOf('@', at + 1) >= 0)
        {
            return null;
        }

        return trimmed.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\\' or ',' or ';' or '<' or '>' or '(' or ')' or '[' or ']')
            ? null
            : trimmed.ToLowerInvariant();
    }
}

/// <summary>
/// After the façade changes a contact: a <c>contactsOnly</c> automatic reply of that mailbox gets the contacts as they
/// now are. Best effort: the contact change has happened whatever this does.
/// </summary>
public sealed class RefreshAutoReplyKnownSenders(IAutoReplyStore replies, AutoReplyKnownSenders senders, IInternalDomains domains)
{
    public async Task ExecuteAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var current = await replies.GetAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (current is not { IsSuccess: true, Value: { Status: not AutoReplyStatus.Disabled, ExternalAudience: ExternalAudience.ContactsOnly } setting })
        {
            return;
        }

        var internalDomains = await domains.ForAsync(mailbox, cancellationToken).ConfigureAwait(false);
        var known = await senders.ForAsync(mailbox, internalDomains, cancellationToken).ConfigureAwait(false);
        if (known.IsSuccess)
        {
            await replies.SetAsync(mailbox, setting, internalDomains, known.Value, cancellationToken).ConfigureAwait(false);
        }
    }
}
