using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities;

/// <summary>The DNS records a domain needs, as the mail server generated them.</summary>
public sealed class DnsModel(IAdminActor actor, GetEntityDnsRecords dns, ListEntities entities) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string Domain { get; set; } = string.Empty;

    public IReadOnlyList<DnsRecord> Records { get; private set; } = [];

    /// <summary>The entity's name, for the breadcrumb; null when it cannot be read.</summary>
    public string? EntityName { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await dns.ExecuteAsync(Id, Domain, cancellationToken), out IReadOnlyList<DnsRecord> records, out IActionResult? failure))
        {
            return failure!;
        }

        Records = records;
        Result<IReadOnlyList<Entity>> manageable = await entities.ExecuteAsync(cancellationToken);
        EntityName = manageable.IsSuccess ? manageable.Value.FirstOrDefault(e => e.Id == Id)?.Name : null;
        return Page();
    }
}
