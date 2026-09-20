using System.Buffers.Binary;
using System.IO.Compression;
using OpenMcdf;

namespace Consysto.CadPreview.SolidWorks;

/// <summary>
/// Reads the picture SolidWorks saves inside a document, so a part, an assembly or a drawing can be shown without
/// SolidWorks installed.
///
/// There are two kinds of file. Documents up to about 2012 are OLE compound files, like Inventor's, and keep the picture
/// in a stream named PreviewPNG. Newer ones use SolidWorks' own container: a chain of compressed blocks, each with its
/// own checksum. The picture is one of those blocks.
/// </summary>
public static class SolidWorksPreviewReader
{
    private static readonly string[] SupportedExtensions = [".sldprt", ".sldasm", ".slddrw"];

    /// <summary>Start of a block header in the newer container.</summary>
    private static ReadOnlySpan<byte> BlockMarker => [0x14, 0x00, 0x06, 0x00, 0x08, 0x00];

    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private const string LegacyStreamName = "PreviewPNG";
    private const long MaximumFileBytes = 512 * 1024 * 1024;
    private const int MaximumBlockBytes = 32 * 1024 * 1024;
    private const int BlockHeaderBytes = 26;

    public static bool IsSupported(string? extension)
        => extension is not null && SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <returns>A complete PNG file, or null when the document has no picture or cannot be read.</returns>
    public static byte[]? TryRead(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumFileBytes)
                return null;

            return TryReadLegacy(path) ?? TryReadBlocks(path);
        }
        catch (Exception)
        {
            // A file being written, a format we do not know, a damaged document: the preview is simply absent
            return null;
        }
    }

    /// <summary>Documents of the older kind: an OLE compound file with a PreviewPNG stream.</summary>
    private static byte[]? TryReadLegacy(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var root = RootStorage.Open(file);
            if (!root.TryOpenStream(LegacyStreamName, out var stream))
                return null;

            using (stream)
            {
                if (stream.Length is 0 or > MaximumBlockBytes)
                    return null;

                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                return ExtractPng(data);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Documents of the newer kind. Blocks are found by their marker rather than by walking a table of contents: the
    /// table's layout varies between versions, while a block always carries its own sizes and checksum, and a block whose
    /// checksum does not match is skipped.
    /// </summary>
    private static byte[]? TryReadBlocks(string path)
    {
        var data = File.ReadAllBytes(path);
        var from = 0;

        while (from < data.Length)
        {
            var marker = data.AsSpan(from).IndexOf(BlockMarker);
            if (marker < 0)
                return null;

            var start = from + marker;
            from = start + 1;

            if (start + BlockHeaderBytes > data.Length)
                continue;

            var header = data.AsSpan(start + 6);
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            var compressedBytes = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
            var plainBytes = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
            var preambleBytes = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);

            if (compressedBytes is <= 0 or > MaximumBlockBytes || plainBytes is <= 0 or > MaximumBlockBytes || preambleBytes is < 0 or > 4096)
                continue;

            var compressedStart = start + BlockHeaderBytes + preambleBytes;
            if (compressedStart + compressedBytes > data.Length)
                continue;

            // Only a block that starts as a PNG once unpacked is worth unpacking twice, so the cheap check comes first:
            // raw deflate of a PNG keeps nothing recognisable, so the unpacking itself has to answer.
            var plain = TryInflate(data.AsSpan(compressedStart, compressedBytes), plainBytes);
            if (plain is null || Crc32(plain) != checksum)
                continue;

            if (ExtractPng(plain) is { } png)
                return png;
        }

        return null;
    }

    private static byte[]? TryInflate(ReadOnlySpan<byte> compressed, int plainBytes)
    {
        try
        {
            var plain = new byte[plainBytes];
            using var source = new MemoryStream(compressed.ToArray(), writable: false);
            using var inflate = new DeflateStream(source, CompressionMode.Decompress);
            inflate.ReadExactly(plain);

            return plain;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// The checksum a block carries. Written out here rather than taken from a package: one short loop weighs less than
    /// another dependency in a program that is compiled ahead of time.
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
        }

        return ~crc;
    }

    /// <summary>Cuts a whole PNG out of a stream that may carry other things after it.</summary>
    internal static byte[]? ExtractPng(byte[] data)
    {
        var start = data.AsSpan().IndexOf(PngSignature);
        if (start < 0)
            return null;

        // Walk the chunks to the end marker; a picture cut short would not open anywhere
        var position = start + PngSignature.Length;
        while (position + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position));
            if (length > int.MaxValue - 12)
                return null;

            var end = position + 12 + (int)length;
            if (end > data.Length)
                return null;

            if (data.AsSpan(position + 4, 4).SequenceEqual("IEND"u8))
                return data[start..end];

            position = end;
        }

        return null;
    }
}
