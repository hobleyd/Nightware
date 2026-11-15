using System.Buffers.Text;
using System.Text;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Mail;

public sealed class AttachmentItemIdTests
{
    private static EmailBodyPart Leaf(string partId, string? name, long size) => new() { PartId = partId, BlobId = "B" + partId, Name = name, Size = size, Type = "application/octet-stream" };

    private static EmailBodyPart Multipart(params EmailBodyPart[] children) => new() { Type = "multipart/mixed", SubParts = children };

    private static string Encode(string json) => AttachmentItemId.Prefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Encodes_ordinal_size_and_name_as_base64url_json()
    {
        var id = AttachmentItemId.Of("notes.txt", 14, 0);

        id.ToString().ShouldBe(Encode("""[0,14,"notes.txt"]"""));
        AttachmentItemId.Parse(id.ToAttachmentId()).Value.ShouldBe(id);
    }

    [Theory]
    [InlineData("Résumé – final.pdf")]
    [InlineData("")]
    [InlineData("a\"quote\\and/slash.txt")]
    [InlineData("名前.png")]
    public void Any_name_round_trips(string name)
    {
        var id = AttachmentItemId.Of(name, 3, 2);

        AttachmentItemId.Parse(id.ToAttachmentId()).Value.ShouldBe(id);
        id.ToString().ShouldStartWith("a.");
        id.ToString().ShouldNotContain("=");
    }

    [Fact]
    public void A_part_without_a_name_has_the_empty_name()
    {
        AttachmentItemId.Of(null, 1, 0).ShouldBe(AttachmentItemId.Of(string.Empty, 1, 0));
    }

    [Theory]
    [InlineData("4")]
    [InlineData("a.")]
    [InlineData("e.WzAsMTQsIm4iXQ")]
    [InlineData("a.WzAsMTQsIm4iXQ==")]
    [InlineData("a.not base64")]
    [InlineData("a.e30")]
    public void Malformed_ids_are_400(string value)
    {
        var error = AttachmentItemId.Parse(new AttachmentId(value)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Code.ShouldBe("ErrorInvalidIdMalformed");
    }

    [Theory]
    [InlineData("""[0,14]""")]
    [InlineData("""[-1,14,"n"]""")]
    [InlineData("""[0,-1,"n"]""")]
    [InlineData("""[0,14,null]""")]
    [InlineData("""[0.5,14,"n"]""")]
    [InlineData("""["0",14,"n"]""")]
    [InlineData("""[0,14,"n",1]""")]
    [InlineData("""{"o":0}""")]
    [InlineData("""[0, 14, "n"]""")]
    public void Only_the_canonical_shape_parses(string json)
    {
        AttachmentItemId.Parse(new AttachmentId(Encode(json))).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void An_escaped_spelling_of_the_same_name_is_not_canonical()
    {
        AttachmentItemId.Parse(new AttachmentId(Encode("[0,14,\"\\u006e\"]"))).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Ordinals_count_equal_name_and_size_in_structure_order_across_nesting()
    {
        var root = Multipart(
            Multipart(Leaf("1", null, 33), Leaf("2", "img.png", 15)),
            Leaf("3", "notes.txt", 14),
            Leaf("4", "notes.txt", 14),
            Leaf("5", "notes.txt", 99),
            new EmailBodyPart { Type = "multipart/alternative", SubParts = [] });

        var ids = AttachmentItemId.Assign(root);

        ids.Keys.ShouldBe(["1", "2", "3", "4", "5"]);
        ids["3"].ShouldBe(AttachmentItemId.Of("notes.txt", 14, 0));
        ids["4"].ShouldBe(AttachmentItemId.Of("notes.txt", 14, 1));
        ids["5"].ShouldBe(AttachmentItemId.Of("notes.txt", 99, 0));
        AttachmentItemId.Of("notes.txt", 14, 1).Find(root)!.PartId.ShouldBe("4");
        AttachmentItemId.Of("notes.txt", 14, 2).Find(root).ShouldBeNull();
    }

    [Fact]
    public void The_id_survives_a_rebuild_that_renumbers_parts_and_changes_blobs()
    {
        var before = Multipart(Leaf("2", null, 33), Leaf("3", "img.png", 15), Leaf("4", "notes.txt", 14), Leaf("5", "q2.csv", 16));
        var after = Multipart(Leaf("1", null, 9), Leaf("2", "notes.txt", 14) with { BlobId = "other" }, Leaf("3", "q2.csv", 16));

        var notes = AttachmentItemId.Assign(before)["4"];

        notes.Find(after)!.PartId.ShouldBe("2");
        AttachmentItemId.Assign(before)["5"].Find(after)!.PartId.ShouldBe("3");
    }

    [Fact]
    public void No_structure_has_no_parts()
    {
        AttachmentItemId.Assign(null).ShouldBeEmpty();
        AttachmentItemId.Of("x", 1, 0).Find(null).ShouldBeNull();
    }
}
