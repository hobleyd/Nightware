using Nightware.Application.Mail;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Mail;

public sealed class TranslateExchangeIdsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<Result<IReadOnlyList<TranslatedId>>> Translate(IReadOnlyList<string>? ids, string? source, string? target) =>
        new TranslateExchangeIds().ExecuteAsync(ids, source, target, Ct);

    [Theory]
    [InlineData("restId", "restImmutableEntryId")]
    [InlineData("RestImmutableEntryId", "restId")]
    [InlineData("restId", "restId")]
    public async Task Rest_ids_are_already_immutable_so_they_translate_to_themselves(string source, string target)
    {
        var result = await Translate(["e.abc", "d.def"], source, target);

        result.Value.ShouldBe([new TranslatedId("e.abc", "e.abc"), new TranslatedId("d.def", "d.def")]);
    }

    [Theory]
    [InlineData("ewsId", "restId")]
    [InlineData("restId", "entryId")]
    [InlineData("immutableEntryId", "restId")]
    public async Task Exchange_store_formats_are_400_notSupported(string source, string target)
    {
        (await Translate(["e.abc"], source, target)).Error!.Code.ShouldBe("notSupported");
    }

    [Fact]
    public async Task Missing_or_too_many_ids_or_an_unknown_format_is_400()
    {
        (await Translate(null, "restId", "restId")).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Translate([], "restId", "restId")).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Translate([.. Enumerable.Repeat("e.a", TranslateExchangeIds.MaxIds + 1)], "restId", "restId")).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Translate(["e.a", " "], "restId", "restId")).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Translate(["e.a"], "graphId", "restId")).Error!.Target.ShouldBe("sourceIdType");
        (await Translate(["e.a"], "restId", null)).Error!.Target.ShouldBe("targetIdType");
    }
}
