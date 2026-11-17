using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Pages.Users;

/// <summary>
/// The add-or-link wizard: 1 who, 2 the plan (what will be created and what linked), 3 the result. The
/// plan is recomputed when applied; nothing from step 2's page is trusted.
/// </summary>
public sealed class NewModel(IAdminActor actor, GetEntity getEntity, PlanUserProvisioning plan, ApplyUserProvisioning apply) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Entity { get; set; } = string.Empty;

    [BindProperty]
    public NewUserForm Form { get; set; } = new();

    public EntityDetail Detail { get; private set; } = null!;

    public ProvisioningPlan? Plan { get; private set; }

    public ProvisioningResult? Result { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken) ?? Page();

    public async Task<IActionResult> OnPostPlanAsync(CancellationToken cancellationToken)
    {
        if (await LoadAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var planned = await plan.ExecuteAsync(ToRequest(), cancellationToken);
        if (planned.IsSuccess)
        {
            Plan = planned.Value;
            return Page();
        }

        return Fail(planned.Error) ?? Page();
    }

    public async Task<IActionResult> OnPostApplyAsync(CancellationToken cancellationToken)
    {
        if (await LoadAsync(cancellationToken) is { } failure)
        {
            return failure;
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // The admin confirms the plan they saw; if the details changed since, show the new plan instead of applying it.
        if (!string.Equals(Form.CheckedDetails, CurrentDetails, StringComparison.Ordinal))
        {
            ModelState.AddModelError(string.Empty, "The details changed after you checked them. Here is what will happen now; check it, then create.");
            var fresh = await plan.ExecuteAsync(ToRequest(), cancellationToken);
            Plan = fresh.IsSuccess ? fresh.Value : null;
            return fresh.IsSuccess ? Page() : Fail(fresh.Error) ?? Page();
        }

        var applied = await apply.ExecuteAsync(ToRequest(), Form.Delivery, cancellationToken);
        if (applied.IsSuccess)
        {
            // Rendered, not redirected: a temporary password is shown on this response only (no-store).
            Result = applied.Value;
            return Page();
        }

        if (Fail(applied.Error) is { } outcome)
        {
            return outcome;
        }

        var replanned = await plan.ExecuteAsync(ToRequest(), cancellationToken);
        Plan = replanned.IsSuccess ? replanned.Value : null;
        return Page();
    }

    /// <summary>Every input that changes the plan, as checked: the plan step echoes it, Apply compares it.</summary>
    public string CurrentDetails => Fingerprint(Form.FirstName, Form.LastName, Form.LocalPart?.Trim(), Form.Domain, Form.Username?.Trim(), Form.Aliases?.Trim(), Form.Delivery.ToString());

    /// <summary>
    /// The details as one hex digest. Never the raw text: a browser turns every line break in a submitted value
    /// into CRLF, so a joined value would never compare equal again.
    /// </summary>
    public static string Fingerprint(params string?[] details) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', details))));

    private NewUserRequest ToRequest() =>
        new(Entity, $"{Form.LocalPart.Trim()}@{Form.Domain}", Form.Username, Form.FirstName, Form.LastName,
            (Form.Aliases ?? string.Empty).Split([',', ';', '\n', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await getEntity.ExecuteAsync(Entity, cancellationToken), out EntityDetail detail, out IActionResult? failure))
        {
            return failure;
        }

        Detail = detail;
        if (string.IsNullOrEmpty(Form.Domain) && detail.Entity.Domains.Count > 0)
        {
            Form.Domain = detail.Entity.Domains[0].Name;
        }

        return null;
    }

    public sealed class NewUserForm
    {
        [StringLength(100), Display(Name = "First name")]
        public string? FirstName { get; set; }

        [StringLength(100), Display(Name = "Last name")]
        public string? LastName { get; set; }

        [Required, StringLength(64), Display(Name = "Address")]
        public string LocalPart { get; set; } = string.Empty;

        [Required, StringLength(253)]
        public string Domain { get; set; } = string.Empty;

        [StringLength(64)]
        public string? Username { get; set; }

        [StringLength(2000)]
        public string? Aliases { get; set; }

        public CredentialDelivery Delivery { get; set; } = CredentialDelivery.TemporaryPassword;

        /// <summary>The details the shown plan was made for (see <see cref="CurrentDetails"/>).</summary>
        public string? CheckedDetails { get; set; }
    }
}
