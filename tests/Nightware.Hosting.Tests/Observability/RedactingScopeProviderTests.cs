using System.Diagnostics;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Nightware.Hosting.Observability;

namespace Nightware.Hosting.Tests.Observability;

public sealed class RedactingScopeProviderTests
{
    private const string SecretPath = "/v1.0/shares/u!c2VjcmV0/driveItem";
    private const string RedactedPath = "/v1.0/shares/{redacted}/driveItem";

    private static List<object?> Scopes(RedactingScopeProvider provider)
    {
        var scopes = new List<object?>();
        provider.ForEachScope(static (scope, list) => list.Add(scope), scopes);
        return scopes;
    }

    private static Dictionary<string, object?> Pairs(object? scope) =>
        ((IEnumerable<KeyValuePair<string, object?>>)scope!).ToDictionary(static p => p.Key, static p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void A_structured_scopes_secret_values_are_redacted()
    {
        var provider = new RedactingScopeProvider(ActivityTrackingOptions.None);
        var state = new Dictionary<string, object?> { ["RequestPath"] = SecretPath, ["RequestId"] = "42" };

        using (provider.Push(state))
        {
            var scope = Scopes(provider).ShouldHaveSingleItem();
            var pairs = Pairs(scope);
            pairs["RequestPath"].ShouldBe(RedactedPath);
            pairs["RequestId"].ShouldBe("42");
            scope!.ToString()!.ShouldNotContain("c2VjcmV0");
        }

        Scopes(provider).ShouldBeEmpty();
    }

    [Fact]
    public void Path_string_and_uri_values_are_redacted()
    {
        var state = new KeyValuePair<string, object?>[]
        {
            new("Path", new PathString(SecretPath)),
            new("Url", new Uri("http://nextcloud/public.php/dav/files/AbCdEf/x.txt")),
            new("Count", 3),
        };

        var pairs = Pairs(RedactingScopeProvider.Redacted(state));

        pairs["Path"].ShouldBe(RedactedPath);
        pairs["Url"].ShouldBe("http://nextcloud/public.php/dav/files/{redacted}/x.txt");
        pairs["Count"].ShouldBe(3);
    }

    [Fact]
    public void A_scope_without_secrets_is_passed_through_unchanged()
    {
        var state = new Dictionary<string, object?> { ["RequestPath"] = "/v1.0/me/messages", ["RequestId"] = "42" };

        RedactingScopeProvider.Redacted(state).ShouldBeSameAs(state);
    }

    [Fact]
    public void A_text_scope_is_redacted_and_other_objects_are_left_alone()
    {
        RedactingScopeProvider.Redacted(SecretPath).ShouldBe(RedactedPath);
        RedactingScopeProvider.Redacted(42).ShouldBe(42);
        RedactingScopeProvider.Redacted(null).ShouldBeNull();
    }

    [Fact]
    public void The_trace_context_is_added_as_the_factory_would()
    {
        var provider = new RedactingScopeProvider(ActivityTrackingOptions.SpanId | ActivityTrackingOptions.TraceId | ActivityTrackingOptions.ParentId
            | ActivityTrackingOptions.TraceState | ActivityTrackingOptions.TraceFlags);
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();

        var trace = Pairs(Scopes(provider).ShouldHaveSingleItem());

        trace["TraceId"].ShouldBe(activity.TraceId.ToHexString());
        trace["SpanId"].ShouldBe(activity.SpanId.ToHexString());
        trace.Keys.ShouldBe(["SpanId", "TraceId", "ParentId", "TraceState", "TraceFlags"]);
    }

    [Fact]
    public void Activity_tags_and_baggage_are_redacted()
    {
        var provider = new RedactingScopeProvider(ActivityTrackingOptions.TraceId | ActivityTrackingOptions.Tags | ActivityTrackingOptions.Baggage);
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C)
            .SetTag("url.path", SecretPath)
            .AddBaggage("origin", SecretPath)
            .Start();

        var scopes = Scopes(provider);

        scopes.Count.ShouldBe(3);
        Pairs(scopes[1])["url.path"].ShouldBe(RedactedPath);
        Pairs(scopes[2])["origin"].ShouldBe(RedactedPath);
    }

    [Fact]
    public void Nothing_is_added_without_an_activity()
    {
        var provider = new RedactingScopeProvider(ActivityTrackingOptions.TraceId);
        Activity.Current = null;

        Scopes(provider).ShouldBeEmpty();
    }
}
