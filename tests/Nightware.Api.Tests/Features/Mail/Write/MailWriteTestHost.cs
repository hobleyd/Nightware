using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Tests.Support;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Mail.Write;

/// <summary>The real composition with every mail port replaced by a substitute.</summary>
public abstract class MailWriteEndpointTestBase : IAsyncLifetime
{
    public const string SharedAddress = "shared@example.com";

    private TestHost _host = null!;

    protected IMailMessageWriter Writer { get; } = Substitute.For<IMailMessageWriter>();

    protected IMailFolderWriter Folders { get; } = Substitute.For<IMailFolderWriter>();

    protected IDraftStore Drafts { get; } = Substitute.For<IDraftStore>();

    protected IMailSender Sender { get; } = Substitute.For<IMailSender>();

    protected IMailImporter Importer { get; } = Substitute.For<IMailImporter>();

    protected IMailMessageReader Reader { get; } = Substitute.For<IMailMessageReader>();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Both mailbox forms: the route prefix and the mailbox it resolves to.</summary>
    public static TheoryData<string, string> Forms => new()
    {
        { "/v1.0/me", TestHost.UserEmail },
        { $"/v1.0/users/{SharedAddress}", SharedAddress },
    };

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services
            .AddSingleton(Writer)
            .AddSingleton(Folders)
            .AddSingleton(Drafts)
            .AddSingleton(Sender)
            .AddSingleton(Importer)
            .AddSingleton(Reader));

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected static MailboxRef Box(string address) => new(address);

    protected static string Context(string address) => $"users('{Uri.EscapeDataString(address)}')";

    protected Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        }

        return _host.AuthorizedClient().SendAsync(request, Ct);
    }
}
