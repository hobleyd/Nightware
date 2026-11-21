using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.AutoReplies;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Admin.Pages.Users;

/// <summary>A user's automatic replies, set by an administrator (their out-of-office while they are away).</summary>
public sealed class AutoReplyModel(IAdminActor actor, GetUser getUser, MailboxAutoReplies replies) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty]
    public AutoReplyForm Form { get; set; } = new();

    public string Name { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await NameAsync(cancellationToken))
        {
            return Unread is null ? NotFound() : Page();
        }

        if (!TryGet(await replies.GetForUserAsync(Id, cancellationToken), out AutoReplySetting setting, out IActionResult? failure))
        {
            return failure!;
        }

        Form = AutoReplyForm.From(setting);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        await NameAsync(cancellationToken);
        var (setting, error) = Form.ToSetting();
        if (setting is null)
        {
            ModelState.AddModelError(string.Empty, error!);
            return Page();
        }

        Result<AutoReplySetting> saved = await replies.SetForUserAsync(Id, setting, cancellationToken);
        if (saved.IsSuccess)
        {
            Notice = "Automatic replies saved.";
            return RedirectToPage("/Users/Detail", new { id = Id });
        }

        return Fail(saved.Error) ?? Page();
    }

    private async Task<bool> NameAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await getUser.ExecuteAsync(Id, cancellationToken), out var user, out _))
        {
            return false;
        }

        Name = user.User.Identity.DisplayName;
        return true;
    }
}
