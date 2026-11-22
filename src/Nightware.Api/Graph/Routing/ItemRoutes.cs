namespace Nightware.Api.Graph.Routing;

/// <summary>
/// Graph's other routes to one item (phase 18 and 19 aliases): an item id names its resource, unique in the mailbox,
/// so the parent segments are not checked and each route maps the base operation's own handler, which can't drift.
/// Literal segments (<c>…/delta</c>, <c>…/$count</c>) keep precedence over the item id beside them.
/// </summary>
internal static class ItemRoutes
{
    /// <summary>An event: <c>events/{id}</c>, <c>calendar/events/{id}</c> and <c>calendars/{id}/events/{id}</c>.</summary>
    public static readonly string[] Event = ["/events/{eventId}", "/calendar/events/{eventId}", "/calendars/{calendarId}/events/{eventId}"];

    /// <summary>A message: <c>messages/{id}</c> and <c>mailFolders/{id}/messages/{id}</c>.</summary>
    public static readonly string[] Message = ["/messages/{messageId}", "/mailFolders/{folderId}/messages/{messageId}"];

    /// <summary>A contact: <c>contacts/{id}</c> and <c>contactFolders/{id}/contacts/{id}</c>.</summary>
    public static readonly string[] Contact = ["/contacts/{contactId}", "/contactFolders/{folderId}/contacts/{contactId}"];
}
