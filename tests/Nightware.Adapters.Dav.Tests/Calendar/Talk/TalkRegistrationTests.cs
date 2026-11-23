using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Calendar.Talk;
using Nightware.Adapters.Dav.Talk;
using Nightware.Application.Calendar.OnlineMeetings;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Calendar.Talk;

/// <summary>The meetings provider through the adapter's real registration, and its options' validation.</summary>
public sealed class TalkRegistrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Provider(params (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?> { ["Dav:BaseUrl"] = "http://nextcloud/" };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(Substitute.For<IBackendCredentials>());
        services.AddDavAdapter(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public async Task Without_a_public_url_no_room_is_ever_opened()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var meetings = scope.ServiceProvider.GetRequiredService<IOnlineMeetings>();

        meetings.ShouldBeOfType<UnconfiguredOnlineMeetings>();
        meetings.IsEnabled.ShouldBeFalse();
        (await meetings.OpenAsync(new OnlineMeetingRequest("x", DateTimeOffset.UnixEpoch, null), Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        (await meetings.KeepAsync("https://x/call/abcd", Ct)).IsSuccess.ShouldBeTrue();
        (await meetings.CloseAsync("https://x/call/abcd", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void With_a_public_url_rooms_are_talk_rooms()
    {
        using var provider = Provider(("Talk:PublicBaseUrl", "https://cloud.example.com/cloud/"));
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IOnlineMeetings>().ShouldBeOfType<NextcloudTalkMeetings>();
    }

    [Theory]
    [InlineData("Talk:PublicBaseUrl", "/cloud/")]
    [InlineData("Talk:PublicBaseUrl", "ftp://cloud.example.com/")]
    [InlineData("Talk:PublicBaseUrl", "https://cloud.example.com/?x=1")]
    [InlineData("Talk:PublicBaseUrl", "https://cloud.example.com/#top")]
    [InlineData("Talk:UntitledRoomName", " ")]
    public void Invalid_options_fail_at_startup(string key, string value)
    {
        using var provider = Provider((key, value));

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<TalkOptions>>().Value);
    }
}
