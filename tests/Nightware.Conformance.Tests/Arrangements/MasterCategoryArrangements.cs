using Nightware.Application.Mail.Categories;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 12 <c>outlook/masterCategories</c> (ADR 0006 D5), against captures from Microsoft 365.</summary>
public sealed class MasterCategoryArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly MasterCategory Captured = new("00000000-0000-0000-0000-000000000010", "Nightware eb09af", "preset2");

    /// <summary>The tenant's defaults as captured; Nightware's own defaults have the same names and colours.</summary>
    private static readonly MasterCategory[] TenantDefaults =
    [
        new("00000000-0000-0000-0000-000000000004", "Red category", "preset0"),
        new("00000000-0000-0000-0000-000000000005", "Orange category", "preset1"),
        new("00000000-0000-0000-0000-000000000006", "Yellow category", "preset3"),
        new("00000000-0000-0000-0000-000000000007", "Green category", "preset4"),
        new("00000000-0000-0000-0000-000000000008", "Blue category", "preset7"),
        new("00000000-0000-0000-0000-000000000009", "Purple category", "preset8"),
    ];

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("mail.master-categories.list", static fakes => Stored(fakes, TenantDefaults));

        yield return new ConformanceArrangement("mail.master-categories.get", static fakes => Stored(fakes, [.. TenantDefaults, Captured]));

        // A new category's id is minted by the façade, so it is the one thing compared loosely.
        yield return new ConformanceArrangement("mail.master-categories.create", static fakes => Stored(fakes, TenantDefaults))
        {
            IgnorePaths = ["id"],
        };

        yield return new ConformanceArrangement("mail.master-categories.update", static fakes => Stored(fakes, [.. TenantDefaults, Captured]));

        yield return new ConformanceArrangement("mail.master-categories.delete", static fakes => Stored(fakes, [.. TenantDefaults, Captured]));
    }

    private static void Stored(PortFakes fakes, MasterCategory[] categories)
    {
        var store = fakes.For<IMasterCategoryStore>();
        store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<MasterCategory>?>.Success(categories));
        store.SaveAsync(Me, Arg.Any<IReadOnlyList<MasterCategory>>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
    }
}
