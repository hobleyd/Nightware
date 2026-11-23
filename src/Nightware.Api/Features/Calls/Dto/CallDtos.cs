using System.Text.Json.Serialization;

using Nightware.Domain.Calls;

namespace Nightware.Api.Features.Calls.Dto;

/// <summary>
/// Graph <c>call</c>, delegated (ADR 0009 D7): always <c>outgoing</c> from the caller. <c>chatInfo.threadId</c> is the Talk
/// room, and <c>@nightware.joinWebUrl</c> (an instance annotation) its join link, which the caller's own client opens for
/// media; Graph's bot-media members are not emitted.
/// </summary>
public sealed record CallDto
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.call";

    public required string Id { get; init; }

    public required string State { get; init; }

    public string Direction { get; init; } = "outgoing";

    public string? CallbackUri { get; init; }

    public ParticipantInfoDto? Source { get; init; }

    public IReadOnlyList<InvitationParticipantInfoDto> Targets { get; init; } = [];

    public IReadOnlyList<string> RequestedModalities { get; init; } = [];

    public required CallChatInfoDto ChatInfo { get; init; }

    [JsonPropertyName("@nightware.joinWebUrl")]
    public required string JoinWebUrl { get; init; }

    public static CallDto From(Call call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return new CallDto
        {
            Id = call.Id,
            State = call.State switch
            {
                CallState.Established => "established",
                CallState.Terminated => "terminated",
                _ => "establishing",
            },
            CallbackUri = call.CallbackUri,
            Source = call.Source is { } source ? new ParticipantInfoDto { Identity = CallIdentitySetDto.Of(source) } : null,
            Targets = [.. call.Targets.Select(t => new InvitationParticipantInfoDto { Identity = CallIdentitySetDto.Of(t) })],
            RequestedModalities = call.RequestedModalities,
            ChatInfo = new CallChatInfoDto { ThreadId = call.ChatThreadId },
            JoinWebUrl = call.JoinWebUrl,
        };
    }
}

public sealed record ParticipantInfoDto
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.participantInfo";

    public required CallIdentitySetDto Identity { get; init; }
}

public sealed record InvitationParticipantInfoDto
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.invitationParticipantInfo";

    public required CallIdentitySetDto Identity { get; init; }
}

public sealed record CallIdentitySetDto
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.identitySet";

    public required CallIdentityDto User { get; init; }

    public static CallIdentitySetDto Of(CallParty party)
    {
        ArgumentNullException.ThrowIfNull(party);
        return new CallIdentitySetDto { User = new CallIdentityDto { Id = party.UserId, DisplayName = party.DisplayName } };
    }
}

public sealed record CallIdentityDto
{
    [JsonPropertyName("@odata.type")]
    public string ODataType { get; init; } = "#microsoft.graph.identity";

    public string? DisplayName { get; init; }

    public string? Id { get; init; }
}

public sealed record CallChatInfoDto
{
    public required string ThreadId { get; init; }

    public string MessageId { get; init; } = "0";

    public string? ReplyChainMessageId { get; init; }
}
