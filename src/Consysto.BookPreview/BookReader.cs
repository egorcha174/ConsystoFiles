using System.Text;

namespace Consysto.BookPreview;

/// <summary>
/// Entry point: metadata and cover of EPUB, FictionBook (.fb2, .fb2.zip) and Kindle (MOBI, AZW3) books,
/// read in managed code without third-party libraries. PDF and DjVu are recognised but left to the host.
/// </summary>
public static class BookReader
{
    internal const int MaximumCoverBytes = 20 * 1024 * 1024;

    // FictionBook files are often windows-1251 or koi8-r, Mobipocket headers cp1252.
    static BookReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static BookFormat? GetFormat(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (path.EndsWith(".fb2.zip", StringComparison.OrdinalIgnoreCase))
            return BookFormat.Fb2;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".fb2" => BookFormat.Fb2,
            ".epub" => BookFormat.Epub,
            ".mobi" => BookFormat.Mobi,
            ".azw3" or ".azw" => BookFormat.Azw3,
            ".pdf" => BookFormat.Pdf,
            ".djvu" or ".djv" => BookFormat.Djvu,
            _ => null,
        };
    }

    /// <summary>Reads a book; returns null for formats the host renders itself (PDF, DjVu) and throws for damaged files.</summary>
    public static BookInfo? Read(string path, bool includeCover = true)
        => GetFormat(path) switch
        {
            BookFormat.Fb2 => Fb2Reader.Read(path, includeCover),
            BookFormat.Epub => EpubReader.Read(path, includeCover),
            BookFormat.Mobi => MobiReader.Read(path, BookFormat.Mobi, includeCover),
            BookFormat.Azw3 => MobiReader.Read(path, BookFormat.Azw3, includeCover),
            _ => null,
        };

    internal static bool LooksLikeImage(ReadOnlySpan<byte> data)
        => data.Length > 12 && (
            data[0] == 0xFF && data[1] == 0xD8 ||
            data[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ||
            data[..4].SequenceEqual("GIF8"u8) ||
            data[..2].SequenceEqual("BM"u8) ||
            data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8));
}
