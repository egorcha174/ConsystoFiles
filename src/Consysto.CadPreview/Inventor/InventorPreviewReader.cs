using System.Buffers.Binary;
using OpenMcdf;

namespace Consysto.CadPreview.Inventor;

/// <summary>Reads the 512×512 PNG Inventor stores inside every saved ipt/iam/idw/ipn, so no Inventor is needed to show it.</summary>
public static class InventorPreviewReader
{
    // Property-set stream of an Inventor document (the name starts with U+0005); the preview PNG sits in the middle of it.
    private static readonly string PreviewStreamName = (char)5 + "Zrxrt4arFafyu34gYa3l3ohgHg";
    private const long MaximumStreamBytes = 64 * 1024 * 1024;

    private static readonly string[] SupportedExtensions = [".ipt", ".iam", ".idw", ".ipn"];

    public static bool IsSupported(string? extension)
        => extension is not null && SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <returns>A complete PNG file, or null when the document has no preview or cannot be opened.</returns>
    public static byte[]? TryRead(string path)
    {
        try
        {
            // Inventor keeps the documents it has open locked for writing; sharing ReadWrite still lets us read them.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var root = RootStorage.Open(file);
            if (!root.TryOpenStream(PreviewStreamName, out var stream))
                return null;

            using (stream)
            {
                if (stream.Length > MaximumStreamBytes)
                    return null;

                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                return ExtractPng(data);
            }
        }
        catch (Exception)
        {
            // A preview is a best-effort extra: a damaged or foreign compound file simply has none.
            return null;
        }
    }

    /// <summary>Cuts the PNG out of the surrounding property data: from the signature through the IEND chunk.</summary>
    internal static byte[]? ExtractPng(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        int start = data.IndexOf(signature);
        if (start < 0)
            return null;

        // Each chunk: 4-byte big-endian length, 4-byte type, data, 4-byte CRC.
        int position = start + signature.Length;
        while (position + 12 <= data.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            if (length > (uint)(data.Length - position - 12))
                return null;

            bool isEnd = data.Slice(position + 4, 4).SequenceEqual("IEND"u8);
            position += 12 + (int)length;
            if (isEnd)
                return data[start..position].ToArray();
        }

        return null;
    }
}
