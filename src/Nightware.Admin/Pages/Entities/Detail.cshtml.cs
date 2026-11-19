using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities;

/// <summary>
/// One entity: its domains (with repair and removal), who receives each domain's mail to unknown addresses, and a
/// way to its users. Its DNS hosting and administrators are pages of their own.
/// </summary>
public sealed class DetailModel(
    IAdminActor actor,
    GetEntity getEntity,
    ListUsers listUsers,
    AddEntityDomain addDomain,
    RemoveEntityDomain removeDomain,
    RemoveDomainDnsRecords removeDnsRecords,
    SetDomainCatchAll setCatchAll) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    public EntityDetail Entity { get; private set; } = null!;

    public IReadOnlyList<UserDetail> Members { get; private set; } = [];

    /// <summary>How many of the entity's addresses (primary and aliases) are on the domain; such a domain cannot be removed.</summary>
    public int AddressesOn(MailDomain domain) =>
        Members.Sum(m => m.User.Identity.Aliases.Prepend(m.User.Identity.Email).Count(a => a.EndsWith("@" + domain.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The users who may receive a domain's mail to unknown addresses: enabled, with a mailbox of their own.</summary>
    public IReadOnlyList<UserDetail> CatchAllCandidates =>
        [.. Members.Where(static m => m.User.Identity.Enabled && m.For(BackendKind.Mail).State == LinkState.Linked).OrderBy(static m => m.User.Identity.DisplayName, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>The user the entity records as receiving <paramref name="domain"/>'s mail to unknown addresses, when they are one of its users.</summary>
    public UserDetail? CatchAllHolder(MailDomain domain) =>
        Entity.Entity.CatchAllFor(domain) is { } userId ? Members.FirstOrDefault(m => m.User.Identity.Id == userId) : null;

    /// <summary>What is wrong with a served domain's catch-all, or null: the record and the mail server disagree, or its user cannot read it.</summary>
    public string? CatchAllProblem(DomainStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        string? recorded = Entity.Entity.CatchAllFor(status.Domain);
        string? server = status.CatchAllAddress;
        if (recorded is null)
        {
            return server is null ? null : $"The mail server sends it to {server}, set outside the console. Choose someone and Save to manage it here.";
        }

        if (CatchAllHolder(status.Domain) is not { } holder)
        {
            return "It goes to a user who is no longer one of this entity's. Choose someone else.";
        }

        IdentityAccount identity = holder.User.Identity;
        if (!string.Equals(server, identity.Email, StringComparison.OrdinalIgnoreCase))
        {
            return server is null
                ? $"The mail server still refuses it rather than sending it to {identity.Email}. Save again."
                : $"The mail server sends it to {server}, not {identity.Email}. Save again.";
        }

        return identity.Enabled ? null : $"{identity.DisplayName} is offboarded, so no one reads this mail. Choose someone else.";
    }

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostAddDomainAsync(string domain, CancellationToken cancellationToken) =>
        await AfterAsync(await addDomain.ExecuteAsync(Id, domain, cancellationToken), s => s.Mail.State == LinkState.Linked
            ? $"{s.Domain} added. Publish its DNS records."
            : $"{s.Domain} added in Keycloak; the mail server does not serve it yet ({s.Mail.Detail}). Try Repair.", cancellationToken);

    public async Task<IActionResult> OnPostCatchAllAsync(string domain, string? userId, CancellationToken cancellationToken) =>
        await AfterAsync(await setCatchAll.ExecuteAsync(Id, domain, userId, cancellationToken), address => address is null
            ? $"Mail to unknown addresses on {domain} is refused."
            : $"Mail to unknown addresses on {domain} now goes to {address}.", cancellationToken);

    public async Task<IActionResult> OnPostRepairDomainAsync(string domain, CancellationToken cancellationToken) =>
        await AfterAsync(await addDomain.ExecuteAsync(Id, domain, cancellationToken), Systems.DomainRepairNotice, cancellationToken);

    /// <summary>
    /// Removes a domain, confirmed in the row's pop-up. With <paramref name="cleanUpDns"/>, the domain's mail records
    /// come out of its zone at the entity's DNS host first (only those matching what the console publishes); if that fails, the
    /// domain stays, so its records can still be found and cleaned up.
    /// </summary>
    public async Task<IActionResult> OnPostRemoveDomainAsync(string domain, bool confirm, bool cleanUpDns, CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            return await UnconfirmedAsync("Confirm removing the domain in its row.", cancellationToken);
        }

        string cleaned = string.Empty;
        if (cleanUpDns)
        {
            Result<DnsCleanUp> cleanUp = await removeDnsRecords.ExecuteAsync(Id, domain, cancellationToken);
            if (!cleanUp.IsSuccess)
            {
                return Fail(cleanUp.Error) ?? await UnconfirmedAsync($"Nothing was removed: the mail records for {domain} could not be cleaned up at the DNS host.", cancellationToken);
            }

            if (cleanUp.Value.Failed.Count > 0)
            {
                ModelState.AddModelError(string.Empty, $"{domain} was not removed: the DNS host refused to delete {cleanUp.Value.Failed.Count} of its mail records ({cleanUp.Value.Failed[0].Error!.Message}). {cleanUp.Value.Removed} were deleted; try again.");
                return await RenderAsync(cancellationToken);
            }

            cleaned = $" {cleanUp.Value.Removed} mail {(cleanUp.Value.Removed == 1 ? "record was" : "records were")} removed from the DNS host"
                + (cleanUp.Value.Left > 0 ? $"; {cleanUp.Value.Left} that differ from what the console publishes were left." : ".");
        }

        return await AfterAsync(await removeDomain.ExecuteAsync(Id, domain, cancellationToken), _ => $"{domain} removed from the entity and the mail server.{cleaned}", cancellationToken);
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
        if (!TryGet(await getEntity.ExecuteAsync(Id, cancellationToken), out EntityDetail entity, out IActionResult? failure)
            || !TryGet(await listUsers.ExecuteAsync(Id, cancellationToken), out IReadOnlyList<UserDetail> members, out failure))
        {
            return failure!;
        }

        Entity = entity;
        Members = members;
        return Page();
    }
}
