using Nightware.Application.Administration.Groups;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Pages.Entities.Groups;

/// <summary>The group form as posted: lists are comma- or line-separated text, as on the new-user wizard.</summary>
public sealed class GroupInput
{
    public string Name { get; set; } = string.Empty;

    public string? Key { get; set; }

    public bool Mail { get; set; }

    public string? LocalPart { get; set; }

    public string? Domain { get; set; }

    public string? Aliases { get; set; }

    public SenderPolicy Senders { get; set; } = SenderPolicy.Members;

    public string? ExternalRecipients { get; set; }

    public bool Files { get; set; }

    public bool TeamFolder { get; set; }

    public GroupForm ToForm() => new(Name)
    {
        Key = Key,
        Mail = Mail,
        LocalPart = LocalPart,
        Domain = Domain,
        Aliases = Split(Aliases),
        Senders = Senders,
        ExternalRecipients = Split(ExternalRecipients),
        Files = Files,
        TeamFolder = Files && TeamFolder,
    };

    /// <summary>The form filled with what <paramref name="group"/> is now.</summary>
    public static GroupInput From(Group group)
    {
        ArgumentNullException.ThrowIfNull(group);
        return new GroupInput
        {
            Name = group.Name,
            Key = group.Key.Value,
            Mail = group.Mail is not null,
            LocalPart = group.Mail?.Address.LocalPart,
            Domain = group.Mail?.Address.Domain.Name,
            Aliases = group.Mail is { } mail ? string.Join(", ", mail.Aliases.Select(static a => a.Value)) : null,
            Senders = group.Mail?.Senders ?? SenderPolicy.Members,
            ExternalRecipients = group.Mail is { } m ? string.Join(", ", m.ExternalRecipients.Select(static r => r.Value)) : null,
            Files = group.Files is not null,
            TeamFolder = group.Files?.TeamFolder ?? false,
        };
    }

    private static string[] Split(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
