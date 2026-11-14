using Nightware.Domain.Kernel;

namespace Nightware.Domain.Security;

public enum ThreatCategory
{
    Spam,
    Phishing,
    Malware,
    NotJunk,
}

/// <summary>Result of reporting a message (Graph <c>emailThreatSubmission</c>).</summary>
public sealed record ThreatSubmission(string Id, ThreatCategory Category, MessageId MessageId, DateTimeOffset CreatedDateTime)
{
    public string Status { get; init; } = "succeeded";
}
