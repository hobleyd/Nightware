using System.Globalization;
using System.Text;

using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Changes;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Chats;

/// <summary>
/// The chat area's state for the change stream (ADR 0007 D5): every Talk conversation the caller is in that Graph
/// shows as a chat or a channel (<see cref="TalkChatRooms.IsChat"/>), in token order, with its newest message id, the
/// caller's read marker and unread count, and its name, from the one room listing (<c>noStatusUpdate=1</c>, so a
/// watching client does not keep the caller "online"). A message posted, a conversation read elsewhere, renamed, joined
/// or left moves it. An edit, a deletion or a reaction does not: Talk posts those without moving the room's last
/// message, and finding them costs a read per conversation (ADR 0009 D11); the client's open conversation is
/// polled anyway.
/// </summary>
/// <remarks>
/// Chats are the caller's, never a shared mailbox's, so another mailbox has no chat area (Unsupported). A credential
/// Nextcloud refuses is reported as Forbidden, which the watcher drops, not as an auth failure that would end the
/// stream: a user Talk does not know must still get mail and calendar signals. A token that really expired fails
/// the mail and calendar reads too.
/// </remarks>
internal sealed class TalkChatChangeProbe(TalkChatClient talk, IUserContext? user) : IChangeStateProbe
{
    public ChangeArea Area => ChangeArea.Chat;

    public async Task<Result<string>> StateAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        // The stream's mailbox is resolved from the caller's own address for /me (MailboxResolver), so anything else
        // is someone else's mailbox.
        if (user is null || !string.Equals(mailbox.Address, user.Principal.Mail, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Unsupported("Chats are the signed-in user's; a shared mailbox has none.");
        }

        var rooms = await talk.ListRoomsAsync(cancellationToken).ConfigureAwait(false);
        if (!rooms.IsSuccess)
        {
            return rooms.Error.Kind is ErrorKind.BackendAuthFailed or ErrorKind.Unauthorized
                ? Error.Forbidden("Talk refused the caller; chats are not watched.")
                : rooms.Error;
        }

        return State(rooms.Value);
    }

    internal static string State(IEnumerable<TalkRoom> rooms)
    {
        var state = new StringBuilder();
        foreach (var room in rooms.Where(TalkChatRooms.IsChat).OrderBy(static r => r.Token, StringComparer.Ordinal))
        {
            state.Append(CultureInfo.InvariantCulture,
                $"{room.Token}={room.LastMessage?.Id},{room.LastReadMessage},{room.UnreadMessages},{room.Name.Length}:{room.Name},{room.DisplayName.Length}:{room.DisplayName};");
        }

        return state.ToString();
    }
}
