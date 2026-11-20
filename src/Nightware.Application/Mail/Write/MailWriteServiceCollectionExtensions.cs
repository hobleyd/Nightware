using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Mail.Write;

public static class MailWriteServiceCollectionExtensions
{
    /// <summary>Registers the mail write use cases. Owned by Phase 3.</summary>
    public static IServiceCollection AddMailWriteUseCases(this IServiceCollection services) =>
        services
            .AddScoped<UpdateMessage>()
            .AddScoped<CreateDraft>()
            .AddScoped<MoveMessage>()
            .AddScoped<CopyMessage>()
            .AddScoped<BeginDraftAttachmentUpload>()
            .AddScoped<UploadDraftAttachmentChunk>()
            .AddScoped<DeleteMessage>()
            .AddScoped<PermanentlyDeleteMessage>()
            .AddScoped<ImportMessage>()
            .AddScoped<AddDraftAttachment>()
            .AddScoped<RemoveDraftAttachment>()
            .AddScoped<SendDraft>()
            .AddScoped<SendMail>()
            .AddScoped<ReplyToMessage>()
            .AddScoped<CreateReplyDraft>()
            .AddScoped<ForwardMessage>()
            .AddScoped<CreateForwardDraft>()
            .AddScoped<CreateChildFolder>()
            .AddScoped<RenameFolder>()
            .AddScoped<MoveFolder>()
            .AddScoped<DeleteFolder>()
            .AddScoped<Categories.ListMasterCategories>()
            .AddScoped<Categories.GetMasterCategory>()
            .AddScoped<Categories.CreateMasterCategory>()
            .AddScoped<Categories.UpdateMasterCategory>()
            .AddScoped<Categories.DeleteMasterCategory>()
            .AddScoped<Rules.ListMessageRules>()
            .AddScoped<Rules.GetMessageRule>()
            .AddScoped<Rules.CreateMessageRule>()
            .AddScoped<Rules.UpdateMessageRule>()
            .AddScoped<Rules.DeleteMessageRule>();
}
