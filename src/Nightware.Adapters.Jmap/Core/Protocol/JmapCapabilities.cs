namespace Nightware.Adapters.Jmap.Core.Protocol;

/// <summary>Capability URIs used in the request <c>using</c> list and in the session document.</summary>
internal static class JmapCapabilities
{
    public const string Core = "urn:ietf:params:jmap:core";
    public const string Mail = "urn:ietf:params:jmap:mail";
    public const string Submission = "urn:ietf:params:jmap:submission";
    public const string VacationResponse = "urn:ietf:params:jmap:vacationresponse";
    public const string Sieve = "urn:ietf:params:jmap:sieve";
    public const string Blob = "urn:ietf:params:jmap:blob";
    public const string Quota = "urn:ietf:params:jmap:quota";
    public const string Metadata = "urn:ietf:params:jmap:metadata";
}
