using System.IO.Compression;

namespace Consysto.CadPreview.Kompas;

/// <summary>
/// Reads the picture KOMPAS-3D saves inside a document, so a part, an assembly or a drawing can be shown without KOMPAS
/// installed.
///
/// A KOMPAS document is an ordinary zip. Inside it, the entry named Preview holds an eighteen-byte header of its own and
/// then a whole TIFF, which may be written either way round — little-endian in newer versions, big-endian in older ones.
/// </summary>
public static class KompasPreviewReader
{
    private static readonly string[] SupportedExtensions = [".m3d", ".a3d", ".cdw", ".frw"];

    private const string PreviewEntryName = "Preview";
    private const long MaximumFileBytes = 512 * 1024 * 1024;
    private const int MaximumPreviewBytes = 32 * 1024 * 1024;

    public static bool IsSupported(string? extension)
        => extension is not null && SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    /// <returns>A complete TIFF file, or null when the document has no picture or cannot be read.</returns>
    public static byte[]? TryRead(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumFileBytes)
                return null;

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var entry = archive.GetEntry(PreviewEntryName);
            if (entry is null || entry.Length is 0 or > MaximumPreviewBytes)
                return null;

            var data = new byte[entry.Length];
            using (var preview = entry.Open())
                preview.ReadExactly(data);

            return ExtractTiff(data);
        }
        catch (Exception)
        {
            // Not a zip, a document being written, a damaged file: the preview is simply absent
            return null;
        }
    }

    /// <summary>Cuts the TIFF out of the entry, skipping whatever header KOMPAS put in front of it.</summary>
    internal static byte[]? ExtractTiff(byte[] data)
    {
        var start = IndexOfTiff(data);
        return start < 0 ? null : data[start..];
    }

    private static int IndexOfTiff(byte[] data)
    {
        ReadOnlySpan<byte> little = [(byte)'I', (byte)'I', 0x2A, 0x00];
        ReadOnlySpan<byte> big = [(byte)'M', (byte)'M', 0x00, 0x2A];

        var span = data.AsSpan();
        var first = span.IndexOf(little);
        var second = span.IndexOf(big);

        return first < 0 ? second
            : second < 0 ? first
            : Math.Min(first, second);
    }
}
