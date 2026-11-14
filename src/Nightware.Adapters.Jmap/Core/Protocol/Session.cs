using System.Text.Json;

namespace Nightware.Adapters.Jmap.Core.Protocol;

/// <summary>The JMAP Session resource (RFC 8620 §2).</summary>
internal sealed record JmapSessionDocument
{
    public Dictionary<string, JsonElement> Capabilities { get; init; } = [];

    public Dictionary<string, JmapAccountInfo> Accounts { get; init; } = [];

    public Dictionary<string, string> PrimaryAccounts { get; init; } = [];

    public string Username { get; init; } = string.Empty;

    public string ApiUrl { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public string UploadUrl { get; init; } = string.Empty;

    public string? EventSourceUrl { get; init; }

    public string State { get; init; } = string.Empty;
}

/// <summary>An entry of <c>session.accounts</c>.</summary>
internal sealed record JmapAccountInfo
{
    public string Name { get; init; } = string.Empty;

    public bool IsPersonal { get; init; }

    public bool IsReadOnly { get; init; }

    public Dictionary<string, JsonElement> AccountCapabilities { get; init; } = [];
}

/// <summary><c>urn:ietf:params:jmap:core</c> capability object: the server's request limits.</summary>
internal sealed record CoreCapability
{
    public long MaxSizeUpload { get; init; }

    public int MaxConcurrentUpload { get; init; }

    public long MaxSizeRequest { get; init; }

    public int MaxConcurrentRequests { get; init; }

    public int MaxCallsInRequest { get; init; }

    public int MaxObjectsInGet { get; init; }

    public int MaxObjectsInSet { get; init; }

    public IReadOnlyList<string> CollationAlgorithms { get; init; } = [];
}

/// <summary><c>urn:ietf:params:jmap:mail</c> account capability (RFC 8621 §1.3.1).</summary>
internal sealed record MailAccountCapability
{
    public int? MaxMailboxesPerEmail { get; init; }

    public int? MaxMailboxDepth { get; init; }

    public int MaxSizeMailboxName { get; init; }

    public long MaxSizeAttachmentsPerEmail { get; init; }

    public IReadOnlyList<string> EmailQuerySortOptions { get; init; } = [];

    public bool MayCreateTopLevelMailbox { get; init; }
}

/// <summary><c>urn:ietf:params:jmap:submission</c> account capability (RFC 8621 §1.3.2).</summary>
internal sealed record SubmissionAccountCapability
{
    public long MaxDelayedSend { get; init; }

    public Dictionary<string, IReadOnlyList<string>> SubmissionExtensions { get; init; } = [];
}
