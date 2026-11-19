using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities;

/// <summary>
/// An entity's DNS hosting: whether it is connected to Cloudflare, and (for a global administrator) connecting,
/// replacing or forgetting its token. Each domain's records are published from that domain's DNS page.
/// </summary>
public sealed class DnsHostingModel(IAdminActor actor, GetEntity getEntity, SetEntityDnsToken setDnsToken) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    public EntityDetail Entity { get; private set; } = null!;

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    /// <summary>The token is a handler argument, never a bound property, so a failed save cannot render it back.</summary>
    public async Task<IActionResult> OnPostDnsTokenAsync(string? dnsToken, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(dnsToken)
            ? await UnconfirmedAsync("Paste the Cloudflare API token.", cancellationToken)
            : await AfterAsync(await setDnsToken.ExecuteAsync(Id, dnsToken, cancellationToken), DnsTokenNotice, cancellationToken);

    public async Task<IActionResult> OnPostRemoveDnsTokenAsync(bool confirm, CancellationToken cancellationToken) =>
        !confirm
            ? await UnconfirmedAsync("Tick the box to confirm disconnecting Cloudflare.", cancellationToken)
            : await AfterAsync(await setDnsToken.ExecuteAsync(Id, null, cancellationToken), _ => "Cloudflare disconnected. The records already published stay in Cloudflare.", cancellationToken);

    internal static string DnsTokenNotice(DnsTokenReach reach)
    {
        string reached = string.Join(", ", reach.Reached.Select(static r => r.Domain.Name == r.Zone.Name ? r.Domain.Name : $"{r.Domain} (zone {r.Zone.Name})"));
        string notice = $"Cloudflare token saved. It reaches {reached}; publish each domain's records from its DNS page.";
        return reach.Unreached.Count == 0 ? notice : $"{notice} It does not reach {string.Join(", ", reach.Unreached)}: publish those by hand or widen the token.";
    }

    private async Task<IActionResult> UnconfirmedAsync(string message, CancellationToken cancellationToken)
    {
        ModelState.AddModelError(string.Empty, message);
        return await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> AfterAsync<T>(Result<T> result, Func<T, string> notice, CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            Notice = notice(result.Value);
            return RedirectToPage(new { id = Id });
        }

        return Fail(result.Error) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await getEntity.ExecuteAsync(Id, cancellationToken), out EntityDetail entity, out IActionResult? failure))
        {
            return failure!;
        }

        Entity = entity;
        return Page();
    }
}
