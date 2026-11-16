using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;

using Nightware.Api.Graph.Json;
using Nightware.Api.Hosting;
using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Hosting;

/// <summary>A Production façade refuses to start without a real <c>DeltaLinks:SigningKey</c>; other environments keep the per-process fallback.</summary>
public sealed class SigningKeyValidatorTests
{
    private static readonly string StrongKey = new('k', SigningKeyValidator.MinimumBytes);

    private static bool Validate(string environment, string? key)
    {
        var host = new HostingEnvironment { EnvironmentName = environment };
        return new SigningKeyValidator(host).Validate(null, new DeltaLinkOptions { SigningKey = key }).Succeeded;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789012345678901234567890")] // 31 bytes
    [InlineData("dev-only-delta-link-signing-key")]
    [InlineData("development-only-delta-link-signing-key-change-me")]
    public void Production_rejects_a_missing_short_or_published_key(string? key) =>
        Validate(Environments.Production, key).ShouldBeFalse();

    [Fact]
    public void Production_accepts_a_key_of_32_bytes() =>
        Validate(Environments.Production, StrongKey).ShouldBeTrue();

    [Fact]
    public void Bytes_are_counted_not_characters() =>
        Validate(Environments.Production, new string('é', 16)).ShouldBeTrue(); // 16 chars, 32 UTF-8 bytes

    [Theory]
    [InlineData("Development", null)]
    [InlineData("Testing", null)]
    [InlineData("Development", "short")]
    public void Other_environments_keep_the_per_process_fallback(string environment, string? key) =>
        Validate(environment, key).ShouldBeTrue();

    [Fact]
    public void The_real_Program_refuses_to_start_in_Production_without_a_key()
    {
        var error = ProgramStartup.Failure(static () => ProductionProgram(key: null)).ToString();

        error.ShouldContain("DeltaLinks__SigningKey");
        error.ShouldContain("NIGHTWARE_DELTA_LINK_KEY");
    }

    [Fact]
    public void The_real_Program_starts_in_Production_with_a_strong_key()
    {
        using var factory = ProductionProgram(StrongKey);

        using var client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    private static WebApplicationFactory<Program> ProductionProgram(string? key) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(Environments.Production);
            b.UseSetting("Directory:ClientSecret", "test-secret");
            b.UseSetting("DeltaLinks:SigningKey", key ?? string.Empty);
        });
}
