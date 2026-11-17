using System.Text.Json.Nodes;

using Nightware.Adapters.Cloudflare.Administration;
using Nightware.Domain.Administration;

namespace Nightware.Adapters.Cloudflare.Tests.Administration;

public sealed class CloudflareRecordsTests
{
    [Fact]
    public void A_long_txt_value_is_written_as_quoted_strings_of_at_most_255_characters()
    {
        string key = "v=DKIM1; k=rsa; p=" + new string('A', 400);

        JsonObject body = CloudflareRecords.ToBody(new DnsRecord("s1._domainkey.acme.example", "TXT", null, key), create: true);

        string content = body["content"]!.GetValue<string>();
        content.ShouldBe($"\"{key[..255]}\" \"{key[255..]}\"");
        DnsPublicationPlanner.Canonical(new DnsRecord("n", "TXT", null, content)).Value.ShouldBe(key);
        body["ttl"]!.GetValue<int>().ShouldBe(CloudflareRecords.AutomaticTtl);
        body.ContainsKey("proxied").ShouldBeFalse();
    }

    [Fact]
    public void Quotes_and_backslashes_in_a_txt_value_are_escaped() =>
        CloudflareRecords.QuotedStrings("a\"b\\c").ShouldBe("\"a\\\"b\\\\c\"");

    [Fact]
    public void Srv_caa_and_tlsa_are_written_as_data()
    {
        JsonObject srv = CloudflareRecords.ToBody(new DnsRecord("_imaps._tcp.acme.example", "SRV", 3600, "0 1 993 mail.acme.example"), create: true);
        srv["data"]!.ToJsonString().ShouldBe("""{"priority":0,"weight":1,"port":993,"target":"mail.acme.example"}""");
        srv["ttl"]!.GetValue<int>().ShouldBe(3600);

        JsonObject caa = CloudflareRecords.ToBody(new DnsRecord("acme.example", "CAA", null, "0 issue \"letsencrypt.org\""), create: true);
        caa["data"]!.ToJsonString().ShouldBe("""{"flags":0,"tag":"issue","value":"letsencrypt.org"}""");

        JsonObject tlsa = CloudflareRecords.ToBody(new DnsRecord("_25._tcp.mail.acme.example", "TLSA", 5, "3 1 1 ABCD"), create: false);
        tlsa["data"]!.ToJsonString().ShouldBe("""{"usage":3,"selector":1,"matching_type":1,"certificate":"ABCD"}""");
        tlsa["ttl"]!.GetValue<int>().ShouldBe(60);
    }

    [Fact]
    public void A_malformed_number_is_sent_as_plain_content_rather_than_throwing() =>
        CloudflareRecords.ToBody(new DnsRecord("acme.example", "MX", null, "ten mail.acme.example"), create: true)["content"]!.GetValue<string>()
            .ShouldBe("ten mail.acme.example");

    [Fact]
    public void A_record_without_an_id_is_ignored() =>
        CloudflareRecords.ToZoneRecord(new JsonObject { ["name"] = "acme.example", ["type"] = "A" }).ShouldBeNull();

    [Fact]
    public void Tlsa_read_from_data_round_trips()
    {
        var json = JsonNode.Parse("""{"id":"t","name":"_25._tcp.mail.acme.example","type":"TLSA","ttl":300,"data":{"usage":3,"selector":1,"matching_type":1,"certificate":"abcd"}}""")!.AsObject();

        CloudflareRecords.ToZoneRecord(json)!.Record.ShouldBe(new DnsRecord("_25._tcp.mail.acme.example", "TLSA", 300, "3 1 1 abcd"));
    }
}
