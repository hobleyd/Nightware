using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Mail;

namespace Nightware.Api.Features.Mail;

/// <summary>Graph <c>POST translateExchangeIds</c> body.</summary>
public sealed record TranslateExchangeIdsRequestDto
{
    public IReadOnlyList<string>? InputIds { get; init; }

    public string? SourceIdType { get; init; }

    public string? TargetIdType { get; init; }
}

/// <summary>Graph <c>convertIdResult</c>.</summary>
public sealed record ConvertIdResultDto(string SourceId, string TargetId);

/// <summary>Phase 19: <c>POST translateExchangeIds</c> (see <see cref="TranslateExchangeIds"/>).</summary>
internal static class IdTranslationEndpoints
{
    public static void Map(IEndpointRouteBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapPost("/translateExchangeIds", TranslateAsync);
        }
    }

    private static async Task<IResult> TranslateAsync(
        Mailbox mailbox, [FromBody] TranslateExchangeIdsRequestDto? body, TranslateExchangeIds useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(body?.InputIds, body?.SourceIdType, body?.TargetIdType, cancellationToken).ConfigureAwait(false),
            ids => GraphResults.Collection(ids.Select(static i => new ConvertIdResultDto(i.SourceId, i.TargetId)), "Collection(microsoft.graph.convertIdResult)",
                Nightware.Application.Common.Query.ItemQuery.Default));
}
