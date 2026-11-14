using System.Globalization;
using System.Xml.Linq;

namespace Nightware.Adapters.Dav.Core;

public enum DavDepth
{
    Zero,
    One,
}

/// <summary>A CalDAV time range (RFC 4791 §9.9), sent in UTC.</summary>
public sealed record DavTimeRange(DateTimeOffset? Start, DateTimeOffset? End)
{
    internal IEnumerable<XAttribute> ToAttributes()
    {
        if (Start is { } start)
        {
            yield return new XAttribute("start", DavRequestBodies.FormatUtc(start));
        }

        if (End is { } end)
        {
            yield return new XAttribute("end", DavRequestBodies.FormatUtc(end));
        }
    }
}

/// <summary>Parameters of a <c>calendar-query</c> REPORT.</summary>
/// <param name="Component"><c>VEVENT</c> or <c>VTODO</c>.</param>
public sealed record CalendarQuery(string Component)
{
    /// <summary>Only components overlapping this range.</summary>
    public DavTimeRange? TimeRange { get; init; }

    /// <summary>Ask Sabre to expand recurrences within this range (<c>C:expand</c>); occurrences come back in UTC.</summary>
    public DavTimeRange? Expand { get; init; }

    /// <summary>Include <c>calendar-data</c> (otherwise only ETags/hrefs).</summary>
    public bool IncludeData { get; init; } = true;

    /// <summary>Exact UID match (<c>i;octet</c>).</summary>
    public string? Uid { get; init; }

    public IReadOnlyList<XName> ExtraProperties { get; init; } = [];
}

/// <summary>Builds the XML bodies of the DAV methods and reports the adapter uses.</summary>
public static class DavRequestBodies
{
    public static string FormatUtc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static XDocument PropFind(IEnumerable<XName> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return Document(new XElement(DavNs.Dav + "propfind", Prop(properties)));
    }

    public static XDocument CalendarQuery(CalendarQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var props = new List<object> { new XElement(DavNames.GetETag) };
        if (query.IncludeData)
        {
            props.Add(new XElement(
                DavNames.CalendarData,
                query.Expand is null ? null : new XElement(DavNs.CalDav + "expand", query.Expand.ToAttributes())));
        }

        props.AddRange(query.ExtraProperties.Select(p => new XElement(p)));

        var componentFilter = new XElement(
            DavNs.CalDav + "comp-filter",
            new XAttribute("name", query.Component),
            query.TimeRange is null ? null : new XElement(DavNs.CalDav + "time-range", query.TimeRange.ToAttributes()),
            query.Uid is null
                ? null
                : new XElement(
                    DavNs.CalDav + "prop-filter",
                    new XAttribute("name", "UID"),
                    new XElement(DavNs.CalDav + "text-match", new XAttribute("collation", "i;octet"), query.Uid)));

        return Document(new XElement(
            DavNs.CalDav + "calendar-query",
            new XElement(DavNames.Prop, props),
            new XElement(
                DavNs.CalDav + "filter",
                new XElement(DavNs.CalDav + "comp-filter", new XAttribute("name", "VCALENDAR"), componentFilter))));
    }

    public static XDocument CalendarMultiget(IEnumerable<string> hrefs, bool includeData = true) =>
        Multiget(DavNs.CalDav + "calendar-multiget", DavNames.CalendarData, hrefs, includeData);

    public static XDocument AddressBookMultiget(IEnumerable<string> hrefs, bool includeData = true) =>
        Multiget(DavNs.CardDav + "addressbook-multiget", DavNames.AddressData, hrefs, includeData);

    /// <summary>
    /// An <c>addressbook-query</c>. With <paramref name="containsText"/>, matches cards whose
    /// FN or EMAIL contains it (case-insensitive); otherwise every card.
    /// </summary>
    public static XDocument AddressBookQuery(string? containsText = null, bool includeData = true, int? limit = null)
    {
        var filter = new XElement(DavNs.CardDav + "filter");
        if (!string.IsNullOrEmpty(containsText))
        {
            filter.Add(new XAttribute("test", "anyof"));
            foreach (var property in new[] { "FN", "EMAIL" })
            {
                filter.Add(new XElement(
                    DavNs.CardDav + "prop-filter",
                    new XAttribute("name", property),
                    new XElement(
                        DavNs.CardDav + "text-match",
                        new XAttribute("collation", "i;unicode-casemap"),
                        new XAttribute("match-type", "contains"),
                        containsText)));
            }
        }

        return Document(new XElement(
            DavNs.CardDav + "addressbook-query",
            new XElement(DavNames.Prop, new XElement(DavNames.GetETag), includeData ? new XElement(DavNames.AddressData) : null),
            filter,
            limit is null ? null : new XElement(DavNs.CardDav + "limit", new XElement(DavNs.CardDav + "nresults", limit.Value))));
    }

    /// <summary>RFC 6578 <c>sync-collection</c>; a null token requests the initial full listing.</summary>
    public static XDocument SyncCollection(string? syncToken, IEnumerable<XName> properties, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return Document(new XElement(
            DavNs.Dav + "sync-collection",
            new XElement(DavNames.SyncToken, syncToken ?? string.Empty),
            new XElement(DavNs.Dav + "sync-level", "1"),
            limit is null ? null : new XElement(DavNs.Dav + "limit", new XElement(DavNs.Dav + "nresults", limit.Value)),
            Prop(properties)));
    }

    /// <summary>RFC 4791 §7.10 <c>free-busy-query</c> (answered with <c>text/calendar</c>, not a multistatus).</summary>
    public static XDocument FreeBusyQuery(DateTimeOffset start, DateTimeOffset end) =>
        Document(new XElement(
            DavNs.CalDav + "free-busy-query",
            new XElement(DavNs.CalDav + "time-range", new DavTimeRange(start, end).ToAttributes())));

    /// <summary>
    /// RFC 3744 <c>principal-property-search</c> across every principal collection, matching
    /// <c>calendar-user-address-set</c> (<c>mailto:</c>) or Sabre's <c>email-address</c>.
    /// </summary>
    public static XDocument PrincipalSearchByEmail(string address, IEnumerable<XName> properties)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(properties);
        return Document(new XElement(
            DavNs.Dav + "principal-property-search",
            new XAttribute("test", "anyof"),
            PropertySearch(DavNames.CalendarUserAddressSet, "mailto:" + address),
            PropertySearch(DavNames.EmailAddress, address),
            Prop(properties),
            new XElement(DavNs.Dav + "apply-to-principal-collection-set")));
    }

    /// <summary>RFC 4791 §5.3.1 <c>MKCALENDAR</c> body.</summary>
    public static XDocument MkCalendar(string? displayName, IEnumerable<string> components, string? color)
    {
        ArgumentNullException.ThrowIfNull(components);
        var props = new XElement(
            DavNames.Prop,
            displayName is null ? null : new XElement(DavNames.DisplayName, displayName),
            color is null ? null : new XElement(DavNames.CalendarColor, color),
            new XElement(
                DavNames.SupportedCalendarComponentSet,
                components.Select(c => new XElement(DavNames.Comp, new XAttribute("name", c)))));
        return Document(new XElement(DavNs.CalDav + "mkcalendar", new XElement(DavNs.Dav + "set", props)));
    }

    /// <summary>RFC 5689 extended <c>MKCOL</c> body (used to create address books).</summary>
    public static XDocument ExtendedMkCol(IEnumerable<XName> resourceTypes, IEnumerable<XElement> properties)
    {
        ArgumentNullException.ThrowIfNull(resourceTypes);
        ArgumentNullException.ThrowIfNull(properties);
        return Document(new XElement(
            DavNs.Dav + "mkcol",
            new XElement(
                DavNs.Dav + "set",
                new XElement(
                    DavNames.Prop,
                    new XElement(DavNames.ResourceType, resourceTypes.Select(t => new XElement(t))),
                    properties))));
    }

    public static XDocument PropPatch(IEnumerable<XElement> set, IEnumerable<XName> remove)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(remove);
        var setList = set.ToList();
        var removeList = remove.ToList();
        return Document(new XElement(
            DavNs.Dav + "propertyupdate",
            setList.Count == 0 ? null : new XElement(DavNs.Dav + "set", new XElement(DavNames.Prop, setList)),
            removeList.Count == 0 ? null : new XElement(DavNs.Dav + "remove", Prop(removeList))));
    }

    private static XDocument Multiget(XName report, XName dataProperty, IEnumerable<string> hrefs, bool includeData)
    {
        ArgumentNullException.ThrowIfNull(hrefs);
        return Document(new XElement(
            report,
            new XElement(DavNames.Prop, new XElement(DavNames.GetETag), includeData ? new XElement(dataProperty) : null),
            hrefs.Select(h => new XElement(DavNames.Href, h))));
    }

    private static XElement PropertySearch(XName property, string match) =>
        new(
            DavNs.Dav + "property-search",
            new XElement(DavNames.Prop, new XElement(property)),
            new XElement(DavNs.Dav + "match", match));

    private static XElement Prop(IEnumerable<XName> properties) =>
        new(DavNames.Prop, properties.Distinct().Select(p => new XElement(p)));

    /// <summary>Declares the usual prefixes on the root so bodies read like the ones Nextcloud's own clients send.</summary>
    private static XDocument Document(XElement root)
    {
        root.SetAttributeValue(XNamespace.Xmlns + "d", DavNs.Dav.NamespaceName);
        var used = root.DescendantsAndSelf().Select(e => e.Name.Namespace).Distinct().ToList();
        AddPrefix(root, used, "cal", DavNs.CalDav);
        AddPrefix(root, used, "card", DavNs.CardDav);
        AddPrefix(root, used, "cs", DavNs.CalendarServer);
        AddPrefix(root, used, "oc", DavNs.OwnCloud);
        AddPrefix(root, used, "nc", DavNs.Nextcloud);
        AddPrefix(root, used, "s", DavNs.Sabre);
        AddPrefix(root, used, "x1", DavNs.AppleICal);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static void AddPrefix(XElement root, List<XNamespace> used, string prefix, XNamespace ns)
    {
        if (used.Contains(ns))
        {
            root.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
        }
    }
}
