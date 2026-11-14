using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;

namespace Nightware.Adapters.Jmap.Tests.Core.Requests;

public sealed class JmapRequestBuilderTests
{
    [Fact]
    public void Calls_get_sequential_ids_and_carry_their_method()
    {
        var request = new JmapRequestBuilder();

        var first = request.Add(JmapMethods.MailboxGet, new GetRequest("c"));
        var second = request.Add(JmapMethods.VacationResponseGet, new GetRequest("c") { Ids = [VacationResponse.SingletonId] });

        first.CallId.ShouldBe("c0");
        second.CallId.ShouldBe("c1");
        second.Method.ShouldBe("VacationResponse/get");
        request.Count.ShouldBe(2);
        request.Invocations[1].Capabilities.ShouldBe([JmapCapabilities.VacationResponse]);
        request.Invocations[1].Kind.ShouldBe(JmapMethodKind.Get);
    }

    [Fact]
    public void References_replace_the_argument_with_a_hash_argument()
    {
        var request = new JmapRequestBuilder();
        var changes = request.Add(JmapMethods.EmailChanges, new ChangesRequest("c", "s1") { MaxChanges = 100 });

        request.Add(JmapMethods.EmailGet, new EmailGetRequest("c") { Ids = ["ignored"] }, changes.Ref("ids", JmapPaths.Created));

        var args = request.Invocations[1].Arguments;
        args.ContainsKey("ids").ShouldBeFalse();
        args["#ids"]!.ToJsonString().ShouldBe("""{"resultOf":"c0","name":"Email/changes","path":"/created"}""");
        request.Invocations[1].ReferencedCallIds().ShouldBe(["c0"]);
        request.Invocations[0].Arguments.ToJsonString().ShouldBe("""{"accountId":"c","sinceState":"s1","maxChanges":100}""");
    }

    [Fact]
    public void References_must_point_at_earlier_calls()
    {
        var request = new JmapRequestBuilder();
        var stranger = new JmapRequestBuilder().Add(JmapMethods.EmailQuery, new EmailQueryRequest("c"));
        var other = new JmapRequestBuilder();
        other.Add(JmapMethods.MailboxGet, new GetRequest("c"));
        var mismatched = other.Add(JmapMethods.EmailQuery, new EmailQueryRequest("c"));
        request.Add(JmapMethods.MailboxGet, new GetRequest("c"));

        Should.Throw<ArgumentException>(() => request.Add(JmapMethods.EmailGet, new EmailGetRequest("c"), mismatched.Ref("ids", JmapPaths.Ids)));
        Should.Throw<ArgumentException>(() => new JmapRequestBuilder().Add(JmapMethods.EmailGet, new EmailGetRequest("c"), stranger.Ref("ids", JmapPaths.Ids)));
    }

    [Fact]
    public void Result_references_serialise_per_rfc_8620()
    {
        new ResultReference("c0", "Email/query", "/ids").ToJson().ToJsonString()
            .ShouldBe("""{"resultOf":"c0","name":"Email/query","path":"/ids"}""");
    }

    [Fact]
    public void Method_safety_follows_the_method_kind()
    {
        JmapMethods.EmailGet.IsSafe.ShouldBeTrue();
        JmapMethods.EmailQuery.IsSafe.ShouldBeTrue();
        JmapMethods.EmailChanges.IsSafe.ShouldBeTrue();
        JmapMethods.EmailQueryChanges.IsSafe.ShouldBeTrue();
        JmapMethods.EmailSet.IsSafe.ShouldBeFalse();
        JmapMethods.EmailImport.IsSafe.ShouldBeFalse();
        JmapMethods.EmailSubmissionSet.IsSafe.ShouldBeFalse();
    }

    [Fact]
    public void Every_method_descriptor_is_complete()
    {
        var descriptors = typeof(JmapMethods).GetProperties().Select(p => p.GetValue(null)!).ToList();

        descriptors.Count.ShouldBeGreaterThan(15);
        foreach (dynamic descriptor in descriptors)
        {
            ((string)descriptor.Name).ShouldContain("/");
            ((IReadOnlyList<string>)descriptor.Capabilities).ShouldNotBeEmpty();
            ((object)descriptor.ArgumentsType).ShouldNotBeNull();
            ((object)descriptor.ResponseType).ShouldNotBeNull();
        }
    }
}
