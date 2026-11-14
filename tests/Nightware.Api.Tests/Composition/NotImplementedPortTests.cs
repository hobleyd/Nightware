using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Composition;
using Nightware.Api.Tests.Support;
using Nightware.Application.Calendar;
using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Composition;

public sealed class NotImplementedPortTests
{
    public interface IValueTaskPort
    {
        ValueTask<Result<int>> GetAsync();

        ValueTask<int> CountAsync();
    }

    [Fact]
    public async Task Task_of_result_of_t_is_an_unsupported_failure()
    {
        var reader = NotImplementedPort<IMailFolderReader>.Create();

        var result = await reader.ListAsync(new MailboxRef("a@b.c"), null, ItemQuery.Default, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(ErrorKind.Unsupported);
        result.Error.Message.ShouldBe("IMailFolderReader.ListAsync is not implemented yet.");
    }

    [Fact]
    public async Task Task_of_result_is_an_unsupported_failure()
    {
        var writer = NotImplementedPort<IMailMessageWriter>.Create();

        var result = await writer.SoftDeleteAsync(new MailboxRef("a@b.c"), new MessageId("m"), CancellationToken.None);

        result.Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }

    [Fact]
    public void Synchronous_results_fail_and_other_returns_throw()
    {
        var codec = NotImplementedPort<IICalendarCodec>.Create();

        codec.Parse("BEGIN:VCALENDAR").Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        Should.Throw<NotSupportedException>(() => codec.Serialize(new Nightware.Domain.Calendar.CalendarDocument(null, [])));
    }

    [Fact]
    public async Task Value_tasks_are_supported()
    {
        var port = NotImplementedPort<IValueTaskPort>.Create();

        (await port.GetAsync()).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        await Should.ThrowAsync<NotSupportedException>(async () => await port.CountAsync());
    }

    [Fact]
    public void Only_interfaces_can_be_stubbed()
    {
        Should.Throw<InvalidOperationException>(() => NotImplementedPort<object>.Create());
    }

    [Fact]
    public void Port_interfaces_cover_every_application_interface()
    {
        PortFallbacks.PortInterfaces.ShouldContain(typeof(IMailMessageReader));
        PortFallbacks.PortInterfaces.ShouldContain(typeof(IUserContext));
        PortFallbacks.PortInterfaces.ShouldAllBe(t => t.IsInterface && t.Namespace!.StartsWith("Nightware.Application", StringComparison.Ordinal));
    }

    [Fact]
    public void Real_registrations_win_over_fallbacks()
    {
        var real = Substitute.For<IMailFolderReader>();
        var services = new ServiceCollection().AddSingleton(real).AddNotImplementedPortFallbacks();

        services.BuildServiceProvider().GetRequiredService<IMailFolderReader>().ShouldBeSameAs(real);
    }

    [Fact]
    public async Task Every_port_resolves_in_the_composed_host()
    {
        await using var host = await TestHost.StartAsync();
        using var scope = host.App.Services.CreateScope();

        foreach (var port in PortFallbacks.PortInterfaces)
        {
            scope.ServiceProvider.GetService(port).ShouldNotBeNull($"{port.Name} does not resolve");
        }

        scope.ServiceProvider.GetRequiredService<IUserContext>().ShouldBeOfType<Nightware.Api.Auth.HttpUserContext>();
        scope.ServiceProvider.GetRequiredService<IMailboxResolver>().ShouldBeOfType<Nightware.Api.Graph.Routing.MailboxResolver>();
    }
}
