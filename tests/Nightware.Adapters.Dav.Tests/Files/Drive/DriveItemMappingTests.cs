using Nightware.Adapters.Dav.Files.Drive;

namespace Nightware.Adapters.Dav.Tests.Files.Drive;

public sealed class DriveItemMappingTests
{
    [Theory]
    [InlineData("SHA1:43f932e4f7c6ecd136a695b7008694bb69d517bd", "SHA1", 40, "43F932E4F7C6ECD136A695B7008694BB69D517BD")]
    [InlineData("MD5:0cc175b9c0f1b6a831c399e269772661 SHA1:43f932e4f7c6ecd136a695b7008694bb69d517bd", "SHA1", 40, "43F932E4F7C6ECD136A695B7008694BB69D517BD")]
    [InlineData("sha256:ca978112ca1bbdcafac231b39a23dc4da786eff8147c4e72b9807785afee48bb", "SHA256", 64, "CA978112CA1BBDCAFAC231B39A23DC4DA786EFF8147C4E72B9807785AFEE48BB")]
    [InlineData("MD5:0cc175b9c0f1b6a831c399e269772661", "SHA1", 40, null)]
    [InlineData("SHA1:not-hex-at-all-not-hex-at-all-not-hex-at", "SHA1", 40, null)]
    [InlineData("SHA1:43f9", "SHA1", 40, null)]
    [InlineData("", "SHA1", 40, null)]
    [InlineData(null, "SHA1", 40, null)]
    public void A_checksum_is_read_only_when_nextcloud_stored_a_well_formed_one(string? checksums, string algorithm, int length, string? expected) =>
        DriveItemMapping.Checksum(checksums, algorithm, length).ShouldBe(expected);

    [Fact]
    public void Every_lookup_asks_for_the_stored_checksums() =>
        DriveItemMapping.Properties.ShouldContain(DriveItemMapping.Checksums);
}
