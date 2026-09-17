using System.Text;

namespace Consysto.MediaPreview;

/// <summary>Reading numbers and text from file headers.</summary>
internal static class Binary
{
    static Binary() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static ushort UInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian ? (ushort)(data[offset] << 8 | data[offset + 1]) : (ushort)(data[offset + 1] << 8 | data[offset]);

    public static uint UInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
        => bigEndian
            ? (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3])
            : (uint)(data[offset + 3] << 24 | data[offset + 2] << 16 | data[offset + 1] << 8 | data[offset]);

    public static ulong UInt64BigEndian(ReadOnlySpan<byte> data, int offset)
        => (ulong)UInt32(data, offset, true) << 32 | UInt32(data, offset + 4, true);

    public static bool Matches(ReadOnlySpan<byte> data, int offset, ReadOnlySpan<byte> expected)
        => offset >= 0 && data.Length >= offset + expected.Length && data.Slice(offset, expected.Length).SequenceEqual(expected);

    /// <summary>Reads up to <paramref name="count"/> bytes; fewer at the end of the stream.</summary>
    public static byte[] Read(Stream stream, int count)
    {
        var buffer = new byte[count];
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, total, count - total);
            if (read == 0)
                break;

            total += read;
        }

        return total == count ? buffer : buffer[..total];
    }

    /// <summary>
    /// Single-byte text: tags that claim ISO-8859-1 are very often Windows-1251 in Russian collections. Bytes that only make
    /// sense as Cyrillic letters are read as Windows-1251.
    /// </summary>
    public static string SingleByte(ReadOnlySpan<byte> data)
    {
        var highBytes = 0;
        var cyrillicLike = 0;
        foreach (var value in data)
        {
            if (value < 0x80)
                continue;

            highBytes++;
            if (value >= 0xC0 || value is 0xA8 or 0xB8)
                cyrillicLike++;
        }

        var encoding = highBytes > 0 && cyrillicLike * 10 >= highBytes * 8
            ? Encoding.GetEncoding(1251)
            : Encoding.Latin1;
        return encoding.GetString(data);
    }

    public static string? Clean(string? text)
    {
        if (text is null)
            return null;

        var cleaned = text.Replace('\0', ' ').Trim();
        return cleaned.Length > 0 ? cleaned : null;
    }

    public static bool LooksLikeImage(ReadOnlySpan<byte> data)
        => data.Length > 12 && (
            data[0] == 0xFF && data[1] == 0xD8 ||
            Matches(data, 0, [0x89, 0x50, 0x4E, 0x47]) ||
            Matches(data, 0, "GIF8"u8) ||
            Matches(data, 0, "BM"u8) ||
            Matches(data, 0, "RIFF"u8) && Matches(data, 8, "WEBP"u8));
}
