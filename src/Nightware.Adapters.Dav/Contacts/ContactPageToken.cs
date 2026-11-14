using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace Nightware.Adapters.Dav.Contacts;

/// <summary>
/// The <c>$skiptoken</c> of a contacts page: the offset into the mailbox's cards ordered by
/// (address book href, card href). Versioned so the encoding can change without misreading old links.
/// </summary>
internal static class ContactPageToken
{
    private const string Prefix = "c1:";

    public static string Encode(int offset) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Prefix + offset.ToString(CultureInfo.InvariantCulture)));

    public static bool TryDecode(string token, out int offset)
    {
        offset = 0;
        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
        }
        catch (FormatException)
        {
            return false;
        }

        return text.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out offset);
    }
}
