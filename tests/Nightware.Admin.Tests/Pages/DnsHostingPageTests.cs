using System.Net;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Admin.Hosting;
using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

/// <summary>An entity's Cloudflare token (entity page) and publishing a domain's records with it (DNS page).</summary>
public sealed partial class DnsHostingPageTests : IDisposable
{
    private const string Token = "cf-SECRET-token-0123456789abcdefghijklmn";
    private static readonly DnsZone AcmeZone = new("z-acme", "acme.example");

    private readonly ConsoleHost _host = new();
    private string? _stored;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string EntityUrl => $"/entities/{ConsoleHost.Acme}";

    private static string DnsUrl => $"/entities/{ConsoleHost.Acme}/dns/acme.example";

    public DnsHostingPageTests()
    {
        _host.Entities.SetSealedDnsTokenAsync(default!, default, Ct).ReturnsForAnyArgs(c =>
        {
            _stored = c.ArgAt<string?>(1);
            return Result.Success();
        });
        _host.Entities.GetSealedDnsTokenAsync(default!, Ct).ReturnsForAnyArgs(_ => Result<string?>.Success(_stored));
        _host.DnsHosting.FindZoneAsync(default!, default!, Ct).ReturnsForAnyArgs(AcmeZone);
        _host.DnsHosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success(
            [new ZoneRecord("r-spf", new DnsRecord("acme.example", "TXT", null, "v=spf1 -all"))]));
        _host.DnsHosting.ApplyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(Result.Success());
        _host.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success(
        [
            new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"),
            new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"),
            new DnsRecord("_dmarc.acme.example", "TXT", 3600, "v=DMARC1; p=reject"),
        ]));
    }

    public void Dispose() => _host.Dispose();

    private void GivenConnected()
    {
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(ConsoleHost.AcmeEntity with { HasDnsToken = true });
        _stored = _host.Services.GetRequiredService<ISecretSealer>().Seal("dns-token:cloudflare:" + ConsoleHost.Acme, Token);
    }

    private async Task<string> GetAsync(string url, string sub = ConsoleHost.GlobalAdmin) =>
        await (await _host.ClientAs(sub).GetAsync(new Uri(url, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

    private Task<HttpResponseMessage> PostAsync(string page, string handler, Dictionary<string, string> fields, string sub = ConsoleHost.GlobalAdmin) =>
        ConsoleHost.PostFormAsync(_host.ClientAs(sub), page, $"{page}?handler={handler}", fields);

    [Fact]
    public async Task A_global_admin_is_offered_to_connect_and_an_entity_admin_only_told_the_state()
    {
        string global = await GetAsync(EntityUrl);
        global.ShouldContain("<h2>DNS hosting</h2>");
        global.ShouldContain("handler=DnsToken");
        global.ShouldContain("type=\"password\" name=\"dnsToken\" autocomplete=\"off\"");
        global.ShouldNotContain("handler=RemoveDnsToken");

        string entityAdmin = await GetAsync(EntityUrl, ConsoleHost.EntityAdmin);
        entityAdmin.ShouldContain("Not connected: publish each domain");
        entityAdmin.ShouldNotContain("handler=DnsToken");
    }

    [Fact]
    public async Task Connecting_checks_the_token_with_cloudflare_and_stores_it_sealed()
    {
        using HttpResponseMessage response = await PostAsync(EntityUrl, "DnsToken", new() { ["dnsToken"] = Token });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.DnsHosting.Received(1).FindZoneAsync(Arg.Is<DnsProviderToken>(t => t.Value == Token), ConsoleHost.Domain("acme.example"), Arg.Any<CancellationToken>());
        _stored.ShouldNotBeNullOrEmpty();
        _stored.ShouldNotContain(Token);
        _stored.Length.ShouldBeLessThanOrEqualTo(255);
        _host.Logs.Lines.ShouldContain(l => l.Contains("admin.audit entity.dns-token.set", StringComparison.Ordinal));
        _host.Logs.Lines.ShouldAllBe(l => !l.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_token_is_explained_and_never_shown_back()
    {
        _host.DnsHosting.FindZoneAsync(default!, default!, Ct).ReturnsForAnyArgs(Error.Validation("Cloudflare refused the token (Invalid API Token).", "dnsToken"));

        using HttpResponseMessage response = await PostAsync(EntityUrl, "DnsToken", new() { ["dnsToken"] = Token });
        string html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Cloudflare refused the token");
        html.ShouldNotContain(Token);
        _stored.ShouldBeNull();
        _host.Logs.Lines.ShouldAllBe(l => !l.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Disconnecting_needs_the_box_ticked_and_forgets_the_token()
    {
        GivenConnected();

        (await PostAsync(EntityUrl, "RemoveDnsToken", [])).StatusCode.ShouldBe(HttpStatusCode.OK);
        _stored.ShouldNotBeNull();

        (await PostAsync(EntityUrl, "RemoveDnsToken", new() { ["confirm"] = "true" })).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        _stored.ShouldBeNull();
    }

    [Fact]
    public async Task An_entity_admin_cannot_set_a_token_or_publish()
    {
        GivenConnected();

        (await PostAsync(EntityUrl, "DnsToken", new() { ["dnsToken"] = Token }, ConsoleHost.EntityAdmin)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(DnsUrl, "Publish", new() { ["PlanFingerprint"] = "x" }, ConsoleHost.EntityAdmin)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await _host.DnsHosting.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Without_a_token_the_dns_page_points_to_the_entity_page()
    {
        string html = await GetAsync(DnsUrl);

        html.ShouldContain("connect Acme Pty's Cloudflare account");
        await _host.DnsHosting.DidNotReceiveWithAnyArgs().FindZoneAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_connected_entitys_dns_page_lists_the_changes_publishing_would_make()
    {
        GivenConnected();

        string html = await GetAsync(DnsUrl);

        html.ShouldContain("Zone acme.example.");
        html.ShouldContain(">Add<");
        html.ShouldContain(">Correct<");
        html.ShouldContain("Publish 3 changes");
        html.ShouldNotContain(Token);
        Fingerprint().IsMatch(html).ShouldBeTrue();
    }

    [Fact]
    public async Task A_cloudflare_failure_is_shown_beside_the_records_rather_than_failing_the_page()
    {
        GivenConnected();
        _host.DnsHosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("Cloudflare could not be reached. Try again shortly."));

        string html = await GetAsync(DnsUrl);

        html.ShouldContain("The Cloudflare zone could not be compared: Cloudflare could not be reached.");
        html.ShouldContain("<table class=\"dns\">");
    }

    [Fact]
    public async Task Publishing_applies_the_plan_on_the_page()
    {
        GivenConnected();
        string fingerprint = Fingerprint().Match(await GetAsync(DnsUrl)).Groups[1].Value;

        using HttpResponseMessage response = await PostAsync(DnsUrl, "Publish", new() { ["PlanFingerprint"] = fingerprint });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.DnsHosting.Received(3).ApplyAsync(Arg.Is<DnsProviderToken>(t => t.Value == Token), AcmeZone, Arg.Any<DnsChange>(), Arg.Any<CancellationToken>());
        _host.Logs.Lines.ShouldContain(l => l.Contains("admin.audit entity.domain.dns-published", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_plan_that_changed_since_the_page_loaded_is_shown_again_not_applied()
    {
        GivenConnected();

        using HttpResponseMessage response = await PostAsync(DnsUrl, "Publish", new() { ["PlanFingerprint"] = "stale" });
        string html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("changed since the page was loaded, so nothing was published");
        await _host.DnsHosting.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, Ct);

        // The page now carries the current plan, so publishing again from it goes ahead.
        string current = Fingerprint().Match(html).Groups[1].Value;
        current.ShouldNotBeEmpty();
        (await PostAsync(DnsUrl, "Publish", new() { ["PlanFingerprint"] = current })).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.DnsHosting.Received(3).ApplyAsync(Arg.Any<DnsProviderToken>(), AcmeZone, Arg.Any<DnsChange>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Changes_cloudflare_refuses_are_listed_with_its_reasons()
    {
        GivenConnected();
        _host.DnsHosting.ApplyAsync(Arg.Any<DnsProviderToken>(), Arg.Any<DnsZone>(), Arg.Is<DnsChange>(c => c.Desired!.Type == "MX"), Arg.Any<CancellationToken>())
            .Returns(Error.Validation("Cloudflare: Content for MX record is invalid."));
        string fingerprint = Fingerprint().Match(await GetAsync(DnsUrl)).Groups[1].Value;

        using HttpResponseMessage response = await PostAsync(DnsUrl, "Publish", new() { ["PlanFingerprint"] = fingerprint });
        string html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("2 changes published to Cloudflare for acme.example. Cloudflare refused 1:");
        html.ShouldContain("MX acme.example: Cloudflare: Content for MX record is invalid.");
    }

    [Fact]
    public void The_sealer_round_trips_only_for_the_same_purpose_and_fits_a_keycloak_attribute()
    {
        var sealer = new DataProtectionSecretSealer(new EphemeralDataProtectionProvider());
        string longest = new('a', DnsProviderToken.MaxLength);

        string sealedToken = sealer.Seal("dns-token:cloudflare:e-acme", longest);

        sealedToken.Length.ShouldBeLessThanOrEqualTo(255);
        sealer.TryUnseal("dns-token:cloudflare:e-acme", sealedToken, out string opened).ShouldBeTrue();
        opened.ShouldBe(longest);
        sealer.TryUnseal("dns-token:cloudflare:e-other", sealedToken, out _).ShouldBeFalse();
        sealer.TryUnseal("dns-token:cloudflare:e-acme", "garbage", out _).ShouldBeFalse();
    }

    [GeneratedRegex("name=\"PlanFingerprint\" value=\"([0-9a-f]{32})\"")]
    private static partial Regex Fingerprint();

    [Fact]
    public async Task Publishing_to_cloudflare_is_a_step_only_while_the_zone_differs()
    {
        GivenConnected();
        string pending = await GetAsync(DnsUrl);
        pending.ShouldContain("Publish to Cloudflare</h2>");
        pending.ShouldContain("<h2>In use now (before you save)</h2>");
        pending.ShouldContain("<main id=\"main\" class=\"wide\">");

        _host.DnsHosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success(
        [
            new ZoneRecord("1", new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example")),
            new ZoneRecord("2", new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all")),
            new ZoneRecord("3", new DnsRecord("_dmarc.acme.example", "TXT", 3600, "v=DMARC1; p=reject")),
        ]));
        string published = await GetAsync(DnsUrl);
        published.ShouldNotContain("Publish to Cloudflare</h2>");
        published.ShouldContain("<span class=\"chip linked\">Published</span> Cloudflare zone acme.example has every record acme.example needs.");
    }

    [Fact]
    public async Task Without_cloudflare_the_records_stand_read_only_with_where_to_publish_them()
    {
        string html = await GetAsync(DnsUrl);

        html.ShouldContain("<h2>In use now (before you save)</h2>");
        html.ShouldContain("Publish these at your DNS provider");
        html.ShouldNotContain("Publish to Cloudflare</h2>");
    }

    [Fact]
    public async Task Removing_another_servers_mx_is_shown_and_needs_the_box_before_publishing()
    {
        GivenConnected();
        _host.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success([new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example")]));
        _host.DnsHosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success(
            [new ZoneRecord("r-old", new DnsRecord("acme.example", "MX", 300, "1 mail.oldhost.example"))]));

        string html = await GetAsync(DnsUrl);
        html.ShouldContain("Another mail server (mail.oldhost.example)");
        html.ShouldContain("name=\"ConfirmMailMove\" value=\"true\" required> All mail for acme.example comes here: mail.oldhost.example stops receiving it");

        string fingerprint = Fingerprint().Match(html).Groups[1].Value;
        string refused = await (await PostAsync(DnsUrl, "Publish", new() { ["PlanFingerprint"] = fingerprint })).Content.ReadAsStringAsync(Ct);
        refused.ShouldContain("Tick the box to confirm that all mail for acme.example should come here.");
        await _host.DnsHosting.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, Ct);

        (await PostAsync(DnsUrl, "Publish", new() { ["PlanFingerprint"] = fingerprint, ["ConfirmMailMove"] = "true" })).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.DnsHosting.Received(1).ApplyAsync(Arg.Any<DnsProviderToken>(), Arg.Any<DnsZone>(), Arg.Is<DnsChange>(c => c.Kind == DnsChangeKind.Remove && c.Current!.Id == "r-old"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Removing_a_domain_can_clean_its_mail_records_out_of_cloudflare_first()
    {
        GivenConnected();
        _host.DnsHosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success(
            [new ZoneRecord("r-mx", new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"))]));
        _host.Entities.RemoveDomainAsync(default!, default!, Ct).ReturnsForAnyArgs(ConsoleHost.AcmeEntity);
        _host.MailDomains.DestroyAsync(default!, Ct).ReturnsForAnyArgs(Result.Success());

        string page = await GetAsync(EntityUrl);
        page.ShouldContain("name=\"cleanUpDns\" value=\"true\"> Also remove acme.example's mail records from Cloudflare");

        using HttpResponseMessage response = await PostAsync(EntityUrl, "RemoveDomain", new() { ["domain"] = "acme.example", ["confirm"] = "true", ["cleanUpDns"] = "true" });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Received.InOrder(() =>
        {
            _host.DnsHosting.ApplyAsync(Arg.Any<DnsProviderToken>(), AcmeZone, Arg.Is<DnsChange>(c => c.Kind == DnsChangeKind.Remove && c.Current!.Id == "r-mx"), Arg.Any<CancellationToken>());
            _host.MailDomains.DestroyAsync(ConsoleHost.Domain("acme.example"), Arg.Any<CancellationToken>());
            _host.Entities.RemoveDomainAsync(ConsoleHost.Acme, Arg.Any<MailDomain>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task A_clean_up_cloudflare_refuses_leaves_the_domain_in_place()
    {
        GivenConnected();
        _host.DnsHosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success(
            [new ZoneRecord("r-mx", new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"))]));
        _host.DnsHosting.ApplyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(Error.Validation("Cloudflare refused it."));

        string html = await (await PostAsync(EntityUrl, "RemoveDomain", new() { ["domain"] = "acme.example", ["confirm"] = "true", ["cleanUpDns"] = "true" })).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("acme.example was not removed: Cloudflare refused to delete 1 of its mail records (Cloudflare refused it.)");
        await _host.Entities.DidNotReceiveWithAnyArgs().RemoveDomainAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Without_a_cloudflare_token_the_removal_says_dns_is_not_touched()
    {
        (await GetAsync(EntityUrl)).ShouldContain("Its DNS records at your provider are not touched.");
    }
}
