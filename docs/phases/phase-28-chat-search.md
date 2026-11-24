# Phase 28 — Chat message search

> **Status (2026-11-24): done, local only.** Asked for: "add a search endpoint
> for chats compatible with Graph". Graph's way to search chat messages is
> Microsoft Search, `POST /search/query` with `entityTypes: ["chatMessage"]`
> (`/chats/{id}/messages` takes no `$search`). Decisions are in ADR 0009 D10.
> The fixture is constructed from Graph's chat message search concept page; a
> tenant capture (`tools/capture --phase 28`) replaces it.

## Step 0 — what Talk offers
- Talk 25 registers the unified search provider `talk-message`
  (`OCA\Talk\Search\MessageSearch`). `GET /ocs/v2.php/search/providers/talk-message/search`
  takes `term`, `limit` and `cursor` (an offset) and searches every room of
  the caller's (not federated ones; lobby rooms only for those who may skip
  the lobby), comments and shared objects, newest first.
- Nextcloud caps `limit` at `unified-search.max-results-per-request` (25).
- An entry's `attributes` carry `conversation` (the token), `messageId`,
  `actorType`, `actorId` and `timestamp`; `title` is "{sender} in
  {conversation}" (just the sender in a one-to-one), `subline` the text around
  the match. Expired and invisible messages are dropped after the page is read.

## Work
| Piece | Where |
|---|---|
| `IChatSearch`, `ChatMessageHit`, `ChatSender`, `ChatSearchHits` | `Application/Chats/ChatPorts.cs` |
| `SearchChatMessages` (text required, at most 256 characters; `size` read as at most 25) | `Application/Chats/ChatMessageUseCases.cs` |
| `TalkChatClient.SearchMessagesAsync`, `TalkSearchEntry`, `TalkSearchPage`; `TalkChatSearch` keeps hits in the caller's chats (channels found as `/me/chats` finds them) | `Adapters.Dav/Chats` |
| `/search/query` moves to `Features/Search/SearchQueryEndpoint` (one request, one type), answered by `DriveSearchQueryEndpoint` (`driveItem`) or `ChatSearchQuery` (`chatMessage`) | `Api/Features` |
| Contract `search.query.chat-messages`, fixture, arrangement, capture scenario | `docs/contract`, `tests/Nightware.Conformance.Tests`, `tools/capture` |

## Done
| Gap | Version |
|---|---|
| `POST /search/query` for `chatMessage` over Talk's message search — verified live | 1.82.0 |
