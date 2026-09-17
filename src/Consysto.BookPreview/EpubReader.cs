using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Consysto.BookPreview;

/// <summary>
/// EPUB 2 and 3: container.xml names the package document, whose metadata and manifest describe the book.
/// Series come from calibre's meta tags or EPUB 3 collections.
/// </summary>
internal static partial class EpubReader
{
    private const int MaximumPageBytes = 1 << 20;
    private const int ShortPageTextLength = 300;

    [GeneratedRegex(@"<(?:img|image)\b[^>]*?\b(?:src|href)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"<head\b[\s\S]*?</head>|<[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MarkupAndHead();

    private sealed record ManifestItem(string? Id, string Href, string? MediaType, string? Properties)
    {
        public bool IsImage
            => MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;

        public bool HasProperty(string token)
            => Properties?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(token) == true;
    }

    public static BookInfo Read(string path, bool includeCover)
    {
        using var archive = ZipFile.OpenRead(path);

        var container = LoadXml(FindEntry(archive, "META-INF/container.xml")
            ?? throw new InvalidDataException("The book has no META-INF/container.xml."));
        var packagePath = container.Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value
            ?? throw new InvalidDataException("The container does not name a package document.");
        var package = LoadXml(FindEntry(archive, packagePath)
            ?? throw new InvalidDataException($"The package document '{packagePath}' is missing."));
        var packageDirectory = DirectoryOf(packagePath);

        var info = new BookInfo { Format = BookFormat.Epub };
        var coverId = ReadMetadata(Child(package.Root, "metadata"), info);

        if (includeCover)
        {
            var items = Child(package.Root, "manifest")?.Elements()
                .Where(e => e.Name.LocalName == "item" && e.Attribute("href") is not null)
                .Select(e => new ManifestItem((string?)e.Attribute("id"), (string)e.Attribute("href")!, (string?)e.Attribute("media-type"), (string?)e.Attribute("properties")))
                .ToList() ?? [];

            info.Cover = ReadCover(archive, package, packageDirectory, items, coverId);
        }

        return info;
    }

    /// <summary>Fills the metadata and returns the manifest id named by &lt;meta name="cover"&gt;.</summary>
    private static string? ReadMetadata(XElement? metadata, BookInfo info)
    {
        if (metadata is null)
            return null;

        var elements = metadata.Descendants().ToList();
        var refinements = elements
            .Where(e => e.Name.LocalName == "meta" && e.Attribute("refines") is not null)
            .ToLookup(e => e.Attribute("refines")!.Value.TrimStart('#'));

        string? Refinement(XElement element, string property)
            => element.Attribute("id")?.Value is { } id
                ? refinements[id].FirstOrDefault(r => (string?)r.Attribute("property") == property)?.Value.Trim()
                : null;

        string? coverId = null;
        foreach (var element in elements)
        {
            var value = element.Value;
            switch (element.Name.LocalName)
            {
                case "title":
                    info.Title ??= TextCleanup.Clean(value);
                    break;

                case "creator" or "contributor":
                    var role = AnyAttribute(element, "role") ?? Refinement(element, "role");
                    if (role == "trl")
                        TextCleanup.AddTo(info.Translators, TextCleanup.Clean(value));
                    else if (element.Name.LocalName == "creator" && role is null or "aut")
                        TextCleanup.AddTo(info.Authors, TextCleanup.Clean(value));
                    break;

                case "description":
                    info.Annotation ??= TextCleanup.FromHtml(value);
                    break;
                case "language":
                    info.Language ??= TextCleanup.Clean(value);
                    break;
                case "publisher":
                    info.Publisher ??= TextCleanup.Clean(value);
                    break;
                case "date":
                    info.Year ??= TextCleanup.Year(value);
                    break;
                case "subject":
                    TextCleanup.AddTo(info.Genres, TextCleanup.Clean(value));
                    break;

                case "identifier":
                    var scheme = AnyAttribute(element, "scheme");
                    if (string.Equals(scheme, "ISBN", StringComparison.OrdinalIgnoreCase) || value.Contains("isbn", StringComparison.OrdinalIgnoreCase))
                        info.Isbn ??= TextCleanup.Isbn(value);
                    break;

                case "meta":
                    var name = (string?)element.Attribute("name");
                    var content = (string?)element.Attribute("content");
                    if (name == "cover")
                        coverId ??= content;
                    else if (name == "calibre:series")
                        info.Series ??= TextCleanup.Clean(content);
                    else if (name == "calibre:series_index")
                        info.SeriesIndex ??= TextCleanup.SeriesNumber(content);
                    else if ((string?)element.Attribute("property") == "belongs-to-collection" && element.Attribute("refines") is null && info.Series is null)
                    {
                        info.Series = TextCleanup.Clean(value);
                        info.SeriesIndex = TextCleanup.SeriesNumber(Refinement(element, "group-position"));
                    }
                    break;
            }
        }

        return coverId;
    }

    private static byte[]? ReadCover(ZipArchive archive, XDocument package, string packageDirectory, List<ManifestItem> items, string? coverId)
    {
        var declared = items.FirstOrDefault(i => i.IsImage && i.HasProperty("cover-image"))
            ?? items.FirstOrDefault(i => i.IsImage && i.Id == coverId)
            ?? items.FirstOrDefault(i => i.IsImage && (Mentions(i.Id, "cover") || Mentions(i.Href, "cover")));

        if (declared is not null && ReadImage(archive, Resolve(packageDirectory, declared.Href)) is { } image)
            return image;

        // No usable cover image: take the picture on the cover page, or on the first page when that page is little more than a picture.
        var coverPage = package.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "reference" && string.Equals((string?)e.Attribute("type"), "cover", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("href")?.Value
            ?? items.FirstOrDefault(i => i.Id == coverId && !i.IsImage)?.Href;

        if (coverPage is not null)
            return ImageOnPage(archive, Resolve(packageDirectory, coverPage), requireShortPage: false);

        var firstId = Child(package.Root, "spine")?.Elements().FirstOrDefault(e => e.Name.LocalName == "itemref")?.Attribute("idref")?.Value;
        var firstPage = items.FirstOrDefault(i => i.Id == firstId);
        return firstPage is null ? null : ImageOnPage(archive, Resolve(packageDirectory, firstPage.Href), requireShortPage: true);
    }

    private static byte[]? ImageOnPage(ZipArchive archive, string pagePath, bool requireShortPage)
    {
        var entry = FindEntry(archive, pagePath);
        if (entry is null || entry.Length > MaximumPageBytes)
            return null;

        string page;
        using (var reader = new StreamReader(entry.Open()))
            page = reader.ReadToEnd();

        if (requireShortPage && MarkupAndHead().Replace(page, "").Trim().Length > ShortPageTextLength)
            return null;

        var match = ImageReference().Match(page);
        return match.Success ? ReadImage(archive, Resolve(DirectoryOf(pagePath), match.Groups[1].Value)) : null;
    }

    private static byte[]? ReadImage(ZipArchive archive, string path)
    {
        var entry = FindEntry(archive, path);
        if (entry is null || entry.Length == 0 || entry.Length > BookReader.MaximumCoverBytes)
            return null;

        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        return BookReader.LooksLikeImage(bytes) ? bytes : null;
    }

    private static XElement? Child(XElement? parent, string localName)
        => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? AnyAttribute(XElement element, string localName)
        => element.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static bool Mentions(string? value, string word)
        => value?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
        => archive.GetEntry(path)
            ?? archive.Entries.FirstOrDefault(e => string.Equals(e.FullName.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase));

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CheckCharacters = false,
        });
        return XDocument.Load(reader);
    }

    private static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..(slash + 1)];
    }

    /// <summary>Resolves a manifest or page link against the directory of the document that contains it.</summary>
    private static string Resolve(string directory, string href)
    {
        var fragment = href.IndexOf('#');
        if (fragment >= 0)
            href = href[..fragment];

        href = Uri.UnescapeDataString(href);
        var combined = href.StartsWith('/') ? href : directory + href;

        var parts = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            if (segment is "" or ".")
                continue;

            if (segment == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(segment);
            }
        }

        return string.Join('/', parts);
    }
}
