using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Status;
using Nightware.Application.Administration.Users;
using Nightware.Admin.Pages.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages;

/// <summary>
/// Dashboard: what needs the admin, backend health, and the actor's entities. Each worklist row carries its
/// fix where the fix is safe to repeat (finish an offboarding, repair a user or a domain); a conflict needs a
/// person to decide, so it links to the user instead.
/// </summary>
public sealed class IndexModel(
    IAdminActor actor,
    GetAttention attention,
    GetSystemStatus status,
    ListEntities entities,
    GetUser getUser,
    OffboardUser offboard,
    RepairUser repair,
    AddEntityDomain repairDomain,
    AdministrationOptions options,
    Microsoft.Extensions.Options.IOptions<Nightware.Admin.Auth.AdminAuthOptions> console) : ConsolePageModel(actor)
{
    /// <summary>
    /// The system's own admin console, when one is configured and the actor is a global administrator: the way
    /// out when the console itself cannot do something. Null for entity administrators, who hold no rights there.
    /// </summary>
    public string? AdminConsoleFor(BackendKind backend) =>
        !Actor.IsGlobal
            ? null
            : backend switch
            {
                BackendKind.Identity => console.Value.KeycloakAdminUrl,
                BackendKind.Mail => console.Value.StalwartAdminUrl,
                BackendKind.Groupware => console.Value.NextcloudAdminUrl,
                _ => null,
            } is { } url && !string.IsNullOrWhiteSpace(url) ? url : null;

    public Attention Attention { get; private set; } = new([], [], [], new Dictionary<string, int>());

    public IReadOnlyList<BackendHealth> Health { get; private set; } = [];

    public IReadOnlyList<Entity> Entities { get; private set; } = [];

    /// <summary>
    /// The users and domains a backend failed to answer for in the sweep, though its health check passed:
    /// the two disagree, and the dashboard says so instead of showing a plain "OK".
    /// </summary>
    public IReadOnlyList<string> FailedFor(BackendKind backend) =>
        [
            .. Attention.Users.Where(a => a.User.Links.Any(l => l.Backend == backend && l.State == LinkState.Error)).Select(a => a.User.User.Identity.DisplayName),
            .. Attention.Domains.Where(d => backend == BackendKind.Mail && d.Domain.Mail.State == LinkState.Error).Select(d => d.Domain.Domain.Name),
        ];

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    /// <summary>
    /// Re-runs an offboarding that did not reach every system. Only for a user who is already offboarded:
    /// a stale dashboard must never offboard an active user in one click.
    /// </summary>
    public async Task<IActionResult> OnPostFinishOffboardingAsync(string userId, CancellationToken cancellationToken)
    {
        Result<UserDetail> user = await getUser.ExecuteAsync(userId, cancellationToken);
        if (user.IsSuccess && user.Value.User.Identity.Enabled)
        {
            ModelState.AddModelError(string.Empty, $"{user.Value.User.Identity.DisplayName} is not offboarded; nothing was changed. Offboard them from their own page.");
            return await RenderAsync(cancellationToken);
        }

        Result<UserDetail> finished = user.IsSuccess ? await offboard.ExecuteAsync(userId, cancellationToken) : user;
        return await AfterAsync(finished, u => OffboardingNotice.For(u, options.AccessTokenLifespan), cancellationToken);
    }

    public async Task<IActionResult> OnPostRepairUserAsync(string userId, CancellationToken cancellationToken) =>
        await AfterAsync(await repair.ExecuteAsync(userId, cancellationToken), u => $"Repair finished for {u.User.Identity.DisplayName}.", cancellationToken);

    public async Task<IActionResult> OnPostRepairDomainAsync(string entityId, string domain, CancellationToken cancellationToken) =>
        await AfterAsync(await repairDomain.ExecuteAsync(entityId, domain, cancellationToken), Systems.DomainRepairNotice, cancellationToken);

    private async Task<IActionResult> AfterAsync<T>(Result<T> result, Func<T, string> notice, CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            Notice = notice(result.Value);
            return RedirectToPage();
        }

        return Fail(result.Error) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await status.ExecuteAsync(cancellationToken), out IReadOnlyList<BackendHealth> health, out IActionResult? failure)
            || !TryGet(await entities.ExecuteAsync(cancellationToken), out IReadOnlyList<Entity> list, out failure)
            || !TryGet(await attention.ExecuteAsync(cancellationToken), out Attention work, out failure))
        {
            return failure!;
        }

        Attention = work;
        Health = health;
        Entities = list;
        return Page();
    }
}
