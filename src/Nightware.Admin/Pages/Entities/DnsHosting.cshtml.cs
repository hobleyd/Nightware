using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities;

/// <summary>
/// An entity's DNS hosting: which DNS host it is connected to, and (for a global administrator) connecting one,
/// replacing its credential or the host, or forgetting it. Each domain's records are published from that domain's
/// DNS page. The host offered first is the entity's own, so replacing a credential never changes the host unasked.
/// </summary>
public sealed class DnsHostingModel(IAdminActor actor, GetEntity getEntity, SetEntityDnsToken setDnsToken, DnsHosts hosts) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    public EntityDetail Entity { get; private set; } = null!;

    /// <summary>The hosts this console can publish to.</summary>
    public IReadOnlyList<DnsProvider> Providers => hosts.Available;

    /// <summary>The hosts whose credential is one token or key, offered in the main form's select.</summary>
    public IReadOnlyList<DnsProvider> TokenProviders => [.. Providers.Where(static p => !HasOwnForm(p))];

    /// <summary>The host selected in the main form: the entity's own when it is one of those, otherwise Cloudflare.</summary>
    public DnsProvider Selected => Entity.Entity.DnsHost is { } host && !HasOwnForm(host) ? host : DnsProvider.Cloudflare;

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    /// <summary>
    /// The credential is a handler argument, never a bound property, so a failed save cannot render it back. A form
    /// without <paramref name="dnsProvider"/> keeps the entity's host. For Azure, <paramref name="dnsToken"/> is the
    /// client secret and the IDs come in fields of their own; for Google it is the JSON key.
    /// </summary>
    public async Task<IActionResult> OnPostDnsTokenAsync(
        string? dnsProvider,
        string? dnsToken,
        string? azureTenant,
        string? azureClient,
        string? azureSubscription,
        string? azureResourceGroup,
        CancellationToken cancellationToken)
    {
        if (!TryGet(await getEntity.ExecuteAsync(Id, cancellationToken), out EntityDetail entity, out IActionResult? failure))
        {
            return failure!;
        }

        DnsProvider provider = entity.Entity.DnsHost ?? DnsProvider.Cloudflare;
        if (!string.IsNullOrWhiteSpace(dnsProvider) && !DnsProviders.TryParse(dnsProvider, out provider))
        {
            return await UnconfirmedAsync("Choose a DNS host from the list.", cancellationToken);
        }

        if (provider == DnsProvider.AzureDns && !string.IsNullOrWhiteSpace(dnsToken))
        {
            dnsToken = string.Join(':', azureTenant?.Trim(), azureClient?.Trim(), azureSubscription?.Trim(), azureResourceGroup?.Trim(), dnsToken.Trim());
        }

        return string.IsNullOrWhiteSpace(dnsToken)
            ? await UnconfirmedAsync(provider switch
            {
                DnsProvider.Route53 => "Enter the AWS access key as KEYID:SECRET.",
                DnsProvider.AzureDns => "Enter the service principal's client secret.",
                DnsProvider.GoogleCloud => "Paste the service account's JSON key.",
                _ => $"Paste the {provider.Name()} API token.",
            }, cancellationToken)
            : await AfterAsync(await setDnsToken.ExecuteAsync(Id, provider, dnsToken, cancellationToken), reach => DnsTokenNotice(provider, reach), cancellationToken);
    }

    public async Task<IActionResult> OnPostRemoveDnsTokenAsync(bool confirm, CancellationToken cancellationToken)
    {
        if (!TryGet(await getEntity.ExecuteAsync(Id, cancellationToken), out EntityDetail entity, out IActionResult? failure))
        {
            return failure!;
        }

        string name = (entity.Entity.DnsHost ?? DnsProvider.Cloudflare).Name();
        return !confirm
            ? await UnconfirmedAsync($"Tick the box to confirm disconnecting {name}.", cancellationToken)
            : await AfterAsync(
                await setDnsToken.ExecuteAsync(Id, entity.Entity.DnsHost ?? DnsProvider.Cloudflare, null, cancellationToken),
                _ => $"{name} disconnected. The records already published stay there.",
                cancellationToken);
    }

    internal static string DnsTokenNotice(DnsProvider provider, DnsTokenReach reach)
    {
        string reached = string.Join(", ", reach.Reached.Select(static r => r.Domain.Name == r.Zone.Name ? r.Domain.Name : $"{r.Domain} (zone {r.Zone.Name})"));
        string notice = $"Connected to {provider.Name()}. The credential reaches {reached}; publish each domain's records from its DNS page.";
        return reach.Unreached.Count == 0 ? notice : $"{notice} It does not reach {string.Join(", ", reach.Unreached)}: publish those by hand or widen the credential.";
    }

    /// <summary>How to create a credential the console can use at <paramref name="provider"/>.</summary>
    public static string Hint(DnsProvider provider) => provider switch
    {
        DnsProvider.Cloudflare => "My Profile → API Tokens: create a token from the \"Edit zone DNS\" template, limited to this entity's zones.",
        DnsProvider.DigitalOcean => "API → Tokens: a personal access token with the domain scopes (read, create, update, delete).",
        DnsProvider.Gandi => "Organization → Personal Access Tokens: a token that may manage the domains' technical configuration (LiveDNS).",
        DnsProvider.Hetzner => "Hetzner Console → the project holding the zones → Security → API Tokens: a Read & Write token.",
        DnsProvider.Route53 => "IAM: an access key for a user allowed route53:ListHostedZonesByName, ListResourceRecordSets and ChangeResourceRecordSets; enter it as KEYID:SECRET.",
        DnsProvider.AzureDns => "Microsoft Entra ID → App registrations: an application with a client secret, given the DNS Zone Contributor role on the domains' zones. Azure DNS here hosts no TLSA records.",
        DnsProvider.GoogleCloud => "IAM → Service accounts: an account with the DNS Administrator role in the zones' project, and a JSON key for it (Keys → Add key).",
        _ => string.Empty,
    };

    /// <summary>Azure's five values and Google's key are entered in a form of their own.</summary>
    public static bool HasOwnForm(DnsProvider provider) => provider is DnsProvider.AzureDns or DnsProvider.GoogleCloud;

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
