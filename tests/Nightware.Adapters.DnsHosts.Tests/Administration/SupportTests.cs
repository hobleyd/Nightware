using System.Net;

using Nightware.Adapters.DnsHosts.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Tests.Administration;

public sealed class DnsHostsOptionsValidatorTests
{
    private static string[] Failures(Action<DnsHostsOptions> change)
    {
        var options = new DnsHostsOptions();
        change(options);
        return [.. new DnsHostsOptionsValidator().Validate(null, options).Failures ?? []];
    }

    [Fact]
    public void The_defaults_are_valid() => Failures(static _ => { }).ShouldBeEmpty();

    [Fact]
    public void Each_base_url_is_absolute_http_and_ends_with_a_slash()
    {
        Failures(static o => o.GandiApiBaseUrl = new Uri("/v5/", UriKind.Relative)).ShouldHaveSingleItem().ShouldContain("GandiApiBaseUrl must be an absolute");
        Failures(static o => o.HetznerApiBaseUrl = new Uri("ftp://api.example/")).ShouldHaveSingleItem().ShouldContain("HetznerApiBaseUrl");
        Failures(static o => o.Route53ApiBaseUrl = new Uri("https://route53.amazonaws.com/2013-04-01")).ShouldHaveSingleItem().ShouldContain("end with '/'");
    }

    [Fact]
    public void Timeouts_retries_and_the_record_bound_are_bounded()
    {
        Failures(static o => o.AttemptTimeout = TimeSpan.Zero).ShouldHaveSingleItem();
        Failures(static o => o.AttemptTimeout = TimeSpan.FromMinutes(5)).ShouldHaveSingleItem().ShouldContain("cannot exceed");
        Failures(static o => o.MaxRetryAttempts = 11).ShouldHaveSingleItem();
        Failures(static o => o.MaxRecords = 10).ShouldHaveSingleItem();
    }
}

public sealed class ZoneNamesTests
{
    [Theory]
    [InlineData("acme.example", "acme.example", "@")]
    [InlineData("Mail.Acme.Example.", "acme.example", "mail")]
    [InlineData("_dmarc.mail.acme.example", "acme.example", "_dmarc.mail")]
    public void Names_are_made_relative_to_the_zone(string name, string zone, string relative) =>
        ZoneNames.Relative(name, zone).ShouldBe(relative);

    [Theory]
    [InlineData("@", "acme.example")]
    [InlineData("", "acme.example")]
    [InlineData("mail", "mail.acme.example")]
    [InlineData("mail.acme.example.", "mail.acme.example")]
    [InlineData("acme.example", "acme.example")]
    public void Names_as_hosts_list_them_are_made_absolute(string name, string absolute) =>
        ZoneNames.Absolute(name, "acme.example").ShouldBe(absolute);

    [Fact]
    public void The_candidate_zones_walk_up_to_but_not_including_the_top_level_domain() =>
        ZoneNames.Candidates(DnsHostsTestHost.Domain("mail.acme.example")).ShouldBe(["mail.acme.example", "acme.example"]);

    [Theory]
    [InlineData("TXT", "v=spf1 mx -all", "\"v=spf1 mx -all\"")]
    [InlineData("TXT", "say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("CNAME", "mail.acme.example", "mail.acme.example.")]
    [InlineData("MX", "10 mail.acme.example", "10 mail.acme.example.")]
    [InlineData("SRV", "0 1 993 mail.acme.example", "0 1 993 mail.acme.example.")]
    [InlineData("CAA", "0 issue \"letsencrypt.org\"", "0 issue \"letsencrypt.org\"")]
    [InlineData("A", "81.2.69.160", "81.2.69.160")]
    public void Values_are_written_as_zone_files_write_them(string type, string value, string presentation) =>
        ZoneNames.Presentation(new DnsRecord("acme.example", type, null, value)).ShouldBe(presentation);

    [Fact]
    public void A_long_txt_value_is_split_into_strings_of_255()
    {
        string presented = ZoneNames.QuotedStrings(new string('k', 300));

        presented.ShouldBe($"\"{new string('k', 255)}\" \"{new string('k', 45)}\"");
        DnsPublicationPlanner.Canonical(new DnsRecord("acme.example", "TXT", null, presented)).Value.ShouldBe(new string('k', 300));
    }

    [Fact]
    public void A_record_id_follows_the_name_type_and_value_and_nothing_else()
    {
        string id = ZoneNames.RecordId("Acme.Example.", "mx", "10 mail.acme.example.");

        id.ShouldStartWith("acme.example/MX/");
        ZoneNames.RecordId("acme.example", "MX", "10 mail.acme.example.").ShouldBe(id);
        ZoneNames.RecordId("acme.example", "MX", "20 mail.acme.example.").ShouldNotBe(id);
    }

    [Fact]
    public void A_ttl_is_the_records_own_or_the_fallback_within_the_hosts_bounds()
    {
        ZoneNames.Ttl(null, 3600, 300, 86400).ShouldBe(3600);
        ZoneNames.Ttl(60, 3600, 300, 86400).ShouldBe(300);
        ZoneNames.Ttl(600, 3600, 300, 86400).ShouldBe(600);
    }
}

public sealed class AwsSignatureTests
{
    // AWS's published Signature Version 4 test suite (get-vanilla, get-vanilla-query-order-key-case).
    private const string KeyId = "AKIDEXAMPLE";
    private const string Secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private static readonly DateTimeOffset When = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    private static string Authorization(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AwsSignature.Sign(request, [], KeyId, Secret, "us-east-1", "service", When);
        request.Headers.GetValues("x-amz-date").ShouldBe(["20150830T123600Z"]);
        return request.Headers.GetValues("Authorization").Single();
    }

    [Fact]
    public void Signs_awss_vanilla_get() =>
        Authorization("https://example.amazonaws.com/").ShouldBe(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, SignedHeaders=host;x-amz-date, "
            + "Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31");

    [Fact]
    public void Signs_query_parameters_in_name_order() =>
        Authorization("https://example.amazonaws.com/?Param2=value2&Param1=value1").ShouldEndWith(
            "Signature=b97d918cfa904a5beff61c982a1b6f458b799221646efd99d3219ec94cdf2500");
}

public sealed class DnsHostClientFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void A_refused_credential_is_a_validation_error_on_the_token(HttpStatusCode status)
    {
        Error error = DnsHostClient.Failure(DnsProvider.Hetzner, status, new DnsHostFault("the token you have provided is invalid"), null);

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Target.ShouldBe("dnsToken");
        error.Message.ShouldBe("Hetzner refused the credential (the token you have provided is invalid). Check that it is current and may edit the domain's zone.");
    }

    [Fact]
    public void A_host_may_say_a_credential_is_refused_or_requests_throttled_with_another_status()
    {
        DnsHostClient.Failure(DnsProvider.Route53, HttpStatusCode.BadRequest, new DnsHostFault("x", TokenRefused: true), null).Target.ShouldBe("dnsToken");
        Error throttled = DnsHostClient.Failure(DnsProvider.Route53, HttpStatusCode.BadRequest, new DnsHostFault("Rate exceeded", Throttled: true), TimeSpan.FromSeconds(2.5));
        throttled.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        throttled.RetryAfterSeconds.ShouldBe(3);
    }

    [Fact]
    public void Outages_missing_things_and_refused_records_are_told_apart()
    {
        DnsHostClient.Failure(DnsProvider.Gandi, HttpStatusCode.TooManyRequests, new DnsHostFault(null), null).Kind.ShouldBe(ErrorKind.BackendUnavailable);
        DnsHostClient.Failure(DnsProvider.Gandi, HttpStatusCode.BadGateway, new DnsHostFault(null), null).Message.ShouldBe("Gandi failed (HTTP 502). Try again shortly.");
        DnsHostClient.Failure(DnsProvider.Gandi, HttpStatusCode.NotFound, new DnsHostFault("Not found"), null).Kind.ShouldBe(ErrorKind.NotFound);
        Error refused = DnsHostClient.Failure(DnsProvider.DigitalOcean, HttpStatusCode.UnprocessableEntity, new DnsHostFault("Data needs to end with a dot (.)"), null);
        refused.Kind.ShouldBe(ErrorKind.Validation);
        refused.Message.ShouldBe("DigitalOcean: Data needs to end with a dot (.).");
    }
}
