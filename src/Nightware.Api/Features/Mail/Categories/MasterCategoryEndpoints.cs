using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Mail.Categories;
using Nightware.Domain.Mail;

namespace Nightware.Api.Features.Mail.Categories;

/// <summary>Graph <c>outlookCategory</c>.</summary>
public sealed record OutlookCategoryDto
{
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    public string? Color { get; init; }

    public static OutlookCategoryDto From(MasterCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);
        return new OutlookCategoryDto { Id = category.Id, DisplayName = category.DisplayName, Color = category.Color };
    }
}

/// <summary>The mailbox's category list, <c>outlook/masterCategories</c> (ADR 0006 D5, phase 12).</summary>
internal static class MasterCategoryEndpoints
{
    public static void Map(IEndpointRouteBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/outlook/masterCategories", ListAsync);
            scope.MapPost("/outlook/masterCategories", CreateAsync);
            scope.MapGet("/outlook/masterCategories/{categoryId}", GetAsync);
            scope.MapPatch("/outlook/masterCategories/{categoryId}", UpdateAsync);
            scope.MapDelete("/outlook/masterCategories/{categoryId}", DeleteAsync);
        }
    }

    private static string Context(Mailbox mailbox) => $"{mailbox.ContextSegment}/outlook/masterCategories";

    private static async Task<IResult> ListAsync(Mailbox mailbox, ODataQuery odata, ListMasterCategories useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, cancellationToken),
            list => GraphResults.Collection(list.Select(OutlookCategoryDto.From), Context(mailbox), odata.Query));

    private static async Task<IResult> GetAsync(Mailbox mailbox, string categoryId, ODataQuery odata, GetMasterCategory useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, categoryId, cancellationToken),
            c => GraphResults.Entity(OutlookCategoryDto.From(c), Context(mailbox), odata.Query.Select));

    private static async Task<IResult> CreateAsync(Mailbox mailbox, [FromBody] OutlookCategoryDto? body, CreateMasterCategory useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, body?.DisplayName, body?.Color, cancellationToken),
            c => GraphResults.CreatedAt(OutlookCategoryDto.From(c), Context(mailbox), $"{mailbox.ContextSegment}/MasterCategories('{c.Id}')"));

    private static async Task<IResult> UpdateAsync(
        Mailbox mailbox, string categoryId, ODataQuery odata, [FromBody] OutlookCategoryDto? body, UpdateMasterCategory useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, categoryId, body?.DisplayName, body?.Color, cancellationToken),
            c => GraphResults.Entity(OutlookCategoryDto.From(c), Context(mailbox), odata.Query.Select));

    private static async Task<IResult> DeleteAsync(Mailbox mailbox, string categoryId, DeleteMasterCategory useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, categoryId, cancellationToken));
}
