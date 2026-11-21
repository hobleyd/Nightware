using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.AutoReplies;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Admin.Pages.Entities.Mailboxes;

/// <summary>A shared mailbox's automatic replies ("We'll answer within a day").</summary>
public sealed class AutoReplyModel(IAdminActor actor, GetSharedMailbox getMailbox, MailboxAutoReplies replies) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string MailboxId { get; set; } = string.Empty;

    [BindProperty]
    public AutoReplyForm Form { get; set; } = new();

    public string Name { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!await NameAsync(cancellationToken))
        {
            return Unread is null ? NotFound() : Page();
        }

        if (!TryGet(await replies.GetForMailboxAsync(Id, MailboxId, cancellationToken), out AutoReplySetting setting, out IActionResult? failure))
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

        Result<AutoReplySetting> saved = await replies.SetForMailboxAsync(Id, MailboxId, setting, cancellationToken);
        if (saved.IsSuccess)
        {
            Notice = "Automatic replies saved.";
            return RedirectToPage("/Entities/Mailboxes/Detail", new { id = Id, mailboxId = MailboxId });
        }

        return Fail(saved.Error) ?? Page();
    }

    private async Task<bool> NameAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await getMailbox.ExecuteAsync(Id, MailboxId, cancellationToken), out var detail, out _))
        {
            return false;
        }

        Name = detail.Mailbox.Name;
        return true;
    }
}
