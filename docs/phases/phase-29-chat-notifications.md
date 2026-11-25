# Phase 29 — Change notifications for chats and channels

> **Status (2026-11-25): done, local only.** Asked for: "add chat notifications
> to line up with the Graph APIs". Read as Graph's change notifications for
> Teams resources (`POST /subscriptions`), on the phase 16 store and poller.
> Decisions: ADR 0009 D11 (and the lifetime amendment to ADR 0006 D10). The
> four fixtures are constructed from the captured events subscription;
> `tools/capture --phase 29` captures them from a tenant with Teams.

## Step 0
- Graph's pages (teams-changenotifications-chatmessage, -chat,
  -chatmembership; subscription resource): delegated resources are
  `chats/{id}/messages`, `users/{id}/chats/getAllMessages`,
  `teams/{id}/channels/{id}/messages`, `users/{id}/chats` (and `me/chats`),
  `chats/{id}` and `chats/{id}/members`; `chats`, `chats/getAllMessages`,
  `chats/getAllMembers` and `teams/getAllMessages` are application-only.
  Teams subscriptions last up to 4,320 minutes and need a
  `lifecycleNotificationUrl` past one hour. Basic notifications name
  `chats('…')/messages('…')`, `teams('…')/channels('…')/messages('…')`,
  `chats('…')` and `chats('…')/members('…')`.
- Talk: `GET chat/{token}?lookIntoFuture=1&timeout=0&lastKnownMessageId=…`
  reads what came after an id without waiting or moving read markers
  (`setReadMarker=0`, `markNotificationsAsRead=0`). Edits, deletes and
  reactions are system messages pointing at their parent, posted with
  `shouldSkipLastMessageUpdate`: the room's `lastMessage` and `lastActivity`
  don't move, so the room list can't say which chats changed.

## Work
| Piece | Where |
|---|---|
| `SubscriptionResourceKind` Teams kinds, `IsTeams` | `Domain/Subscriptions` |
| `ITeamsChangeFeed`; `ResourceChange.Resource`/`ODataType`; `ChangeBatch.Classified` (the notifier keeps the feed's types and tracks no items) | `Application/Subscriptions` |
| Teams resources parsed (caller only, no query options), 4,320-minute cap and the lifecycle URL rule on create and renewal | `SubscriptionsUseCase` |
| `TalkChangeFeed` (rooms' chats after a cursor; chats and members against a snapshot) | `Adapters.Dav/Chats` |
| `ChatKeySegmentMiddleware`: `chats('x')/messages('y')` and the channel forms routed | `Api/Features/Chats` |
| Contract `subscriptions.create-chat-messages`, `-channel-messages`, `-chats`, `-chat-members`; fixtures, arrangements, capture scenario | `docs/contract`, `tests/Nightware.Conformance.Tests`, `tools/capture` |

## Done
| Gap | Version |
|---|---|
| Change notifications for chat messages (one chat and `getAllMessages`), channel messages, chats and chat members; key-form chat paths routed — verified live (created, updated and deleted chat messages, the notified resource read back) | 1.84.0 |
