using System.Globalization;
using System.Text.Json.Nodes;

using Nightware.Api.Graph.Routing;
using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Api.Features.OnlineMeetings;

/// <summary>Reads online-meeting requests: create and update bodies, the <c>JoinWebUrl</c> filter, and who may ask.</summary>
internal static class OnlineMeetingRequests
{
    /// <summary>Meetings are the caller's own: <c>/me</c>, or <c>/users/{x}</c> naming the caller (the backend acts as the caller).</summary>
    public static Error? OwnMeetings(Mailbox mailbox, IUserContext caller)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(caller);
        return mailbox.IsMe || string.Equals(mailbox.Ref.Address, caller.Principal.Mail, StringComparison.OrdinalIgnoreCase)
            ? null
            : Error.Forbidden("Only your own online meetings can be reached.");
    }

    /// <summary><c>POST onlineMeetings</c>: subject, the span, and <c>participants.attendees[].identity.user.id</c> (or <c>upn</c>).</summary>
    public static Result<NewOnlineMeeting> ReadNew(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("The request needs a JSON body.");
        }

        var subject = Text(body, "subject");
        if (!subject.IsSuccess)
        {
            return subject.Error;
        }

        var start = Instant(body, "startDateTime");
        var end = Instant(body, "endDateTime");
        if (!start.IsSuccess || !end.IsSuccess)
        {
            return start.IsSuccess ? end.Error! : start.Error;
        }

        var attendees = new List<string>();
        if (Member(body, "participants") is JsonObject participants && Member(participants, "attendees") is { } list)
        {
            if (list is not JsonArray array)
            {
                return Error.Validation("participants.attendees must be an array.", "participants");
            }

            foreach (var attendee in array)
            {
                var identity = attendee is JsonObject a ? Member(a, "identity") as JsonObject : null;
                var user = identity is null ? null : Member(identity, "user") as JsonObject;
                var name = (user is null ? null : StringOf(Member(user, "id"))) ?? (attendee is JsonObject b ? StringOf(Member(b, "upn")) : null);
                attendees.Add(name ?? string.Empty);
            }
        }

        return new NewOnlineMeeting(subject.Value, start.Value, end.Value, attendees);
    }

    /// <summary><c>PATCH onlineMeetings/{id}</c>: <c>subject</c>, and <c>startDateTime</c> with <c>endDateTime</c>. Other members are ignored.</summary>
    public static Result<OnlineMeetingChange> ReadChange(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("The request needs a JSON body.");
        }

        var subject = Text(body, "subject");
        var start = Instant(body, "startDateTime");
        var end = Instant(body, "endDateTime");
        if (!subject.IsSuccess)
        {
            return subject.Error;
        }

        if (!start.IsSuccess || !end.IsSuccess)
        {
            return start.IsSuccess ? end.Error! : start.Error;
        }

        return new OnlineMeetingChange(HasMember(body, "subject"), subject.Value, start.Value, end.Value);
    }

    /// <summary>
    /// The link a <c>$filter=JoinWebUrl eq '…'</c> names. Graph finds meetings only this way, so a listing without it, or
    /// any other filter, is 400.
    /// </summary>
    public static Result<string> JoinUrlFilter(ItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Filter is FilterNode.Compare { Op: CompareOp.Eq, Value: FilterValue.Text { Value: var url } } compare
            && string.Equals(compare.Property, "JoinWebUrl", StringComparison.OrdinalIgnoreCase)
                ? url
                : Error.Validation("Online meetings are found with $filter=JoinWebUrl eq '{joinWebUrl}' only.", "$filter");
    }

    private static Result<string?> Text(JsonObject body, string name) =>
        Member(body, name) switch
        {
            null => Result<string?>.Success(null),
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => Error.Validation($"{name} must be a string.", name),
        };

    private static Result<DateTimeOffset?> Instant(JsonObject body, string name) =>
        Member(body, name) switch
        {
            null => Result<DateTimeOffset?>.Success(null),
            JsonValue value when value.TryGetValue<string>(out var text)
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) => at.ToUniversalTime(),
            _ => Error.Validation($"{name} must be an ISO 8601 date and time.", name),
        };

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    /// <summary>A member by name, case-insensitively as Graph binds bodies.</summary>
    private static JsonNode? Member(JsonObject body, string name) =>
        body.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool HasMember(JsonObject body, string name) =>
        body.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));
}
