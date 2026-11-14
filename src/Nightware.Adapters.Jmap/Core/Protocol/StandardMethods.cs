using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nightware.Adapters.Jmap.Core.Protocol;

// Generic argument/response shapes of the standard methods (RFC 8620 §5).
// Ids that should come from a back-reference are left null here and bound with
// JmapCall.Ref(...) when the call is added to a JmapRequestBuilder.

/// <summary><c>Foo/get</c> arguments.</summary>
internal class GetRequest
{
    public GetRequest(string accountId) => AccountId = accountId;

    public string AccountId { get; }

    /// <summary>Null fetches every object (only sensible for small types such as Mailbox/Identity).</summary>
    public IReadOnlyList<string>? Ids { get; init; }

    public IReadOnlyList<string>? Properties { get; init; }
}

/// <summary><c>Email/get</c> arguments (RFC 8621 §4.2).</summary>
internal sealed class EmailGetRequest(string accountId) : GetRequest(accountId)
{
    public IReadOnlyList<string>? BodyProperties { get; init; }

    public bool? FetchTextBodyValues { get; init; }

    [JsonPropertyName("fetchHTMLBodyValues")]
    public bool? FetchHtmlBodyValues { get; init; }

    public bool? FetchAllBodyValues { get; init; }

    public long? MaxBodyValueBytes { get; init; }
}

/// <summary><c>Foo/get</c> response.</summary>
internal sealed record GetResponse<T>
{
    public string AccountId { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public IReadOnlyList<T> List { get; init; } = [];

    public IReadOnlyList<string> NotFound { get; init; } = [];
}

/// <summary><c>Foo/changes</c> arguments.</summary>
internal sealed record ChangesRequest(string AccountId, string SinceState)
{
    public int? MaxChanges { get; init; }
}

/// <summary><c>Foo/changes</c> response. <see cref="UpdatedProperties"/> is only sent by <c>Mailbox/changes</c>.</summary>
internal sealed record ChangesResponse
{
    public string AccountId { get; init; } = string.Empty;

    public string OldState { get; init; } = string.Empty;

    public string NewState { get; init; } = string.Empty;

    public bool HasMoreChanges { get; init; }

    public IReadOnlyList<string> Created { get; init; } = [];

    public IReadOnlyList<string> Updated { get; init; } = [];

    public IReadOnlyList<string> Destroyed { get; init; } = [];

    public IReadOnlyList<string>? UpdatedProperties { get; init; }
}

/// <summary>A sort criterion (RFC 8620 §5.5).</summary>
internal sealed record Comparator(string Property, bool IsAscending = true)
{
    public string? Collation { get; init; }

    /// <summary>Only for <c>keyword</c>-based sorts (RFC 8621 §4.4.2).</summary>
    public string? Keyword { get; init; }
}

/// <summary><c>Foo/query</c> arguments.</summary>
internal class QueryRequest<TFilter>
    where TFilter : class
{
    public QueryRequest(string accountId) => AccountId = accountId;

    public string AccountId { get; }

    public TFilter? Filter { get; init; }

    public IReadOnlyList<Comparator>? Sort { get; init; }

    public int? Position { get; init; }

    public string? Anchor { get; init; }

    public int? AnchorOffset { get; init; }

    /// <summary>When the ids feed a <c>#ids</c> back-reference, keep this ≤ the session's maxObjectsInGet.</summary>
    public int? Limit { get; init; }

    public bool? CalculateTotal { get; init; }
}

/// <summary><c>Email/query</c> arguments.</summary>
internal sealed class EmailQueryRequest(string accountId) : QueryRequest<EmailFilter>(accountId)
{
    public bool? CollapseThreads { get; init; }
}

/// <summary><c>Mailbox/query</c> arguments.</summary>
internal sealed class MailboxQueryRequest(string accountId) : QueryRequest<MailboxFilter>(accountId)
{
    public bool? SortAsTree { get; init; }

    public bool? FilterAsTree { get; init; }
}

/// <summary><c>Foo/query</c> response.</summary>
internal sealed record QueryResponse
{
    public string AccountId { get; init; } = string.Empty;

    public string QueryState { get; init; } = string.Empty;

    public bool CanCalculateChanges { get; init; }

    public int Position { get; init; }

    public IReadOnlyList<string> Ids { get; init; } = [];

    public int? Total { get; init; }

    public int? Limit { get; init; }
}

/// <summary><c>Foo/queryChanges</c> arguments.</summary>
internal class QueryChangesRequest<TFilter>
    where TFilter : class
{
    public QueryChangesRequest(string accountId, string sinceQueryState)
    {
        AccountId = accountId;
        SinceQueryState = sinceQueryState;
    }

    public string AccountId { get; }

    public string SinceQueryState { get; }

    public TFilter? Filter { get; init; }

    public IReadOnlyList<Comparator>? Sort { get; init; }

    public int? MaxChanges { get; init; }

    public string? UpToId { get; init; }

    public bool? CalculateTotal { get; init; }
}

internal sealed record AddedItem(string Id, int Index);

/// <summary><c>Foo/queryChanges</c> response.</summary>
internal sealed record QueryChangesResponse
{
    public string AccountId { get; init; } = string.Empty;

    public string OldQueryState { get; init; } = string.Empty;

    public string NewQueryState { get; init; } = string.Empty;

    public int? Total { get; init; }

    public IReadOnlyList<string> Removed { get; init; } = [];

    public IReadOnlyList<AddedItem> Added { get; init; } = [];
}

/// <summary><c>Foo/set</c> arguments. <see cref="Update"/> values are PatchObjects (see <see cref="PatchObject"/>).</summary>
internal class SetRequest<T>
{
    public SetRequest(string accountId) => AccountId = accountId;

    public string AccountId { get; }

    public string? IfInState { get; init; }

    public IReadOnlyDictionary<string, T>? Create { get; init; }

    public IReadOnlyDictionary<string, PatchObject>? Update { get; init; }

    public IReadOnlyList<string>? Destroy { get; init; }
}

/// <summary><c>Mailbox/set</c> arguments.</summary>
internal sealed class MailboxSetRequest(string accountId) : SetRequest<Mailbox>(accountId)
{
    public bool? OnDestroyRemoveEmails { get; init; }
}

/// <summary><c>EmailSubmission/set</c> arguments (RFC 8621 §7.5).</summary>
internal sealed class EmailSubmissionSetRequest(string accountId) : SetRequest<EmailSubmission>(accountId)
{
    /// <summary>Keyed by EmailSubmission id or <c>#creationId</c>.</summary>
    public IReadOnlyDictionary<string, PatchObject>? OnSuccessUpdateEmail { get; init; }

    public IReadOnlyList<string>? OnSuccessDestroyEmail { get; init; }
}

/// <summary>A per-object failure in a <c>Foo/set</c> response (RFC 8620 §5.3).</summary>
internal sealed record SetError
{
    public string Type { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>For <c>invalidProperties</c>.</summary>
    public IReadOnlyList<string>? Properties { get; init; }

    /// <summary>For <c>alreadyExists</c>.</summary>
    public string? ExistingId { get; init; }

    /// <summary>For <c>blobNotFound</c>.</summary>
    public IReadOnlyList<string>? NotFound { get; init; }

    /// <summary>For <c>tooLarge</c> on EmailSubmission.</summary>
    public long? MaxSize { get; init; }

    /// <summary>For <c>invalidRecipients</c>.</summary>
    public IReadOnlyList<string>? InvalidRecipients { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary><c>Foo/set</c> response. <c>updated</c> values are null when the server changed nothing else.</summary>
internal sealed record SetResponse<T>
{
    public string AccountId { get; init; } = string.Empty;

    public string? OldState { get; init; }

    public string NewState { get; init; } = string.Empty;

    public Dictionary<string, T>? Created { get; init; }

    public Dictionary<string, T?>? Updated { get; init; }

    public IReadOnlyList<string>? Destroyed { get; init; }

    public Dictionary<string, SetError>? NotCreated { get; init; }

    public Dictionary<string, SetError>? NotUpdated { get; init; }

    public Dictionary<string, SetError>? NotDestroyed { get; init; }
}

/// <summary>
/// A JMAP PatchObject: JSON-pointer paths (relative to the object) to new values;
/// a null value removes/resets the property. Build it with <see cref="Patch"/>.
/// </summary>
internal sealed class PatchObject : Dictionary<string, JsonNode?>
{
    public PatchObject()
        : base(StringComparer.Ordinal)
    {
    }
}

/// <summary>Fluent builder for <see cref="PatchObject"/> that escapes map keys as JSON-pointer tokens.</summary>
internal sealed class Patch
{
    private readonly PatchObject _patch = [];

    /// <summary>Escapes a map key (keyword, mailbox id) for use as one JSON-pointer path segment (RFC 6901).</summary>
    public static string Escape(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    /// <summary>Sets <paramref name="path"/> (already a JSON pointer, e.g. <c>subject</c>) to a value.</summary>
    public Patch Set(string path, JsonNode? value)
    {
        _patch[path] = value;
        return this;
    }

    /// <summary>Sets a map entry, e.g. <c>SetEntry("keywords", "$seen", true)</c> → <c>keywords/$seen: true</c>.</summary>
    public Patch SetEntry(string property, string key, bool value = true) =>
        Set($"{property}/{Escape(key)}", value ? JsonValue.Create(true) : null);

    /// <summary>Removes a map entry, e.g. <c>RemoveEntry("mailboxIds", id)</c> → <c>mailboxIds/id: null</c>.</summary>
    public Patch RemoveEntry(string property, string key) => Set($"{property}/{Escape(key)}", null);

    public PatchObject Build()
    {
        var copy = new PatchObject();
        foreach (var (key, value) in _patch)
        {
            copy[key] = value?.DeepClone();
        }

        return copy;
    }
}
