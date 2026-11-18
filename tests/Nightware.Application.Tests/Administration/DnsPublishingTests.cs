using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

/// <summary>An entity's Cloudflare token (kept sealed) and publishing a domain's records with it.</summary>
public sealed class DnsPublishingTests
{
    private const string Token = "cf-token-0123456789abcdefghijklmnopqrstuv";
    private const string Sealed = "sealed:" + Token;

    private static readonly DnsZone AcmeZone = new("z-acme", "acme.example");

    private readonly AdminTestBed _bed = new();
    private readonly IDnsHosting _hosting = Substitute.For<IDnsHosting>();
    private readonly FakeSealer _sealer = new();

    private static CancellationToken Ct => AdminTestBed.Ct;

    public DnsPublishingTests()
    {
        _bed.Entities.SetSealedDnsTokenAsync(default!, default, default).ReturnsForAnyArgs(Result.Success());
        _bed.Entities.GetSealedDnsTokenAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<string?>.Success(Sealed));
        _bed.MailDomains.GetDnsRecordsAsync(default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success(
        [
            new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"),
            new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all"),
        ]));
        _hosting.FindZoneAsync(default!, default!, Ct).ReturnsForAnyArgs(AcmeZone);
        _hosting.ListRecordsAsync(default!, default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success(
            [new ZoneRecord("r-spf", new DnsRecord("acme.example", "TXT", null, "v=spf1 -all"))]));
        _hosting.ApplyAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
    }

    private DnsTokenVault Vault() => new(_bed.Entities, _sealer);

    private SetEntityDnsToken SetToken() => new(_bed.Actor, _bed.Entities, _hosting, Vault(), _bed.Audit);

    private PlanDnsPublication Planner() => new(_bed.Actor, new GetEntityDnsRecords(_bed.Actor, _bed.Entities, _bed.MailDomains, _bed.PublicAddresses()), _hosting, Vault());

    private PublishDnsRecords Publisher() => new(_bed.Actor, Planner(), _hosting, _bed.Audit);

    private RemoveDomainDnsRecords CleanUp() => new(_bed.Actor, Planner(), _hosting, _bed.Audit);

    [Fact]
    public async Task Cleaning_up_removes_only_the_records_that_match_what_the_console_publishes()
    {
        _hosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success(
        [
            new ZoneRecord("r-mx", new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example")),
            new ZoneRecord("r-spf", new DnsRecord("acme.example", "TXT", null, "v=spf1 include:other.example -all")),
            new ZoneRecord("r-web", new DnsRecord("www.acme.example", "A", 300, "81.2.69.160")),
        ]));

        DnsCleanUp done = (await CleanUp().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        done.Removed.ShouldBe(1);
        done.Left.ShouldBe(1);
        done.Failed.ShouldBeEmpty();
        await _hosting.Received(1).ApplyAsync(Arg.Any<DnsProviderToken>(), AcmeZone, Arg.Is<DnsChange>(c => c.Kind == DnsChangeKind.Remove && c.Current!.Id == "r-mx"), Arg.Any<CancellationToken>());
        _bed.Audit.Single("entity.domain.dns-removed").Details["removed"].ShouldBe("1");
    }

    [Fact]
    public async Task A_record_cloudflare_refuses_to_delete_is_reported()
    {
        _hosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success(
            [new ZoneRecord("r-mx", new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"))]));
        _hosting.ApplyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(Error.Validation("Cloudflare refused it."));

        DnsCleanUp done = (await CleanUp().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        done.Removed.ShouldBe(0);
        done.Failed.ShouldHaveSingleItem().Error!.Message.ShouldBe("Cloudflare refused it.");
        _bed.Audit.Single("entity.domain.dns-removed").Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task Only_a_global_administrator_cleans_up_dns()
    {
        _bed.ActAsEntityAdmin();

        (await CleanUp().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _hosting.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task A_token_that_reaches_the_entitys_zone_is_stored_sealed_for_that_entity()
    {
        Result<DnsTokenReach> result = await SetToken().ExecuteAsync(AdminTestBed.Acme, $"  {Token} ", Ct);

        result.Value.Reached.ShouldHaveSingleItem().Zone.ShouldBe(AcmeZone);
        await _bed.Entities.Received(1).SetSealedDnsTokenAsync(AdminTestBed.Acme, Sealed, Arg.Any<CancellationToken>());
        _sealer.Purposes.ShouldBe([DnsTokenVault.Purpose(AdminTestBed.Acme)]);
        AuditEvent audit = _bed.Audit.Single("entity.dns-token.set");
        audit.Succeeded.ShouldBeTrue();
        audit.Details.Values.ShouldNotContain(v => v != null && v.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_cloudflare_refuses_is_not_stored()
    {
        _hosting.FindZoneAsync(default!, default!, Ct).ReturnsForAnyArgs(Error.Validation("Cloudflare refused the token.", "dnsToken"));

        Result<DnsTokenReach> result = await SetToken().ExecuteAsync(AdminTestBed.Acme, Token, Ct);

        result.Error!.Target.ShouldBe("dnsToken");
        await _bed.Entities.DidNotReceiveWithAnyArgs().SetSealedDnsTokenAsync(default!, default, Ct);
        _bed.Audit.Single("entity.dns-token.set").Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task A_token_that_reaches_none_of_the_domains_is_not_stored_and_one_that_reaches_some_says_which_not()
    {
        _hosting.FindZoneAsync(default!, default!, Ct).ReturnsForAnyArgs(Error.NotFound("no zone"));
        (await SetToken().ExecuteAsync(AdminTestBed.Acme, Token, Ct)).Error!.Message.ShouldContain("reaches none of acme.example");
        await _bed.Entities.DidNotReceiveWithAnyArgs().SetSealedDnsTokenAsync(default!, default, Ct);

        _bed.Entities.GetAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(AdminTestBed.AcmeEntity with { Domains = [AdminTestBed.Domain("acme.example"), AdminTestBed.Domain("acme.org")] });
        _hosting.FindZoneAsync(Arg.Any<DnsProviderToken>(), AdminTestBed.Domain("acme.example"), Arg.Any<CancellationToken>()).Returns(AcmeZone);
        Result<DnsTokenReach> partial = await SetToken().ExecuteAsync(AdminTestBed.Acme, Token, Ct);
        partial.Value.Unreached.ShouldBe([AdminTestBed.Domain("acme.org")]);
    }

    [Fact]
    public async Task A_blank_token_removes_the_stored_one_and_a_malformed_one_is_refused()
    {
        (await SetToken().ExecuteAsync(AdminTestBed.Acme, " ", Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Entities.Received(1).SetSealedDnsTokenAsync(AdminTestBed.Acme, null, Arg.Any<CancellationToken>());
        _bed.Audit.Single("entity.dns-token.removed").Succeeded.ShouldBeTrue();

        (await SetToken().ExecuteAsync(AdminTestBed.Acme, "two words", Ct)).Error!.Target.ShouldBe("dnsToken");
        await _hosting.DidNotReceiveWithAnyArgs().FindZoneAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Only_global_admins_set_tokens_or_publish()
    {
        _bed.ActAsEntityAdmin();

        (await SetToken().ExecuteAsync(AdminTestBed.Acme, Token, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await Planner().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await Publisher().ExecuteAsync(AdminTestBed.Acme, "acme.example", "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _bed.Entities.DidNotReceiveWithAnyArgs().GetSealedDnsTokenAsync(default!, Ct);
    }

    [Fact]
    public async Task The_plan_compares_the_records_with_spf_and_dmarc_choices_applied_against_the_zone()
    {
        DnsPublicationPreview preview = (await Planner().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        preview.Plan.Changes.Select(static c => (c.Kind, c.Desired?.Type)).ShouldBe(
        [
            (DnsChangeKind.Create, "MX"),
            (DnsChangeKind.Update, "TXT"),
            (DnsChangeKind.Create, "TXT"), // the default DMARC record the mail server lacked
        ]);
        await _hosting.Received(1).FindZoneAsync(Arg.Is<DnsProviderToken>(t => t.Value == Token), AdminTestBed.Domain("acme.example"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_readable_token_or_a_zone_there_is_no_plan()
    {
        _bed.Entities.GetSealedDnsTokenAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<string?>.Success(null));
        (await Planner().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Message.ShouldContain("has no Cloudflare token");

        _bed.Entities.GetSealedDnsTokenAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<string?>.Success("sealed-with-a-lost-key"));
        (await Planner().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Message.ShouldContain("can no longer be read");

        _bed.Entities.GetSealedDnsTokenAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<string?>.Success(Sealed));
        _hosting.FindZoneAsync(default!, default!, Ct).ReturnsForAnyArgs(Error.NotFound("no zone"));
        (await Planner().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Message.ShouldContain("reaches no zone for acme.example");
    }

    [Fact]
    public async Task A_token_sealed_for_another_entity_does_not_open()
    {
        _bed.Entities.GetSealedDnsTokenAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<string?>.Success(Sealed));
        _sealer.Refuse = DnsTokenVault.Purpose(AdminTestBed.Acme);

        (await Vault().OpenAsync(AdminTestBed.Acme, Ct)).Error!.Target.ShouldBe("dnsToken");
    }

    [Fact]
    public async Task Publishing_applies_the_plan_that_was_seen_and_audits_counts()
    {
        string fingerprint = (await Planner().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value.Plan.Fingerprint;

        DnsPublication publication = (await Publisher().ExecuteAsync(AdminTestBed.Acme, "acme.example", fingerprint, Ct)).Value;

        publication.Stale.ShouldBeFalse();
        publication.Written.ShouldBe(3);
        await _hosting.Received(3).ApplyAsync(Arg.Any<DnsProviderToken>(), AcmeZone, Arg.Any<DnsChange>(), Arg.Any<CancellationToken>());
        AuditEvent audit = _bed.Audit.Single("entity.domain.dns-published");
        audit.Details["created"].ShouldBe("2");
        audit.Details["updated"].ShouldBe("1");
        audit.Details["failed"].ShouldBe("0");
    }

    [Fact]
    public async Task A_plan_that_changed_since_it_was_seen_is_not_applied()
    {
        DnsPublication publication = (await Publisher().ExecuteAsync(AdminTestBed.Acme, "acme.example", "an-old-plan", Ct)).Value;

        publication.Stale.ShouldBeTrue();
        publication.Preview.Plan.HasWork.ShouldBeTrue();
        await _hosting.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default!, Ct);
        _bed.Audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_change_is_reported_and_the_others_still_made()
    {
        _hosting.ApplyAsync(Arg.Any<DnsProviderToken>(), Arg.Any<DnsZone>(), Arg.Is<DnsChange>(c => c.Desired!.Type == "MX"), Arg.Any<CancellationToken>())
            .Returns(Error.Validation("Cloudflare: bad MX."));
        string fingerprint = (await Planner().ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value.Plan.Fingerprint;

        DnsPublication publication = (await Publisher().ExecuteAsync(AdminTestBed.Acme, "acme.example", fingerprint, Ct)).Value;

        publication.Written.ShouldBe(2);
        publication.Failed.ShouldHaveSingleItem().Error!.Message.ShouldBe("Cloudflare: bad MX.");
        _bed.Audit.Single("entity.domain.dns-published").Succeeded.ShouldBeFalse();
    }

    /// <summary>"Seals" by prefixing; refuses to open for <see cref="Refuse"/> or text it did not seal.</summary>
    private sealed class FakeSealer : ISecretSealer
    {
        public List<string> Purposes { get; } = [];

        public string? Refuse { get; set; }

        public string Seal(string purpose, string secret)
        {
            Purposes.Add(purpose);
            return "sealed:" + secret;
        }

        public bool TryUnseal(string purpose, string sealedSecret, out string secret)
        {
            bool ok = purpose != Refuse && sealedSecret.StartsWith("sealed:", StringComparison.Ordinal);
            secret = ok ? sealedSecret["sealed:".Length..] : string.Empty;
            return ok;
        }
    }
}
