using Nightware.Application.Calls;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calls;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 26: delegated calls (ADR 0009 D7), constructed from Graph's reference pages. A call id is opaque (it seals the
/// Talk session), and the call happens in a Talk room whose join link sits on the file site, <c>cloud.example.com</c>.
/// </summary>
public sealed class CallsArrangements : IConformanceArrangements
{
    private const string CallId = "AQIDBHNlYWxlZC1jYWxsLWlkLWV4YW1wbGU";

    private static readonly Call Ringing = new(CallId, CallState.Establishing, "nge9dkks", "https://cloud.example.com/cloud/call/nge9dkks")
    {
        Source = new CallParty("me-id", "Me Example"),
        Targets = [new CallParty("bob-id", "Bob Brown")],
        RequestedModalities = ["audio"],
        CallbackUri = "https://bot.example.com/callback",
    };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("calls.create", static f => f.For<ICallService>()
            .PlaceAsync(Arg.Is<NewCall>(c => c.Targets.Single() == "bob-id" && c.RequestedModalities.Single() == "audio" && c.CallbackUri == "https://bot.example.com/callback"), Any)
            .Returns(Ringing));
        yield return new ConformanceArrangement("calls.get", static f => f.For<ICallService>().GetAsync(CallId, Any).Returns(Ringing with { State = CallState.Established }));
        yield return new ConformanceArrangement("calls.delete", static f => f.For<ICallService>().HangUpAsync(CallId, Any).Returns(Result.Success()));
    }

    private static CancellationToken Any => Arg.Any<CancellationToken>();
}
