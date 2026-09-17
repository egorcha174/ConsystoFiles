using System.Xml.Linq;

namespace Consysto.BookPreview.Opds;

/// <summary>OPDS 1.x: Atom feeds and entry documents. Elements are matched by local name, since catalogs are careless with namespaces.</summary>
internal static class OpdsAtomParser
{
    private const string AcquisitionRelation = "http://opds-spec.org/acquisition";

    // Links of these relations lead into the entry itself; any other catalog link of an entry is "related"
    private static readonly HashSet<string> NavigationRelations = new(StringComparer.Ordinal)
    {
        string.Empty,
        "subsection",
        "collection",
        "alternate",
        "http://opds-spec.org/sort/new",
        "http://opds-spec.org/sort/popular",
        "http://opds-spec.org/featured",
        "http://opds-spec.org/recommended",
        "http://opds-spec.org/crawlable",
        "http://opds-spec.org/shelf",
        "http://opds-spec.org/subscriptions",
    };

    public static OpdsFeed Parse(XDocument document, Uri address)
    {
        var root = document.Root ?? throw new InvalidDataException("The catalog page is empty.");
        var feed = new OpdsFeed { Address = address };

        if (root.Name.LocalName == "entry")
        {
            feed.Title = TextCleanup.Clean(Text(root, "title"));
            feed.Entries.Add(ParseEntry(root, address));
            return feed;
        }

        if (root.Name.LocalName != "feed")
            throw new InvalidDataException($"The page is not an OPDS catalog (its root element is <{root.Name.LocalName}>).");

        feed.Title = TextCleanup.Clean(Text(root, "title"));
        feed.Subtitle = TextCleanup.Clean(Text(root, "subtitle"));
        feed.Icon = OpdsUri.ResolveImage(address, Text(root, "icon") ?? Text(root, "logo"));

        foreach (var link in Children(root, "link"))
        {
            var href = (string?)link.Attribute("href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var type = (string?)link.Attribute("type") ?? string.Empty;
            switch ((string?)link.Attribute("rel"))
            {
                case "next":
                    feed.NextPage ??= OpdsUri.Resolve(address, href);
                    break;
                case "previous" or "prev":
                    feed.PreviousPage ??= OpdsUri.Resolve(address, href);
                    break;
                case "start":
                    feed.StartPage ??= OpdsUri.Resolve(address, href);
                    break;
                case "up":
                    feed.UpPage ??= OpdsUri.Resolve(address, href);
                    break;
                case "search":
                    if (href.Contains(OpdsSearch.Placeholder, StringComparison.Ordinal))
                        feed.SearchTemplate ??= OpdsSearch.ResolveTemplate(address, href);
                    else if (type.Contains("opensearchdescription", StringComparison.OrdinalIgnoreCase))
                        feed.SearchDescription ??= OpdsUri.Resolve(address, href);
                    break;
            }
        }

        foreach (var entry in Children(root, "entry"))
            feed.Entries.Add(ParseEntry(entry, address));

        return feed;
    }

    private static OpdsEntry ParseEntry(XElement element, Uri address)
    {
        var summary = ReadContent(Children(element, "summary").FirstOrDefault());
        var content = ReadContent(Children(element, "content").FirstOrDefault());

        var entry = new OpdsEntry
        {
            Id = TextCleanup.Clean(Text(element, "id")),
            Title = TextCleanup.Clean(Text(element, "title")),
            Summary = (content?.Length ?? 0) > (summary?.Length ?? 0) ? content : summary,
        };

        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "author":
                    TextCleanup.AddTo(entry.Authors, TextCleanup.Clean(Text(child, "name") ?? child.Value));
                    break;
                case "language":
                    entry.Language ??= TextCleanup.Clean(child.Value);
                    break;
                case "issued" or "published" or "date":
                    entry.Year ??= TextCleanup.Year(child.Value);
                    break;
                case "publisher":
                    entry.Publisher ??= TextCleanup.Clean(child.Value);
                    break;
                case "category":
                    TextCleanup.AddTo(entry.Categories, TextCleanup.Clean((string?)child.Attribute("label") ?? (string?)child.Attribute("term")));
                    break;
                case "link":
                    ReadLink(child, entry, address);
                    break;
            }
        }

        entry.Thumbnail ??= entry.Image;
        entry.Image ??= entry.Thumbnail;
        return entry;
    }

    private static void ReadLink(XElement link, OpdsEntry entry, Uri address)
    {
        var href = (string?)link.Attribute("href");
        if (string.IsNullOrWhiteSpace(href))
            return;

        var relation = ((string?)link.Attribute("rel"))?.Trim() ?? string.Empty;
        var type = ((string?)link.Attribute("type"))?.Trim() ?? string.Empty;

        if (relation.StartsWith(AcquisitionRelation, StringComparison.Ordinal))
        {
            if (OpdsUri.Resolve(address, href) is { } target)
            {
                // An indirect acquisition (a page or DRM wrapper) names the final book format in nested elements
                var mediaType = link.Descendants().LastOrDefault(e => e.Name.LocalName == "indirectAcquisition")?.Attribute("type")?.Value ?? type;
                var length = long.TryParse((string?)link.Attribute("length"), out var bytes) && bytes > 0 ? bytes : (long?)null;
                entry.Acquisitions.Add(new(target, mediaType, OpdsBookFormat.FromMediaType(mediaType, target), KindOf(relation), TextCleanup.Clean((string?)link.Attribute("title")), length));
            }

            return;
        }

        switch (relation)
        {
            case "http://opds-spec.org/image" or "http://opds-spec.org/cover" or "x-stanza-cover-image":
                entry.Image ??= OpdsUri.ResolveImage(address, href);
                return;
            case "http://opds-spec.org/image/thumbnail" or "http://opds-spec.org/thumbnail" or "x-stanza-cover-image-thumbnail":
                entry.Thumbnail ??= OpdsUri.ResolveImage(address, href);
                return;
        }

        if (!IsCatalogType(type) || relation == "self" || OpdsUri.Resolve(address, href) is not { } catalogPage)
            return;

        if (NavigationRelations.Contains(relation))
            entry.Navigation ??= catalogPage;
        else
            entry.Related.Add(new(TextCleanup.Clean((string?)link.Attribute("title")) ?? relation, catalogPage));
    }

    public static OpdsAcquisitionKind KindOf(string relation)
        => relation switch
        {
            _ when relation.EndsWith("/open-access", StringComparison.Ordinal) => OpdsAcquisitionKind.OpenAccess,
            _ when relation.EndsWith("/sample", StringComparison.Ordinal) || relation.EndsWith("/preview", StringComparison.Ordinal) => OpdsAcquisitionKind.Sample,
            _ when relation.EndsWith("/borrow", StringComparison.Ordinal) => OpdsAcquisitionKind.Borrow,
            _ when relation.EndsWith("/buy", StringComparison.Ordinal) => OpdsAcquisitionKind.Buy,
            _ when relation.EndsWith("/subscribe", StringComparison.Ordinal) => OpdsAcquisitionKind.Subscribe,
            _ => OpdsAcquisitionKind.Download,
        };

    public static bool IsCatalogType(string type)
        => type.Contains("atom+xml", StringComparison.OrdinalIgnoreCase) || type.Contains("opds", StringComparison.OrdinalIgnoreCase);

    private static string? ReadContent(XElement? element)
    {
        if (element is null)
            return null;

        return ((string?)element.Attribute("type"))?.ToLowerInvariant() switch
        {
            "xhtml" => TextCleanup.FromHtml(string.Concat(element.Nodes().Select(node => node.ToString()))),
            "html" or "text/html" => TextCleanup.FromHtml(element.Value),
            _ => TextCleanup.Paragraphs(element.Value),
        };
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName)
        => parent.Elements().Where(e => e.Name.LocalName == localName);

    private static string? Text(XElement parent, string localName)
        => Children(parent, localName).FirstOrDefault()?.Value;
}
