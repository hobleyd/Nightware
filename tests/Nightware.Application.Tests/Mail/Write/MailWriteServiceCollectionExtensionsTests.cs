using Microsoft.Extensions.DependencyInjection;
using Nightware.Application.Mail;
using Nightware.Application.Mail.Categories;
using Nightware.Application.Mail.Write;

namespace Nightware.Application.Tests.Mail.Write;

public sealed class MailWriteServiceCollectionExtensionsTests
{
    private static readonly Type[] UseCases =
    [
        typeof(UpdateMessage), typeof(CreateDraft), typeof(MoveMessage), typeof(CopyMessage), typeof(DeleteMessage), typeof(PermanentlyDeleteMessage),
        typeof(ImportMessage), typeof(AddDraftAttachment), typeof(RemoveDraftAttachment), typeof(SendDraft), typeof(SendMail),
        typeof(ReplyToMessage), typeof(CreateReplyDraft), typeof(ForwardMessage), typeof(CreateForwardDraft),
        typeof(CreateChildFolder), typeof(RenameFolder), typeof(MoveFolder), typeof(DeleteFolder),
        typeof(ListMasterCategories), typeof(GetMasterCategory), typeof(CreateMasterCategory), typeof(UpdateMasterCategory), typeof(DeleteMasterCategory),
    ];

    // The test project has no DI container, so resolvability is checked structurally: every use case is
    // registered scoped as itself and depends only on Application ports.
    private static readonly Type[] Ports =
    [
        typeof(IMailMessageWriter), typeof(IMailFolderWriter), typeof(IDraftStore), typeof(IMailSender),
        typeof(IMailImporter), typeof(IMailMessageReader), typeof(IMasterCategoryStore),
    ];

    [Fact]
    public void Registers_every_write_use_case_as_scoped_over_ports_only()
    {
        var services = new ServiceCollection().AddMailWriteUseCases();

        services.Count.ShouldBe(UseCases.Length);
        foreach (var useCase in UseCases)
        {
            var descriptor = services.Single(d => d.ServiceType == useCase);
            descriptor.Lifetime.ShouldBe(ServiceLifetime.Scoped);
            descriptor.ImplementationType.ShouldBe(useCase);
            useCase.GetConstructors().Single().GetParameters()
                .ShouldAllBe(p => Ports.Contains(p.ParameterType), $"{useCase.Name} depends on a non-port");
        }
    }

    [Fact]
    public void Every_public_use_case_in_the_namespace_is_registered()
    {
        var registered = new ServiceCollection().AddMailWriteUseCases().Select(d => d.ServiceType).ToHashSet();
        var declared = typeof(UpdateMessage).Assembly.GetExportedTypes()
            .Where(t => t.Namespace == typeof(UpdateMessage).Namespace && t is { IsClass: true, IsSealed: true, IsAbstract: false }
                && t.GetMethod("ExecuteAsync") is not null);

        declared.ShouldAllBe(t => registered.Contains(t));
    }
}
