using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Nightware.Adapters.Jmap.Core.Requests;

/// <summary>A back-reference to an earlier call's result (RFC 8620 §3.7).</summary>
internal sealed record ResultReference(string ResultOf, string Name, string Path)
{
    public JsonObject ToJson() => new()
    {
        ["resultOf"] = ResultOf,
        ["name"] = Name,
        ["path"] = Path,
    };
}

/// <summary>Binds argument <paramref name="Argument"/> (e.g. <c>ids</c>) to a result reference; sent as <c>#ids</c>.</summary>
internal sealed record ArgumentReference(string Argument, ResultReference Reference);

/// <summary>Common JSON-pointer paths into method responses.</summary>
internal static class JmapPaths
{
    /// <summary><c>Foo/query</c> result ids.</summary>
    public const string Ids = "/ids";

    /// <summary>Ids of every object in a <c>Foo/get</c> list.</summary>
    public const string ListIds = "/list/*/id";

    /// <summary>Thread ids of every Email in an <c>Email/get</c> list.</summary>
    public const string ListThreadIds = "/list/*/threadId";

    /// <summary>Email ids of every Thread in a <c>Thread/get</c> list.</summary>
    public const string ListEmailIds = "/list/*/emailIds";

    /// <summary><c>Foo/changes</c> created ids.</summary>
    public const string Created = "/created";

    /// <summary><c>Foo/changes</c> updated ids.</summary>
    public const string Updated = "/updated";

    /// <summary><c>Foo/changes</c> destroyed ids.</summary>
    public const string Destroyed = "/destroyed";
}

/// <summary>A call added to a <see cref="JmapRequestBuilder"/>; use it to reference its result and to read its response.</summary>
internal abstract class JmapCall
{
    private protected JmapCall(string callId, string method)
    {
        CallId = callId;
        Method = method;
    }

    public string CallId { get; }

    public string Method { get; }

    /// <summary>A reference to <paramref name="path"/> in this call's result.</summary>
    public ResultReference Ref(string path) => new(CallId, Method, path);

    /// <summary>Binds <paramref name="argument"/> of a later call to <paramref name="path"/> in this call's result.</summary>
    public ArgumentReference Ref(string argument, string path) => new(argument, Ref(path));
}

/// <inheritdoc />
internal sealed class JmapCall<TResponse> : JmapCall
{
    internal JmapCall(string callId, string method, JsonTypeInfo<TResponse> responseType)
        : base(callId, method) => ResponseType = responseType;

    internal JsonTypeInfo<TResponse> ResponseType { get; }
}
