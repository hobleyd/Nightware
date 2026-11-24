using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Application.Administration.Status;
using Nightware.Application.Administration.Users;
using Nightware.Admin.Hosting;
using Nightware.Admin.Pages.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages;

/// <summary>
/// Dashboard: what needs the admin, backend health, and the actor's entities. Each worklist row carries its
/// fix where the fix is safe to repeat (finish an offboarding, repair a user, a domain or a shared mailbox); a conflict needs a
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
    VerifyUserEmail verifyEmail,
    AddEntityDomain repairDomain,
    RepairSharedMailbox repairMailbox,
    AdministrationOptions options,
    IGrafanaStatus grafana,
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
                BackendKind.Mail => StalwartSignIn(console.Value.StalwartAdminUrl),
                BackendKind.Groupware => console.Value.NextcloudAdminUrl,
                _ => null,
            } is { } url && !string.IsNullOrWhiteSpace(url) ? url : null;

    /// <summary>
    /// Stalwart's web admin decides where an account signs in from the address typed on its sign-in page (a bare
    /// username stays on its own password page). Its sign-in page, patched (deploy/stalwart/webui), takes the
    /// admin's address as <c>login_hint</c> and goes straight on, so the Keycloak session signs them in.
    /// </summary>
    private string? StalwartSignIn(string? webAdmin) =>
        string.IsNullOrWhiteSpace(webAdmin) || User.FindFirst("email")?.Value is not { Length: > 0 } email
            ? webAdmin
            : $"{webAdmin.TrimEnd('/')}/login?login_hint={Uri.EscapeDataString(email)}";

    /// <summary>Grafana, the stack's telemetry, for a global administrator when it is configured; null otherwise.</summary>
    public string? GrafanaUrl =>
        Actor.IsGlobal && console.Value.GrafanaUrl is { } url && !string.IsNullOrWhiteSpace(url) ? url : null;

    /// <summary>Grafana's health, checked only when <see cref="GrafanaUrl"/> is shown; null otherwise.</summary>
    public GrafanaHealth? Grafana { get; private set; }

    public Attention Attention { get; private set; } = new([], [], [], [], new Dictionary<string, int>());

    public IReadOnlyList<BackendHealth> Health { get; private set; } = [];

    public IReadOnlyList<Entity> Entities { get; private set; } = [];

    /// <summary>
    /// The users, domains and shared mailboxes a backend failed to answer for in the sweep, though its health check passed:
    /// the two disagree, and the dashboard says so instead of showing a plain "OK".
    /// </summary>
    public IReadOnlyList<string> FailedFor(BackendKind backend) =>
        [
            .. Attention.Users.Where(a => a.User.Links.Any(l => l.Backend == backend && l.State == LinkState.Error)).Select(a => a.User.User.Identity.DisplayName),
            .. Attention.Domains.Where(d => backend == BackendKind.Mail && d.Domain.Mail.State == LinkState.Error).Select(d => d.Domain.Domain.Name),
            .. Attention.Mailboxes.Where(m => m.Mailbox.Links.Any(l => l.Backend == backend && l.State == LinkState.Error)).Select(m => m.Mailbox.Mailbox.Name),
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

    public async Task<IActionResult> OnPostVerifyEmailAsync(string userId, CancellationToken cancellationToken) =>
        await AfterAsync(await verifyEmail.ExecuteAsync(userId, cancellationToken), u => $"{u.User.Identity.DisplayName}'s email is marked verified.", cancellationToken);

    public async Task<IActionResult> OnPostRepairDomainAsync(string entityId, string domain, CancellationToken cancellationToken) =>
        await AfterAsync(await repairDomain.ExecuteAsync(entityId, domain, cancellationToken), Systems.DomainRepairNotice, cancellationToken);

    public async Task<IActionResult> OnPostRepairMailboxAsync(string entityId, string mailboxId, CancellationToken cancellationToken) =>
        await AfterAsync(
            await repairMailbox.ExecuteAsync(entityId, mailboxId, cancellationToken),
            o => string.Join(" ", o.Notes.Prepend($"{o.Mailbox.Name} was brought into line.")),
            cancellationToken);

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
        Grafana = GrafanaUrl is null ? null : await grafana.CheckAsync(cancellationToken);
        return Page();
    }
}
