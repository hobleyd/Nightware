using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Mail.Rules;

namespace Nightware.Api.Features.Mail.Rules;

/// <summary>Inbox rules, <c>mailFolders/inbox/messageRules</c> (ADR 0006 D5, phase 12): one Sieve script on Stalwart.</summary>
internal static class MessageRuleEndpoints
{
    public static void Map(IEndpointRouteBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/mailFolders/inbox/messageRules", ListAsync);
            scope.MapPost("/mailFolders/inbox/messageRules", CreateAsync);
            scope.MapGet("/mailFolders/inbox/messageRules/{ruleId}", GetAsync);
            scope.MapPatch("/mailFolders/inbox/messageRules/{ruleId}", UpdateAsync);
            scope.MapDelete("/mailFolders/inbox/messageRules/{ruleId}", DeleteAsync);
        }
    }

    private static string Context(Mailbox mailbox) => $"{mailbox.ContextSegment}/mailFolders('inbox')/messageRules";

    private static async Task<IResult> ListAsync(Mailbox mailbox, ODataQuery odata, ListMessageRules useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, cancellationToken),
            rules => GraphResults.Collection(rules.Select(MessageRuleMapper.ToDto), Context(mailbox), odata.Query));

    private static async Task<IResult> GetAsync(Mailbox mailbox, string ruleId, ODataQuery odata, GetMessageRule useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, ruleId, cancellationToken),
            r => GraphResults.Entity(MessageRuleMapper.ToDto(r), Context(mailbox), odata.Query.Select));

    private static async Task<IResult> CreateAsync(Mailbox mailbox, [FromBody] JsonObject? body, CreateMessageRule useCase, CancellationToken cancellationToken)
    {
        var rule = MessageRuleMapper.ToNew(body);
        if (!rule.IsSuccess)
        {
            return GraphResults.Error(rule.Error);
        }

        return GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, rule.Value, cancellationToken),
            r => GraphResults.CreatedAt(MessageRuleMapper.ToDto(r), Context(mailbox), $"{Context(mailbox)}('{r.Id}')"));
    }

    private static async Task<IResult> UpdateAsync(
        Mailbox mailbox, string ruleId, ODataQuery odata, [FromBody] JsonObject? body, UpdateMessageRule useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, ruleId, MessageRuleMapper.ToChange(body), cancellationToken),
            r => GraphResults.Entity(MessageRuleMapper.ToDto(r), Context(mailbox), odata.Query.Select));

    private static async Task<IResult> DeleteAsync(Mailbox mailbox, string ruleId, DeleteMessageRule useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, ruleId, cancellationToken));
}
