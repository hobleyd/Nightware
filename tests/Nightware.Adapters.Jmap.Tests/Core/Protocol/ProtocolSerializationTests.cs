using System.Text.Json;
using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Tests.Support;

namespace Nightware.Adapters.Jmap.Tests.Core.Protocol;

public sealed class ProtocolSerializationTests
{
    private static JmapJsonContext Json => JmapJsonContext.Default;

    [Fact]
    public void Stalwart_session_document_deserialises()
    {
        var session = JsonSerializer.Deserialize(Fixtures.AliceSession, Json.JmapSessionDocument)!;

        session.Username.ShouldBe("alice@nightware.localhost");
        session.ApiUrl.ShouldBe("https://mail.nightware.localhost/jmap/");
        session.DownloadUrl.ShouldContain("{blobId}");
        session.PrimaryAccounts[JmapCapabilities.Mail].ShouldBe("c");
        session.Accounts["d"].Name.ShouldBe("bob@nightware.localhost");
        session.Accounts["f"].IsReadOnly.ShouldBeTrue();
        session.Capabilities.Keys.ShouldContain(JmapCapabilities.VacationResponse);

        var core = session.Capabilities[JmapCapabilities.Core].Deserialize(Json.CoreCapability)!;
        core.MaxObjectsInGet.ShouldBe(500);
        core.MaxCallsInRequest.ShouldBe(16);

        var mail = session.Accounts["c"].AccountCapabilities[JmapCapabilities.Mail].Deserialize(Json.MailAccountCapability)!;
        mail.MaxMailboxesPerEmail.ShouldBeNull();
        mail.MayCreateTopLevelMailbox.ShouldBeTrue();

        var submission = session.Accounts["c"].AccountCapabilities[JmapCapabilities.Submission].Deserialize(Json.SubmissionAccountCapability)!;
        submission.SubmissionExtensions.Keys.ShouldContain("FUTURERELEASE");
    }

    [Fact]
    public void Recorded_anonymous_session_deserialises()
    {
        var session = JsonSerializer.Deserialize(Fixtures.AnonymousSession, Json.JmapSessionDocument)!;

        session.Accounts.ShouldBeEmpty();
        session.Username.ShouldBeEmpty();
        session.UploadUrl.ShouldBe("https://mail.nightware.localhost/jmap/upload/{accountId}/");
    }

    [Fact]
    public void Email_get_response_deserialises_including_header_and_body_forms()
    {
        var root = JsonNode.Parse(Fixtures.EmailGetResponse)!;
        var args = root["methodResponses"]![0]![1]!;
        var response = args.Deserialize(Json.GetResponseEmail)!;

        response.NotFound.ShouldBe(["zzzzzz"]);
        var email = response.List.ShouldHaveSingleItem();
        email.Id.ShouldBe("eaaaaab");
        email.MailboxIds!["a"].ShouldBeTrue();
        email.Keywords!.ShouldContainKey(Keywords.Seen);
        email.MessageId.ShouldBe(["CAH1yz=abc123@mail.example.org"]);
        email.From![0].ShouldBe(new EmailAddressValue("Bob Example", "bob@nightware.localhost"));
        email.To![0].Name.ShouldBeNull();
        email.ReceivedAt.ShouldBe(new DateTimeOffset(2026, 11, 14, 21, 2, 11, TimeSpan.Zero));
        email.SentAt!.Value.Offset.ShouldBe(TimeSpan.FromHours(11));
        email.BodyStructure!.SubParts!.Count.ShouldBe(3);
        email.BodyStructure.SubParts[2].Extra!.ShouldContainKey("header:Content-Type");
        email.BodyValues!["1"].Value.ShouldContain("numbers");
        email.Attachments![0].Name.ShouldBe("q3.pdf");
        EmailHeaders.GetText(email, "X-Priority").ShouldBe("1");
        EmailHeaders.TryGet(email, "list-unsubscribe", HeaderForm.Urls, all: false, out var unsubscribe).ShouldBeTrue();
        unsubscribe.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void Requests_omit_nulls_and_use_jmap_names()
    {
        var request = new EmailGetRequest("c") { Ids = ["a"], FetchHtmlBodyValues = true, MaxBodyValueBytes = 1024 };

        var json = JsonSerializer.SerializeToNode(request, Json.EmailGetRequest)!.AsObject();

        json["accountId"]!.GetValue<string>().ShouldBe("c");
        json["fetchHTMLBodyValues"]!.GetValue<bool>().ShouldBeTrue();
        json.ContainsKey("properties").ShouldBeFalse();
        json.ContainsKey("fetchTextBodyValues").ShouldBeFalse();
    }

    [Fact]
    public void Dates_are_written_as_utc_dates()
    {
        var query = new EmailQueryRequest("c")
        {
            Filter = new EmailFilter { After = new DateTimeOffset(2026, 1, 2, 13, 4, 5, TimeSpan.FromHours(11)) },
            Sort = [new Comparator("receivedAt", IsAscending: false)],
            Limit = 50,
        };

        var json = JsonSerializer.Serialize(query, Json.EmailQueryRequest);

        json.ShouldContain("\"after\":\"2026-01-02T02:04:05Z\"");
        json.ShouldContain("\"sort\":[{\"property\":\"receivedAt\",\"isAscending\":false}]");
    }

    [Fact]
    public void Utc_date_keeps_non_zero_fractions()
    {
        UtcDateConverter.Format(new DateTimeOffset(2026, 1, 2, 3, 4, 5, 120, TimeSpan.Zero)).ShouldBe("2026-01-02T03:04:05.12Z");
    }

    [Fact]
    public void Filter_operators_serialise_as_jmap_filter_operators()
    {
        var filter = EmailFilter.And(new EmailFilter { InMailbox = "a" }, EmailFilter.Not(new EmailFilter { HasKeyword = Keywords.Seen }));

        var json = JsonSerializer.Serialize(new EmailQueryRequest("c") { Filter = filter }, Json.EmailQueryRequest);

        json.ShouldContain("\"filter\":{\"operator\":\"AND\",\"conditions\":[{\"inMailbox\":\"a\"},{\"operator\":\"NOT\",\"conditions\":[{\"hasKeyword\":\"$seen\"}]}]}");
        EmailFilter.Or(new EmailFilter()).Operator.ShouldBe("OR");
    }

    [Fact]
    public void Patch_null_values_are_written()
    {
        var patch = new Patch()
            .SetEntry("keywords", Keywords.Seen)
            .RemoveEntry("keywords", Keywords.Flagged)
            .RemoveEntry("mailboxIds", "a/b~c")
            .Set("subject", "x")
            .Build();
        var request = new SetRequest<Email>("c") { Update = new Dictionary<string, PatchObject> { ["M1"] = patch } };

        var json = JsonSerializer.Serialize(request, Json.SetRequestEmail);

        json.ShouldContain("\"keywords/$seen\":true");
        json.ShouldContain("\"keywords/$flagged\":null");
        json.ShouldContain("\"mailboxIds/a~1b~0c\":null");
        json.ShouldContain("\"subject\":\"x\"");
    }

    [Fact]
    public void Patch_builds_independent_copies()
    {
        var builder = new Patch().Set("a", 1);
        var first = builder.Build();
        builder.Set("b", 2);

        first.Count.ShouldBe(1);
        builder.Build().Count.ShouldBe(2);
    }

    [Fact]
    public void Set_responses_deserialise_errors()
    {
        const string json = """
            {"accountId":"c","oldState":"1","newState":"2",
             "created":{"k1":{"id":"M9","blobId":"B","threadId":"T","size":10}},
             "updated":{"M1":null},
             "destroyed":["M2"],
             "notUpdated":{"M3":{"type":"notFound","description":"gone"}},
             "notCreated":{"k2":{"type":"invalidProperties","properties":["mailboxIds"],"x-extra":1}}}
            """;

        var response = JsonSerializer.Deserialize(json, Json.SetResponseEmail)!;

        response.Created!["k1"].Id.ShouldBe("M9");
        response.Updated!.ShouldContainKey("M1");
        response.NotUpdated!["M3"].Description.ShouldBe("gone");
        response.NotCreated!["k2"].Properties.ShouldBe(["mailboxIds"]);
        response.NotCreated["k2"].Extra!.ShouldContainKey("x-extra");
    }

    [Fact]
    public void Vacation_and_submission_models_serialise()
    {
        var vacation = new SetRequest<VacationResponse>("c")
        {
            Update = new Dictionary<string, PatchObject> { [VacationResponse.SingletonId] = new Patch().Set("isEnabled", true).Build() },
        };
        JsonSerializer.Serialize(vacation, Json.SetRequestVacationResponse).ShouldContain("\"singleton\":{\"isEnabled\":true}");

        var submission = new EmailSubmissionSetRequest("c")
        {
            Create = new Dictionary<string, EmailSubmission> { ["s"] = new() { IdentityId = "i", EmailId = "#draft" } },
            OnSuccessUpdateEmail = new Dictionary<string, PatchObject> { ["#s"] = new Patch().RemoveEntry("keywords", Keywords.Draft).Build() },
        };
        var json = JsonSerializer.Serialize(submission, Json.EmailSubmissionSetRequest);
        json.ShouldContain("\"emailId\":\"#draft\"");
        json.ShouldContain("\"onSuccessUpdateEmail\":{\"#s\":{\"keywords/$draft\":null}}");
    }

    [Fact]
    public void Header_property_names_follow_rfc_8621()
    {
        EmailHeaders.Property("Message-ID", HeaderForm.MessageIds).ShouldBe("header:Message-ID:asMessageIds");
        EmailHeaders.Property("List-Unsubscribe", HeaderForm.Urls, all: true).ShouldBe("header:List-Unsubscribe:asURLs:all");
        EmailHeaders.Property("X-A").ShouldBe("header:X-A");
        EmailHeaders.Property("From", HeaderForm.Addresses).ShouldBe("header:From:asAddresses");
        EmailHeaders.Property("From", HeaderForm.GroupedAddresses).ShouldBe("header:From:asGroupedAddresses");
        EmailHeaders.Property("Date", HeaderForm.Date).ShouldBe("header:Date:asDate");
        EmailHeaders.Property("Subject", HeaderForm.Text).ShouldBe("header:Subject:asText");
        Should.Throw<ArgumentException>(() => EmailHeaders.Property("Bad:Name"));
    }

    [Fact]
    public void Header_readers_handle_missing_properties()
    {
        var email = new Email
        {
            Extra = new Dictionary<string, JsonElement>
            {
                ["header:References:asMessageIds"] = JsonElement.Parse("[\"a@x\",\"b@x\"]"),
            },
        };

        EmailHeaders.GetMessageIds(email, "References").ShouldBe(["a@x", "b@x"]);
        EmailHeaders.GetMessageIds(email, "In-Reply-To").ShouldBeEmpty();
        EmailHeaders.GetText(new Email(), "Subject").ShouldBeNull();
    }
}
