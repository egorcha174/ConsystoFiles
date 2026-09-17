using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using Consysto.BookPreview.Library;
using Consysto.Collections;

namespace Consysto.BookPreview.Server;

/// <summary>Writes one OPDS 1.2 Atom feed. Addresses are root-relative; readers resolve them against the feed address.</summary>
internal sealed class AtomFeedWriter : IDisposable
{
    public const string NavigationType = "application/atom+xml;profile=opds-catalog;kind=navigation";
    public const string AcquisitionType = "application/atom+xml;profile=opds-catalog;kind=acquisition";

    private const string Atom = "http://www.w3.org/2005/Atom";
    private const string Dc = "http://purl.org/dc/terms/";
    private const string Opds = "http://opds-spec.org/2010/catalog";
    private const string OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";
    private const string IdPrefix = "urn:consysto:files:opds:";

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly MemoryStream stream = new();
    private readonly XmlWriter writer;
    private readonly string updated;

    public AtomFeedWriter(string self, string title, string selfType, string? up, DateTime? updated)
    {
        this.updated = Timestamp(updated ?? DateTime.UtcNow);
        writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        writer.WriteStartDocument();
        writer.WriteStartElement("feed", Atom);
        writer.WriteAttributeString("xmlns", "dc", null, Dc);
        writer.WriteAttributeString("xmlns", "opds", null, Opds);
        writer.WriteAttributeString("xmlns", "opensearch", null, OpenSearch);

        Element("id", IdPrefix + self);
        Element("title", title);
        Element("updated", this.updated);
        writer.WriteStartElement("author", Atom);
        Element("name", "Consysto Files");
        writer.WriteEndElement();

        Link("self", self, selfType);
        Link("start", "/opds", NavigationType);
        if (up is not null)
            Link("up", up, NavigationType);

        Link("search", "/opds/opensearch.xml", "application/opensearchdescription+xml");
        Link("search", "/opds/search?q={searchTerms}", AcquisitionType);
    }

    public void Paging(int total, int pageSize, int page, int pageCount, Func<int, string> address)
    {
        writer.WriteElementString("opensearch", "totalResults", OpenSearch, total.ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString("opensearch", "itemsPerPage", OpenSearch, pageSize.ToString(CultureInfo.InvariantCulture));
        writer.WriteElementString("opensearch", "startIndex", OpenSearch, ((page - 1) * pageSize + 1).ToString(CultureInfo.InvariantCulture));
        if (pageCount <= 1)
            return;

        Link("first", address(1), AcquisitionType);
        if (page > 1)
            Link("previous", address(page - 1), AcquisitionType);
        if (page < pageCount)
            Link("next", address(page + 1), AcquisitionType);

        Link("last", address(pageCount), AcquisitionType);
    }

    public void Navigation(string id, string title, string href, string? content, string type)
    {
        writer.WriteStartElement("entry", Atom);
        Element("title", title);
        Element("id", IdPrefix + id);
        Element("updated", updated);
        if (!string.IsNullOrEmpty(content))
        {
            writer.WriteStartElement("content", Atom);
            writer.WriteAttributeString("type", "text");
            writer.WriteString(content);
            writer.WriteEndElement();
        }

        Link("subsection", href, type);
        writer.WriteEndElement();
    }

    public void Book(CollectionItem book)
    {
        var format = BookMediaTypes.Of(book);

        writer.WriteStartElement("entry", Atom);
        Element("title", book.Title);
        Element("id", "urn:consysto:files:book:" + book.Id);
        Element("updated", Timestamp(book.Modified));

        foreach (var author in book.Authors)
        {
            writer.WriteStartElement("author", Atom);
            Element("name", author);
            Element("uri", AuthorAddress(author));
            writer.WriteEndElement();
        }

        if (book.Language is not null)
            writer.WriteElementString("dc", "language", Dc, book.Language);
        if (book.Year is not null)
            writer.WriteElementString("dc", "issued", Dc, book.Year);
        if (book.Publisher is not null)
            writer.WriteElementString("dc", "publisher", Dc, book.Publisher);

        foreach (var genre in book.Genres)
        {
            writer.WriteStartElement("category", Atom);
            writer.WriteAttributeString("term", genre);
            writer.WriteAttributeString("label", genre);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("content", Atom);
        writer.WriteAttributeString("type", "html");
        writer.WriteString(Details(book, format.Name));
        writer.WriteEndElement();

        if (book.HasCover)
        {
            Link("http://opds-spec.org/image", $"/cover/{book.Id}.jpg", "image/jpeg");
            Link("http://opds-spec.org/image/thumbnail", $"/thumb/{book.Id}.jpg", "image/jpeg");
        }

        writer.WriteStartElement("link", Atom);
        writer.WriteAttributeString("rel", "http://opds-spec.org/acquisition");
        writer.WriteAttributeString("href", $"/book/{book.Id}/{Uri.EscapeDataString(CatalogBuilder.DownloadName(book))}");
        writer.WriteAttributeString("type", format.MediaType);
        writer.WriteAttributeString("title", format.Name);
        writer.WriteAttributeString("length", book.Size.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();

        foreach (var author in book.Authors)
            Link("related", AuthorAddress(author), AcquisitionType, $"Все книги автора: {author}");

        if (book.Series is { } series)
            Link("related", CatalogBuilder.Href("/opds/series-books", ("name", CollectionText.Normalize(series))), AcquisitionType, $"Все книги серии: {series}");

        writer.WriteEndElement();
    }

    public CatalogPage Finish(string mediaType)
    {
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
        return new(stream.ToArray(), mediaType);
    }

    public void Dispose()
    {
        writer.Dispose();
        stream.Dispose();
    }

    private static string AuthorAddress(string author)
        => CatalogBuilder.Href("/opds/author", ("name", CollectionText.Normalize(CollectionText.SurnameFirst(author))));

    /// <summary>Series, year, genres, format and the annotation, as escaped HTML: readers show text content on one line.</summary>
    private static string Details(CollectionItem book, string formatName)
    {
        var lines = new List<string>();
        if (book.Series is not null)
            lines.Add($"Серия: {book.Series}{(book.SeriesIndex is null ? string.Empty : " #" + book.SeriesIndex)}");
        if (book.Year is not null)
            lines.Add($"Год: {book.Year}");
        if (book.Genres.Count > 0)
            lines.Add($"Жанр: {string.Join(", ", book.Genres)}");

        lines.Add($"{formatName}, {FormatSize(book.Size)}");

        var html = new StringBuilder("<p>");
        html.AppendJoin("<br/>", lines.Select(WebUtility.HtmlEncode));
        html.Append("</p>");
        if (book.Annotation is { } annotation)
        {
            foreach (var paragraph in annotation.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                html.Append("<p>").Append(WebUtility.HtmlEncode(paragraph)).Append("</p>");
        }

        return html.ToString();
    }

    private static string FormatSize(long bytes)
        => bytes < 1024 * 1024
            ? string.Format(Russian, "{0:0} КБ", Math.Max(1, bytes / 1024.0))
            : string.Format(Russian, "{0:0.0} МБ", bytes / (1024.0 * 1024.0));

    private static string Timestamp(DateTime time)
        => (time.Kind == DateTimeKind.Local ? time.ToUniversalTime() : time).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private void Element(string name, string value)
        => writer.WriteElementString(name, Atom, value);

    private void Link(string relation, string href, string type, string? title = null)
    {
        writer.WriteStartElement("link", Atom);
        writer.WriteAttributeString("rel", relation);
        writer.WriteAttributeString("href", href);
        writer.WriteAttributeString("type", type);
        if (title is not null)
            writer.WriteAttributeString("title", title);

        writer.WriteEndElement();
    }
}

internal static class BookMediaTypes
{
    public static (string Name, string MediaType) Of(CollectionItem book)
        => book.Extension switch
        {
            ".fb2.zip" => ("FB2.ZIP", "application/fb2+zip"),
            ".fb2" => ("FB2", "application/x-fictionbook+xml"),
            ".epub" => ("EPUB", "application/epub+zip"),
            ".mobi" => ("MOBI", "application/x-mobipocket-ebook"),
            ".azw3" => ("AZW3", "application/x-mobi8-ebook"),
            ".azw" => ("AZW", "application/vnd.amazon.ebook"),
            ".pdf" => ("PDF", "application/pdf"),
            ".djvu" or ".djv" => ("DJVU", "image/vnd.djvu"),
            _ => (book.Extension.TrimStart('.').ToUpperInvariant(), "application/octet-stream"),
        };

    public static string OfImage(byte[] image)
        => image switch
        {
            [0xFF, 0xD8, ..] => "image/jpeg",
            [0x89, 0x50, ..] => "image/png",
            [0x47, 0x49, ..] => "image/gif",
            [0x42, 0x4D, ..] => "image/bmp",
            _ => "image/webp",
        };
}
