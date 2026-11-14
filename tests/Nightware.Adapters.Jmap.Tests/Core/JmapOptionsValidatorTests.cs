using Nightware.Adapters.Jmap.Core;

namespace Nightware.Adapters.Jmap.Tests.Core;

public sealed class JmapOptionsValidatorTests
{
    private static JmapOptions Valid() => new() { SessionUrl = new Uri("http://stalwart:8080/jmap/session") };

    [Fact]
    public void Defaults_with_a_session_url_are_valid()
    {
        new JmapOptionsValidator().Validate(null, Valid()).Succeeded.ShouldBeTrue();
    }

    public static TheoryData<string, Action<JmapOptions>> Invalid => new()
    {
        { "SessionUrl is required", o => o.SessionUrl = null },
        { "absolute http(s)", o => o.SessionUrl = new Uri("ftp://x/jmap") },
        { "absolute http(s)", o => o.SessionUrl = new Uri("/jmap", UriKind.Relative) },
        { "SessionCacheDuration", o => o.SessionCacheDuration = TimeSpan.Zero },
        { "RequestTimeout", o => o.RequestTimeout = TimeSpan.FromSeconds(-1) },
        { "AttemptTimeout must", o => o.AttemptTimeout = TimeSpan.Zero },
        { "BlobTimeout", o => o.BlobTimeout = TimeSpan.Zero },
        { "cannot exceed", o => o.AttemptTimeout = TimeSpan.FromMinutes(5) },
        { "MaxRetryAttempts", o => o.MaxRetryAttempts = -1 },
        { "MaxRetryAttempts", o => o.MaxRetryAttempts = 11 },
        { "MaxObjectsInGetFallback", o => o.MaxObjectsInGetFallback = 0 },
        { "MaxCallsInRequestFallback", o => o.MaxCallsInRequestFallback = 0 },
        { "MasterUserSeparator", o => o.MasterUserSeparator = "" },
        { "MasterUserSeparator", o => o.MasterUserSeparator = ":" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_settings_are_reported(string expectedMessage, Action<JmapOptions> mutate)
    {
        var options = Valid();
        mutate(options);

        var result = new JmapOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(expectedMessage);
    }
}
