using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Nightware.Adapters.Dav.Core;

/// <summary>One <c>DAV:propstat</c>: the properties that share a status.</summary>
public sealed record DavPropStat(int StatusCode, IReadOnlyList<XElement> Properties)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300;
}

/// <summary>One <c>DAV:response</c> of a multistatus.</summary>
/// <param name="Href">The href exactly as Sabre sent it (server-absolute, percent-encoded).</param>
/// <param name="StatusCode">The response-level status (used for missing/deleted members); null when the response carries propstats.</param>
public sealed record DavResponse(string Href, int? StatusCode, IReadOnlyList<DavPropStat> PropStats)
{
    /// <summary>False when the response-level status is not 2xx (e.g. a 404 member of a multiget or a sync-collection deletion).</summary>
    public bool IsFound => StatusCode is null or (>= 200 and < 300);

    /// <summary>The property if it came back in a 2xx propstat.</summary>
    public XElement? Find(XName name) =>
        PropStats.Where(p => p.IsSuccess).SelectMany(p => p.Properties).FirstOrDefault(p => p.Name == name);

    /// <summary>The status reported for a property (200, 404, 403…), or null when it is absent.</summary>
    public int? StatusOf(XName name) =>
        PropStats.FirstOrDefault(p => p.Properties.Any(e => e.Name == name))?.StatusCode;

    /// <summary>Text value of a found property, or null.</summary>
    public string? Text(XName name)
    {
        var element = Find(name);
        return element is null || element.IsEmpty ? null : element.Value;
    }

    /// <summary>The first <c>DAV:href</c> inside a found property (e.g. <c>calendar-home-set</c>).</summary>
    public string? HrefOf(XName name) => HrefsOf(name) is { Count: > 0 } hrefs ? hrefs[0] : null;

    /// <summary>Every <c>DAV:href</c> inside a found property (e.g. <c>calendar-user-address-set</c>).</summary>
    public IReadOnlyList<string> HrefsOf(XName name) =>
        Find(name)?.Descendants(DavNames.Href).Select(h => h.Value.Trim()).Where(h => h.Length > 0).ToList() ?? [];

    /// <summary>True when <c>DAV:resourcetype</c> contains <paramref name="type"/>.</summary>
    public bool HasResourceType(XName type) => Find(DavNames.ResourceType)?.Element(type) is not null;

    public string? ETag => Text(DavNames.GetETag);

    public string? CalendarData => Text(DavNames.CalendarData);

    public string? AddressData => Text(DavNames.AddressData);
}

/// <summary>A parsed <c>207 Multi-Status</c> body.</summary>
public sealed record DavMultistatus(IReadOnlyList<DavResponse> Responses, string? SyncToken = null)
{
    public static DavMultistatus Empty { get; } = new([]);

    /// <summary>The response whose href matches <paramref name="href"/> (ignoring a trailing slash).</summary>
    public DavResponse? For(string href)
    {
        ArgumentNullException.ThrowIfNull(href);
        var wanted = href.TrimEnd('/');
        return Responses.FirstOrDefault(r => string.Equals(r.Href.TrimEnd('/'), wanted, StringComparison.Ordinal));
    }
}

/// <summary>Parses multistatus and DAV error bodies. DTDs and external entities are refused.</summary>
public static class DavXml
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    public static XDocument Load(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var stringReader = new StringReader(xml);
        using var reader = XmlReader.Create(stringReader, ReaderSettings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    /// <summary>Parses a multistatus; throws <see cref="XmlException"/> or <see cref="FormatException"/> on malformed input.</summary>
    public static DavMultistatus ParseMultistatus(string xml)
    {
        var document = Load(xml);
        var root = document.Root;
        if (root is null || root.Name != DavNames.Multistatus)
        {
            throw new FormatException("Expected a DAV:multistatus document.");
        }

        var responses = root.Elements(DavNames.Response).Select(ParseResponse).ToList();
        var syncToken = root.Element(DavNames.SyncToken)?.Value.Trim();
        return new DavMultistatus(responses, string.IsNullOrEmpty(syncToken) ? null : syncToken);
    }

    /// <summary>Parses <c>HTTP/1.1 404 Not Found</c> into 404.</summary>
    public static int? ParseStatusLine(string? statusLine)
    {
        if (string.IsNullOrWhiteSpace(statusLine))
        {
            return null;
        }

        var parts = statusLine.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code)
            ? code
            : null;
    }

    /// <summary>The precondition/postcondition element names inside a <c>DAV:error</c> body; empty when the body is not one.</summary>
    public static IReadOnlyList<XName> ParseErrorConditions(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('<'))
        {
            return [];
        }

        try
        {
            var root = Load(body).Root;
            return root is not null && root.Name == DavNames.Error
                ? root.Elements().Select(e => e.Name).ToList()
                : [];
        }
        catch (XmlException)
        {
            return [];
        }
    }

    private static DavResponse ParseResponse(XElement response)
    {
        var href = response.Element(DavNames.Href)?.Value.Trim()
                   ?? throw new FormatException("DAV:response without DAV:href.");
        var status = ParseStatusLine(response.Element(DavNames.Status)?.Value);
        var propStats = response.Elements(DavNames.PropStat)
            .Select(ps => new DavPropStat(
                ParseStatusLine(ps.Element(DavNames.Status)?.Value) ?? 200,
                ps.Element(DavNames.Prop)?.Elements().ToList() ?? []))
            .ToList();
        return new DavResponse(href, status, propStats);
    }
}
