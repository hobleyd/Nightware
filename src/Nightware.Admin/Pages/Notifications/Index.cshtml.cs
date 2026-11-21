using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Subscriptions;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Notifications;

/// <summary>Every app subscribed to change notifications on the server (global admins only), with who it acts for.</summary>
public sealed class IndexModel(IAdminActor actor, ListAllSubscriptions list, RemoveSubscription remove) : ConsolePageModel(actor)
{
    public IReadOnlyList<SubscriptionOwner> Subscriptions { get; private set; } = [];

    /// <summary>Set when the console has no subscription store configured.</summary>
    public bool NotConfigured { get; private set; }

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostRemoveAsync(string userId, string subscriptionId, CancellationToken cancellationToken)
    {
        Result removed = await remove.ExecuteAsync(userId, subscriptionId, cancellationToken);
        if (removed.IsSuccess)
        {
            Notice = "The app will no longer be notified; it was told so.";
            return RedirectToPage();
        }

        return Fail(removed.Error!) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<SubscriptionOwner>> all = await list.ExecuteAsync(cancellationToken);
        if (all is { IsSuccess: false, Error.Kind: ErrorKind.Unsupported })
        {
            NotConfigured = true;
            return Page();
        }

        if (!TryGet(all, out IReadOnlyList<SubscriptionOwner> subscriptions, out IActionResult? failure))
        {
            return failure!;
        }

        Subscriptions = subscriptions;
        return Page();
    }
}
