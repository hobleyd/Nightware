using Nightware.Application.Calls;
using Nightware.Domain.Calls;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Calls;

public sealed class CallUseCaseTests
{
    private static readonly Call Ringing = new("c1", CallState.Establishing, "abcd1234", "https://cloud.example.com/call/abcd1234");

    private readonly ICallService _calls = Substitute.For<ICallService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_valid_call_is_placed()
    {
        var call = new NewCall(["bob-id"], ["audio", "Video"], "https://bot.example.com/callback");
        _calls.PlaceAsync(call, Ct).Returns(Ringing);

        (await new PlaceCall(_calls).ExecuteAsync(call, Ct)).Value.ShouldBeSameAs(Ringing);
    }

    public static TheoryData<NewCall, string> Invalid() => new()
    {
        { new NewCall([], ["audio"], null), "targets" },
        { new NewCall([""], ["audio"], null), "targets" },
        { new NewCall(["bob", "BOB"], ["audio"], null), "targets" },
        { new NewCall(["bob"], ["smell"], null), "requestedModalities" },
        { new NewCall(["bob"], ["audio"], "http://bot.example.com/callback"), "callbackUri" },
        { new NewCall(["bob"], ["audio"], "not a url"), "callbackUri" },
        { new NewCall(["bob"], ["audio"], "https://bot.example.com/" + new string('x', PlaceCall.MaxCallbackUriLength)), "callbackUri" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task An_invalid_call_is_refused_before_ringing_anyone(NewCall call, string target)
    {
        (await new PlaceCall(_calls).ExecuteAsync(call, Ct)).Error!.Target.ShouldBe(target);
        await _calls.DidNotReceiveWithAnyArgs().PlaceAsync(default!, Ct);
    }

    [Fact]
    public async Task Get_and_hang_up_pass_through()
    {
        _calls.GetAsync("c1", Ct).Returns(Ringing);
        _calls.HangUpAsync("c1", Ct).Returns(Result.Success());

        (await new GetCall(_calls).ExecuteAsync("c1", Ct)).Value.ShouldBeSameAs(Ringing);
        (await new HangUpCall(_calls).ExecuteAsync("c1", Ct)).IsSuccess.ShouldBeTrue();
    }
}
