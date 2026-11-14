using Nightware.Conformance.Tests.Harness;

namespace Nightware.Conformance.Tests;

/// <summary>
/// Replays every golden fixture. An operation runs once a feature phase registers its
/// <see cref="ConformanceArrangement"/> (in <c>Arrangements/</c>); until then it is skipped, not failed.
/// </summary>
public sealed class ConformanceTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (var file in ConformanceRunner.FixtureFiles())
        {
            data.Add(file);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task Response_matches_the_fixture(string fixtureFile)
    {
        var fixture = ConformanceRunner.Load(fixtureFile);
        var arrangement = ArrangementRegistry.Find(fixture.OperationId);
        if (arrangement is null)
        {
            Assert.Skip($"{fixture.OperationId}: no conformance arrangement registered yet (feature not implemented).");
        }

        await ConformanceRunner.AssertConformsAsync(fixture, arrangement, TestContext.Current.CancellationToken);
    }
}
