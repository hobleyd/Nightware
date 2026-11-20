using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Graph.Json;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Features.Mail.Rules;

/// <summary>Graph <c>messageRulePredicates</c>; only what is set is written, as Graph does.</summary>
public sealed record MessageRulePredicatesDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SubjectContains { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? BodyContains { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? BodyOrSubjectContains { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? SenderContains { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? RecipientContains { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RecipientDto>? FromAddresses { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RecipientDto>? SentToAddresses { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasAttachments { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Importance { get; init; }
}

/// <summary>Graph <c>messageRuleActions</c>; only what is set is written.</summary>
public sealed record MessageRuleActionsDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MoveToFolder { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CopyToFolder { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Delete { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PermanentDelete { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? MarkAsRead { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RecipientDto>? ForwardTo { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RecipientDto>? RedirectTo { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StopProcessingRules { get; init; }
}

/// <summary>Graph <c>messageRule</c>.</summary>
public sealed record MessageRuleDto
{
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public int? Sequence { get; init; }

    public bool? IsEnabled { get; init; }

    public bool HasError { get; init; }

    public bool IsReadOnly { get; init; }

    public MessageRulePredicatesDto? Conditions { get; init; }

    public MessageRuleActionsDto? Actions { get; init; }

    public MessageRulePredicatesDto? Exceptions { get; init; }
}

/// <summary>Maps <c>messageRule</c> to and from the Domain, refusing what a Sieve script cannot do (ADR 0006 D5).</summary>
public static class MessageRuleMapper
{
    private static readonly HashSet<string> RuleMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "displayName", "sequence", "isEnabled", "hasError", "isReadOnly", "conditions", "actions", "exceptions",
    };

    private static readonly HashSet<string> PredicateMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "subjectContains", "bodyContains", "bodyOrSubjectContains", "senderContains", "recipientContains",
        "fromAddresses", "sentToAddresses", "hasAttachments", "importance",
    };

    private static readonly HashSet<string> ActionMembers = new(StringComparer.OrdinalIgnoreCase)
    {
        "moveToFolder", "copyToFolder", "delete", "permanentDelete", "markAsRead", "forwardTo", "redirectTo", "stopProcessingRules",
    };

    public static MessageRuleDto ToDto(MessageRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new MessageRuleDto
        {
            Id = rule.Id,
            DisplayName = rule.DisplayName,
            Sequence = rule.Sequence,
            IsEnabled = rule.IsEnabled,
            Conditions = ToDto(rule.Conditions),
            Exceptions = ToDto(rule.Exceptions),
            Actions = ToDto(rule.Actions),
        };
    }

    /// <summary>A new rule from a POST body (sequence defaults to last, enabled defaults to true).</summary>
    public static Result<MessageRule> ToNew(JsonObject? body)
    {
        var dto = Read(body);
        return dto.Bind(d => Apply(new MessageRule(string.Empty, d.DisplayName ?? string.Empty, d.Sequence ?? int.MaxValue, d.IsEnabled ?? true), d, body!));
    }

    /// <summary>A PATCH body applied to <paramref name="rule"/>: present members replace, absent ones are kept.</summary>
    public static Func<MessageRule, Result<MessageRule>> ToChange(JsonObject? body) => rule =>
        Read(body).Bind(d => Apply(rule with
        {
            DisplayName = d.DisplayName ?? rule.DisplayName,
            Sequence = d.Sequence ?? rule.Sequence,
            IsEnabled = d.IsEnabled ?? rule.IsEnabled,
        }, d, body!));

    private static Result<MessageRuleDto> Read(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("The request has no body.");
        }

        var unsupported = Unsupported(body, RuleMembers, string.Empty)
            ?? Unsupported(Member(body, "conditions"), PredicateMembers, "conditions.")
            ?? Unsupported(Member(body, "exceptions"), PredicateMembers, "exceptions.")
            ?? Unsupported(Member(body, "actions"), ActionMembers, "actions.");
        if (unsupported is not null)
        {
            return unsupported;
        }

        try
        {
            return body.Deserialize<MessageRuleDto>(GraphJson.Options) ?? new MessageRuleDto();
        }
        catch (JsonException ex)
        {
            return Error.Validation($"The rule is malformed: {ex.Message}");
        }
    }

    private static Result<MessageRule> Apply(MessageRule rule, MessageRuleDto dto, JsonObject body)
    {
        var conditions = Member(body, "conditions") is null ? Result<MessageRulePredicates>.Success(rule.Conditions) : ToDomain(dto.Conditions, "conditions");
        var exceptions = Member(body, "exceptions") is null ? Result<MessageRulePredicates>.Success(rule.Exceptions) : ToDomain(dto.Exceptions, "exceptions");
        if (!conditions.IsSuccess)
        {
            return conditions.Error;
        }

        if (!exceptions.IsSuccess)
        {
            return exceptions.Error;
        }

        return rule with
        {
            Conditions = conditions.Value,
            Exceptions = exceptions.Value,
            Actions = Member(body, "actions") is null ? rule.Actions : ToDomain(dto.Actions),
        };
    }

    private static Error? Unsupported(JsonNode? node, HashSet<string> supported, string prefix) =>
        node is JsonObject obj && obj.Select(static p => p.Key).FirstOrDefault(k => !supported.Contains(k) && !k.StartsWith('@')) is { } key
            ? Error.Unsupported($"'{prefix}{key}' cannot be carried out by this server's mail filter.", prefix + key)
            : null;

    private static JsonNode? Member(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static Result<MessageRulePredicates> ToDomain(MessageRulePredicatesDto? dto, string target)
    {
        if (dto is null)
        {
            return new MessageRulePredicates();
        }

        Importance? importance = null;
        if (dto.Importance is { } text)
        {
            if (!Enum.TryParse<Importance>(text, ignoreCase: true, out var parsed))
            {
                return Error.Validation($"'{text}' is not an importance.", target + ".importance");
            }

            importance = parsed;
        }

        return new MessageRulePredicates
        {
            SubjectContains = dto.SubjectContains ?? [],
            BodyContains = dto.BodyContains ?? [],
            BodyOrSubjectContains = dto.BodyOrSubjectContains ?? [],
            SenderContains = dto.SenderContains ?? [],
            RecipientContains = dto.RecipientContains ?? [],
            FromAddresses = Addresses(dto.FromAddresses),
            SentToAddresses = Addresses(dto.SentToAddresses),
            HasAttachments = dto.HasAttachments,
            Importance = importance,
        };
    }

    private static MessageRuleActions ToDomain(MessageRuleActionsDto? dto) => dto is null
        ? new MessageRuleActions()
        : new MessageRuleActions
        {
            MoveToFolder = string.IsNullOrEmpty(dto.MoveToFolder) ? null : new FolderId(dto.MoveToFolder),
            CopyToFolder = string.IsNullOrEmpty(dto.CopyToFolder) ? null : new FolderId(dto.CopyToFolder),
            Delete = dto.Delete == true,
            PermanentDelete = dto.PermanentDelete == true,
            MarkAsRead = dto.MarkAsRead == true,
            ForwardTo = Addresses(dto.ForwardTo),
            RedirectTo = Addresses(dto.RedirectTo),
            StopProcessingRules = dto.StopProcessingRules == true,
        };

    private static List<EmailAddress> Addresses(IReadOnlyList<RecipientDto>? recipients) =>
        [.. (recipients ?? []).Select(static r => r.EmailAddress).Where(static a => !string.IsNullOrWhiteSpace(a?.Address))
            .Select(static a => new EmailAddress(a!.Address!, a.Name))];

    private static MessageRulePredicatesDto? ToDto(MessageRulePredicates p) => p.IsEmpty ? null : new MessageRulePredicatesDto
    {
        SubjectContains = NullIfEmpty(p.SubjectContains),
        BodyContains = NullIfEmpty(p.BodyContains),
        BodyOrSubjectContains = NullIfEmpty(p.BodyOrSubjectContains),
        SenderContains = NullIfEmpty(p.SenderContains),
        RecipientContains = NullIfEmpty(p.RecipientContains),
        FromAddresses = Recipients(p.FromAddresses),
        SentToAddresses = Recipients(p.SentToAddresses),
        HasAttachments = p.HasAttachments,
        Importance = p.Importance is { } i ? MessageDtoMapper.ToGraph(i) : null,
    };

    private static MessageRuleActionsDto? ToDto(MessageRuleActions a) => a.IsEmpty ? null : new MessageRuleActionsDto
    {
        MoveToFolder = a.MoveToFolder?.Value,
        CopyToFolder = a.CopyToFolder?.Value,
        Delete = a.Delete ? true : null,
        PermanentDelete = a.PermanentDelete ? true : null,
        MarkAsRead = a.MarkAsRead ? true : null,
        ForwardTo = Recipients(a.ForwardTo),
        RedirectTo = Recipients(a.RedirectTo),
        StopProcessingRules = a.StopProcessingRules ? true : null,
    };

    private static IReadOnlyList<string>? NullIfEmpty(IReadOnlyList<string> values) => values.Count == 0 ? null : values;

    private static IReadOnlyList<RecipientDto>? Recipients(IReadOnlyList<EmailAddress> addresses) =>
        addresses.Count == 0 ? null : [.. addresses.Select(static a => new RecipientDto { EmailAddress = new EmailAddressDto { Name = a.Name ?? a.Address, Address = a.Address } })];
}
