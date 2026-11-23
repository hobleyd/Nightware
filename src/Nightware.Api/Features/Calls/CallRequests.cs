using System.Text.Json.Nodes;

using Nightware.Domain.Calls;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Calls;

/// <summary>Reads <c>POST /communications/calls</c>: <c>targets[].identity.user.id</c>, <c>requestedModalities</c>, <c>callbackUri</c>, <c>subject</c>.</summary>
internal static class CallRequests
{
    public static Result<NewCall> ReadNew(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("The request needs a JSON body.");
        }

        if (Member(body, "targets") is not JsonArray targets)
        {
            return Error.Validation("targets must be an array of invitationParticipantInfo.", "targets");
        }

        var ids = new List<string>();
        foreach (var target in targets)
        {
            var identity = target is JsonObject t ? Member(t, "identity") as JsonObject : null;
            var user = identity is null ? null : Member(identity, "user") as JsonObject;
            ids.Add((user is null ? null : StringOf(Member(user, "id"))) ?? string.Empty);
        }

        var modalities = new List<string>();
        switch (Member(body, "requestedModalities"))
        {
            case null:
                modalities.Add("audio");
                break;
            case JsonArray array:
                foreach (var modality in array)
                {
                    if (StringOf(modality) is not { } text)
                    {
                        return Error.Validation("requestedModalities must be strings.", "requestedModalities");
                    }

                    modalities.Add(text);
                }

                break;
            default:
                return Error.Validation("requestedModalities must be an array.", "requestedModalities");
        }

        var callback = Member(body, "callbackUri");
        if (callback is not null && StringOf(callback) is null)
        {
            return Error.Validation("callbackUri must be a URL.", "callbackUri");
        }

        return new NewCall(ids, modalities, StringOf(callback)) { Subject = StringOf(Member(body, "subject")) };
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static JsonNode? Member(JsonObject body, string name) =>
        body.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
