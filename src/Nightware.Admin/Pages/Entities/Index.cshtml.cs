using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Pages.Entities;

/// <summary>The actor's entities; global admins create new ones here.</summary>
public sealed class IndexModel(IAdminActor actor, ListEntities list, CreateEntity create) : ConsolePageModel(actor)
{
    public IReadOnlyList<Entity> Entities { get; private set; } = [];

    [BindProperty]
    public NewEntityForm Form { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await LoadAsync(cancellationToken) ?? Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (ModelState.IsValid)
        {
            var created = await create.ExecuteAsync(Form.Name, Form.Alias, Form.Domain, cancellationToken);
            if (created.IsSuccess)
            {
                DomainStatus domain = created.Value.Domains[0];
                Notice = domain.Mail.State == LinkState.Linked
                    ? $"{created.Value.Entity.Name} created. Publish the DNS records for {domain.Domain} next."
                    : $"{created.Value.Entity.Name} created, but the mail server does not serve {domain.Domain} yet: {domain.Mail.Detail} Use Repair on the entity page.";
                return RedirectToPage("/Entities/Detail", new { id = created.Value.Entity.Id });
            }

            if (Fail(created.Error) is { } failure)
            {
                return failure;
            }
        }

        return await LoadAsync(cancellationToken) ?? Page();
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await list.ExecuteAsync(cancellationToken), out IReadOnlyList<Entity> entities, out IActionResult? failure))
        {
            return failure;
        }

        Entities = entities;
        return null;
    }

    public sealed class NewEntityForm
    {
        [Required, StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [Required, StringLength(40), Display(Name = "Short name")]
        public string Alias { get; set; } = string.Empty;

        [Required, StringLength(253), Display(Name = "Mail domain")]
        public string Domain { get; set; } = string.Empty;
    }
}
