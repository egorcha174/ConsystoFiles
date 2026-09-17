namespace Consysto.BookPreview.Opds;

/// <summary>
/// A downloadable book format. <see cref="Rank"/> orders the choices: FictionBook first, then EPUB,
/// then the rest.
/// </summary>
public sealed record OpdsBookFormat(string Name, string Extension, int Rank)
{
    private static readonly OpdsBookFormat Fb2Zip = new("FB2", ".fb2.zip", 0);
    private static readonly OpdsBookFormat Fb2 = new("FB2", ".fb2", 1);
    private static readonly OpdsBookFormat Epub = new("EPUB", ".epub", 2);
    private static readonly OpdsBookFormat Azw3 = new("AZW3", ".azw3", 3);
    private static readonly OpdsBookFormat Mobi = new("MOBI", ".mobi", 4);
    private static readonly OpdsBookFormat Pdf = new("PDF", ".pdf", 5);
    private static readonly OpdsBookFormat Djvu = new("DJVU", ".djvu", 6);
    private static readonly OpdsBookFormat Rtf = new("RTF", ".rtf", 7);
    private static readonly OpdsBookFormat Txt = new("TXT", ".txt", 8);
    private static readonly OpdsBookFormat Html = new("HTML", ".html", 9);
    private static readonly OpdsBookFormat Cbz = new("CBZ", ".cbz", 10);
    private static readonly OpdsBookFormat Zip = new("ZIP", ".zip", 11);

    private static readonly Dictionary<string, OpdsBookFormat> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/fb2+zip"] = Fb2Zip,
        ["application/x-zip-compressed-fb2"] = Fb2Zip,
        ["application/fb2.zip"] = Fb2Zip,
        ["application/x-fictionbook+xml"] = Fb2,
        ["application/x-fictionbook"] = Fb2,
        ["application/fb2+xml"] = Fb2,
        ["application/fb2"] = Fb2,
        ["text/fb2+xml"] = Fb2,
        ["application/epub+zip"] = Epub,
        ["application/x-mobi8-ebook"] = Azw3,
        ["application/vnd.amazon.ebook"] = Azw3,
        ["application/x-mobipocket-ebook"] = Mobi,
        ["application/pdf"] = Pdf,
        ["image/vnd.djvu"] = Djvu,
        ["image/x-djvu"] = Djvu,
        ["application/rtf"] = Rtf,
        ["text/rtf"] = Rtf,
        ["text/plain"] = Txt,
        ["text/html"] = Html,
        ["application/xhtml+xml"] = Html,
        ["application/vnd.comicbook+zip"] = Cbz,
        ["application/x-cbz"] = Cbz,
        ["application/zip"] = Zip,
    };

    private static readonly Dictionary<string, OpdsBookFormat> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".fb2.zip"] = Fb2Zip,
        [".fb2"] = Fb2,
        [".epub"] = Epub,
        [".azw3"] = Azw3,
        [".mobi"] = Mobi,
        [".pdf"] = Pdf,
        [".djvu"] = Djvu,
        [".rtf"] = Rtf,
        [".txt"] = Txt,
        [".cbz"] = Cbz,
    };

    public static OpdsBookFormat FromMediaType(string? mediaType, Uri? address = null)
    {
        var type = (mediaType ?? string.Empty).Split(';')[0].Trim();
        if (MediaTypes.TryGetValue(type, out var known))
            return known;

        if (address is not null && FromFileName(address.AbsolutePath) is { } byName)
            return byName;

        // Flibusta-style links end with the format name instead of an extension: /b/12345/epub
        if (address is not null && FromFileName("." + address.Segments.LastOrDefault()?.Trim('/')) is { } bySegment)
            return bySegment;

        var name = type.Contains('/') ? type[(type.IndexOf('/') + 1)..] : type;
        return new(name.Length > 0 ? name.ToUpperInvariant() : "?", string.Empty, 100);
    }

    public static OpdsBookFormat? FromFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;

        if (fileName.EndsWith(".fb2.zip", StringComparison.OrdinalIgnoreCase))
            return Fb2Zip;

        return Extensions.TryGetValue(Path.GetExtension(fileName), out var format) ? format : null;
    }
}
