using Nightware.Adapters.Jmap.Mail.Rules;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Adapters.Jmap.Tests.Mail.Rules;

public sealed class SieveScriptWriterTests
{
    private static MessageRule Rule(int sequence, MessageRulePredicates conditions, MessageRuleActions actions, bool enabled = true) =>
        new($"r{sequence}", $"Rule {sequence}", sequence, enabled) { Conditions = conditions, Actions = actions };

    [Fact]
    public void Client_text_is_escaped_inside_quoted_strings()
    {
        SieveScriptWriter.Quoted("a\"b\\c\r\nd").ShouldBe("\"a\\\"b\\\\c  d\"");
    }

    [Fact]
    public void A_text_block_dot_stuffs_its_lines_and_ends_with_a_lone_dot()
    {
        SieveScriptWriter.Text("one\n.two\r\n..three\n").ShouldBe("text:\none\n..two\n...three\n.\n");
    }

    [Fact]
    public void A_rule_becomes_tests_and_actions_in_order()
    {
        var rule = Rule(1,
            new MessageRulePredicates
            {
                SubjectContains = ["Invoice", "Bill"],
                FromAddresses = [new EmailAddress("billing@example.com")],
                HasAttachments = true,
                Importance = Importance.High,
            },
            new MessageRuleActions { MarkAsRead = true, MoveToFolder = new FolderId("mbx7"), StopProcessingRules = true })
            with { Exceptions = new MessageRulePredicates { SenderContains = ["noreply"] } };

        var script = SieveScriptWriter.Write(new FilterState([rule], null), "trash");

        script.ShouldStartWith("require [\"body\", \"copy\", \"date\", \"fileinto\", \"imap4flags\", \"mailboxid\", \"mime\", \"relational\", \"vacation\"];\n");
        script.ShouldContain("""
            if allof(header :contains "subject" ["Invoice", "Bill"], address :is "from" ["billing@example.com"], header :mime :anychild :contains "Content-Disposition" "attachment", anyof(header :is "importance" "high", header :matches "x-priority" ["1*", "2*"]), not anyof(header :contains "from" ["noreply"])) {
              addflag "\\Seen";
              fileinto :mailboxid "mbx7" "INBOX";
              stop;
            }
            """.ReplaceLineEndings("\n"));
        script.ShouldNotContain("vacation :mime");
    }

    [Fact]
    public void Rules_without_conditions_match_everything_and_disabled_rules_are_left_out()
    {
        var script = SieveScriptWriter.Write(new FilterState(
            [
                Rule(2, new MessageRulePredicates(), new MessageRuleActions { Delete = true }),
                Rule(1, new MessageRulePredicates(), new MessageRuleActions { PermanentDelete = true }, enabled: false),
                Rule(3, new MessageRulePredicates { BodyContains = ["x"] }, new MessageRuleActions
                {
                    CopyToFolder = new FolderId("c"), ForwardTo = [new EmailAddress("f@example.com")],
                }),
            ], null), "trash");

        script.ShouldContain("if true {\n  fileinto :mailboxid \"trash\" \"INBOX\";\n}");
        script.ShouldNotContain("discard");
        script.ShouldContain("if body :text :contains [\"x\"] {\n  redirect :copy \"f@example.com\";\n  fileinto :copy :mailboxid \"c\" \"INBOX\";\n}");
        script.IndexOf("\"trash\"", StringComparison.Ordinal).ShouldBeLessThan(script.IndexOf("\"c\" \"INBOX\"", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scheduled_reply_comes_first_inside_currentdate_bounds_in_stalwarts_shape()
    {
        var reply = new AutoReplySetting(AutoReplyStatus.Scheduled)
        {
            ExternalReplyMessage = "<p>Away</p>\n.dot",
            ScheduledStartDateTime = new DateTimeTimeZone(new DateTime(2026, 12, 1, 9, 0, 0), "UTC"),
            ScheduledEndDateTime = new DateTimeTimeZone(new DateTime(2026, 12, 5, 17, 0, 0), "UTC"),
        };
        var rule = Rule(1, new MessageRulePredicates(), new MessageRuleActions { StopProcessingRules = true });

        var script = SieveScriptWriter.Write(new FilterState([rule], reply), null);

        script.ShouldContain("if currentdate :value \"ge\" \"iso8601\" \"2026-12-01T09:00:00Z\" {\nif currentdate :value \"le\" \"iso8601\" \"2026-12-05T17:00:00Z\" {\nvacation :mime text:\nContent-Type: multipart/alternative;");
        script.ShouldContain("\n..dot\n");
        script.IndexOf("vacation :mime", StringComparison.Ordinal).ShouldBeLessThan(script.IndexOf("if true", StringComparison.Ordinal));
    }

    [Fact]
    public void A_disabled_reply_writes_no_vacation_and_an_always_on_one_no_dates()
    {
        SieveScriptWriter.Write(new FilterState([], new AutoReplySetting(AutoReplyStatus.Disabled) { ExternalReplyMessage = "x" }), null).ShouldNotContain("vacation :mime");
        var always = SieveScriptWriter.Write(new FilterState([], new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) { InternalReplyMessage = "<b>in</b>" }), null);
        always.ShouldContain("\nvacation :mime text:\n");
        always.ShouldNotContain("currentdate");
        always.ShouldContain("<b>in</b>");
    }

    [Fact]
    public void The_state_rides_along_and_reads_back()
    {
        var rule = Rule(1, new MessageRulePredicates { SentToAddresses = [new EmailAddress("me@example.com", "Me")] }, new MessageRuleActions { MarkAsRead = true });
        var reply = new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) { ExternalReplyMessage = "Ünïcode" };

        var state = FilterState.FromScript(SieveScriptWriter.Write(new FilterState([rule], reply), null))!;

        state.Rules.Single().Conditions.SentToAddresses.ShouldBe([new EmailAddress("me@example.com", "Me")]);
        state.Rules.Single().Actions.MarkAsRead.ShouldBeTrue();
        state.AutoReply!.ExternalReplyMessage.ShouldBe("Ünïcode");
    }

    [Fact]
    public void A_script_without_a_readable_state_is_not_ours()
    {
        FilterState.FromScript("require \"fileinto\";\nkeep;\n").ShouldBeNull();
        FilterState.FromScript("# nightware-state: !!!not-base64\n").ShouldBeNull();
        FilterState.FromScript("# nightware-state: " + Convert.ToBase64String("{\"v\":2}"u8.ToArray()) + "\n").ShouldBeNull();
    }
}
