using System.Xml.Linq;

namespace Nightware.Adapters.Dav.Core;

/// <summary>XML namespaces spoken by Sabre/Nextcloud.</summary>
public static class DavNs
{
    public static readonly XNamespace Dav = "DAV:";
    public static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    public static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";
    public static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";
    public static readonly XNamespace Nextcloud = "http://nextcloud.com/ns";
    public static readonly XNamespace OwnCloud = "http://owncloud.org/ns";
    public static readonly XNamespace Sabre = "http://sabredav.org/ns";
    public static readonly XNamespace AppleICal = "http://apple.com/ns/ical/";
}

/// <summary>Property and element names used across the adapter.</summary>
public static class DavNames
{
    // DAV: (RFC 4918, 3744, 5397, 6578)
    public static readonly XName Multistatus = DavNs.Dav + "multistatus";
    public static readonly XName Response = DavNs.Dav + "response";
    public static readonly XName Href = DavNs.Dav + "href";
    public static readonly XName Status = DavNs.Dav + "status";
    public static readonly XName PropStat = DavNs.Dav + "propstat";
    public static readonly XName Prop = DavNs.Dav + "prop";
    public static readonly XName Error = DavNs.Dav + "error";
    public static readonly XName ResponseDescription = DavNs.Dav + "responsedescription";
    public static readonly XName SyncToken = DavNs.Dav + "sync-token";
    public static readonly XName ResourceType = DavNs.Dav + "resourcetype";
    public static readonly XName Collection = DavNs.Dav + "collection";
    public static readonly XName Principal = DavNs.Dav + "principal";
    public static readonly XName DisplayName = DavNs.Dav + "displayname";
    public static readonly XName GetETag = DavNs.Dav + "getetag";
    public static readonly XName GetContentType = DavNs.Dav + "getcontenttype";
    public static readonly XName GetContentLength = DavNs.Dav + "getcontentlength";
    public static readonly XName GetLastModified = DavNs.Dav + "getlastmodified";
    public static readonly XName CurrentUserPrincipal = DavNs.Dav + "current-user-principal";
    public static readonly XName CurrentUserPrivilegeSet = DavNs.Dav + "current-user-privilege-set";
    public static readonly XName Privilege = DavNs.Dav + "privilege";
    public static readonly XName Write = DavNs.Dav + "write";
    public static readonly XName WriteContent = DavNs.Dav + "write-content";
    public static readonly XName All = DavNs.Dav + "all";
    public static readonly XName Owner = DavNs.Dav + "owner";
    public static readonly XName PrincipalCollectionSet = DavNs.Dav + "principal-collection-set";
    public static readonly XName ValidSyncToken = DavNs.Dav + "valid-sync-token";

    // CalDAV (RFC 4791, 6638)
    public static readonly XName Calendar = DavNs.CalDav + "calendar";
    public static readonly XName CalendarData = DavNs.CalDav + "calendar-data";
    public static readonly XName CalendarHomeSet = DavNs.CalDav + "calendar-home-set";
    public static readonly XName CalendarUserAddressSet = DavNs.CalDav + "calendar-user-address-set";
    public static readonly XName CalendarUserType = DavNs.CalDav + "calendar-user-type";
    public static readonly XName CalendarDescription = DavNs.CalDav + "calendar-description";
    public static readonly XName CalendarTimezone = DavNs.CalDav + "calendar-timezone";
    public static readonly XName SupportedCalendarComponentSet = DavNs.CalDav + "supported-calendar-component-set";
    public static readonly XName Comp = DavNs.CalDav + "comp";
    public static readonly XName ScheduleOutboxUrl = DavNs.CalDav + "schedule-outbox-URL";
    public static readonly XName ScheduleInboxUrl = DavNs.CalDav + "schedule-inbox-URL";
    public static readonly XName ScheduleInbox = DavNs.CalDav + "schedule-inbox";
    public static readonly XName ScheduleOutbox = DavNs.CalDav + "schedule-outbox";
    public static readonly XName ScheduleResponse = DavNs.CalDav + "schedule-response";
    public static readonly XName ScheduleResponseItem = DavNs.CalDav + "response";
    public static readonly XName Recipient = DavNs.CalDav + "recipient";
    public static readonly XName RequestStatus = DavNs.CalDav + "request-status";
    public static readonly XName NoUidConflict = DavNs.CalDav + "no-uid-conflict";
    public static readonly XName MaxResourceSize = DavNs.CalDav + "max-resource-size";

    // CardDAV (RFC 6352)
    public static readonly XName AddressBook = DavNs.CardDav + "addressbook";
    public static readonly XName AddressData = DavNs.CardDav + "address-data";
    public static readonly XName AddressBookHomeSet = DavNs.CardDav + "addressbook-home-set";
    public static readonly XName AddressBookDescription = DavNs.CardDav + "addressbook-description";
    public static readonly XName CardDavNoUidConflict = DavNs.CardDav + "no-uid-conflict";
    public static readonly XName CardDavMaxResourceSize = DavNs.CardDav + "max-resource-size";

    // Calendar server / Apple / ownCloud / Nextcloud extensions
    public static readonly XName GetCTag = DavNs.CalendarServer + "getctag";
    public static readonly XName CalendarServerSharedUrl = DavNs.CalendarServer + "shared-url";
    public static readonly XName CalendarColor = DavNs.AppleICal + "calendar-color";
    public static readonly XName CalendarOrder = DavNs.AppleICal + "calendar-order";
    public static readonly XName OwnerPrincipal = DavNs.OwnCloud + "owner-principal";
    public static readonly XName OwnCloudInvite = DavNs.OwnCloud + "invite";
    public static readonly XName OwnCloudReadOnly = DavNs.OwnCloud + "read-only";
    public static readonly XName OwnerDisplayName = DavNs.Nextcloud + "owner-displayname";
    public static readonly XName Deleted = DavNs.Nextcloud + "deleted-at";
    public static readonly XName EmailAddress = DavNs.Sabre + "email-address";
}
