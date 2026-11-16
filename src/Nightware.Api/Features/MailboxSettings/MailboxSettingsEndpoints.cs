using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.MailboxSettings.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.MailboxSettings;

namespace Nightware.Api.Features.MailboxSettings;

public static class MailboxSettingsEndpoints
{
    /// <summary>
    /// Maps <c>GET</c>/<c>PATCH {mailbox}/mailboxSettings</c> onto a Graph version group (<c>/v1.0</c> and
    /// <c>/beta</c>). Only <c>automaticRepliesSetting</c> and <c>timeZone</c> exist, per the contract.
    /// </summary>
    public static RouteGroupBuilder MapMailboxSettingsEndpoints(this RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/mailboxSettings", GetAsync);
            scope.MapPatch("/mailboxSettings", UpdateAsync);
        }
        return graph;
    }

    private static string Context(Mailbox mailbox) => $"{mailbox.ContextSegment}/mailboxSettings";

    private static async Task<IResult> GetAsync(Mailbox mailbox, ODataQuery odata, GetMailboxSettings useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, cancellationToken).ConfigureAwait(false),
            settings => GraphResults.Entity(MailboxSettingsMapper.ToDto(settings), Context(mailbox), odata.Query.Select));

    private static async Task<IResult> UpdateAsync(Mailbox mailbox, JsonObject? body, UpdateMailboxSettings useCase, CancellationToken cancellationToken)
    {
        var patch = MailboxSettingsMapper.ReadPatch(body);
        if (!patch.IsSuccess)
        {
            return GraphResults.Error(patch.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(mailbox.Ref, patch.Value, cancellationToken).ConfigureAwait(false),
            settings => GraphResults.Entity(MailboxSettingsMapper.ToDto(settings), Context(mailbox), MailboxSettingsMapper.Patched(patch.Value)));
    }
}
