using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Consysto.BookPreview;

/// <summary>
/// FictionBook 2: the description is read as a stream and the body skipped, so a large book costs
/// little more than its header. The cover is a base64 &lt;binary&gt; after the body.
/// </summary>
internal static class Fb2Reader
{
    private const string XLinkNamespace = "http://www.w3.org/1999/xlink";

    public static BookInfo Read(string path, bool includeCover)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".fb2", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The archive does not contain a FictionBook file.");

            using var entryStream = entry.Open();
            return Read(entryStream, includeCover);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Read(stream, includeCover);
    }

    private static BookInfo Read(Stream stream, bool includeCover)
    {
        var info = new BookInfo { Format = BookFormat.Fb2 };
        string? coverId = null;
        string? writtenYear = null;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            CheckCharacters = false,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var reader = XmlReader.Create(stream, settings);
            reader.MoveToContent();

            while (!reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                switch (reader.LocalName)
                {
                    case "title-info":
                        (coverId, writtenYear) = ReadTitleInfo(reader, info);
                        reader.Read();
                        break;

                    case "publish-info":
                        ReadPublishInfo(reader, info);
                        reader.Read();
                        break;

                    case "body":
                        if (!includeCover || coverId is null)
                            return Finish(info, writtenYear);

                        reader.Skip();
                        break;

                    case "binary":
                        if (!includeCover || coverId is null)
                            return Finish(info, writtenYear);

                        if (string.Equals(reader.GetAttribute("id"), coverId, StringComparison.Ordinal))
                        {
                            info.Cover = ReadBase64(reader);
                            return Finish(info, writtenYear);
                        }

                        reader.Skip();
                        break;

                    case "src-title-info" or "document-info" or "custom-info" or "stylesheet":
                        reader.Skip();
                        break;

                    default:
                        reader.Read();
                        break;
                }
            }
        }
        catch (XmlException) when (info.Title is not null || info.Authors.Count > 0)
        {
            // Many FictionBook files are not well-formed somewhere in the text; what was read before that is kept.
        }

        return Finish(info, writtenYear);
    }

    private static BookInfo Finish(BookInfo info, string? writtenYear)
    {
        // The edition year wins over the year the text was written.
        info.Year ??= writtenYear;
        return info;
    }

    private static (string? CoverId, string? Year) ReadTitleInfo(XmlReader reader, BookInfo info)
    {
        string? coverId = null;
        string? year = null;

        using var section = reader.ReadSubtree();
        while (section.Read())
        {
            if (section.NodeType != XmlNodeType.Element)
                continue;

            switch (section.LocalName)
            {
                case "genre":
                    TextCleanup.AddTo(info.Genres, Fb2Genres.Describe(ReadText(section)));
                    break;
                case "author":
                    TextCleanup.AddTo(info.Authors, ReadPerson(section));
                    break;
                case "translator":
                    TextCleanup.AddTo(info.Translators, ReadPerson(section));
                    break;
                case "book-title":
                    info.Title ??= TextCleanup.Clean(ReadText(section));
                    break;
                case "annotation":
                    info.Annotation ??= ReadParagraphs(section);
                    break;
                case "date":
                    year ??= TextCleanup.Year(section.GetAttribute("value")) ?? TextCleanup.Year(ReadText(section));
                    break;
                case "coverpage":
                    coverId ??= ReadCoverId(section);
                    break;
                case "lang":
                    info.Language ??= TextCleanup.Clean(ReadText(section));
                    break;
                case "sequence":
                    ReadSequence(section, info);
                    break;
            }
        }

        return (coverId, year);
    }

    private static void ReadPublishInfo(XmlReader reader, BookInfo info)
    {
        using var section = reader.ReadSubtree();
        while (section.Read())
        {
            if (section.NodeType != XmlNodeType.Element)
                continue;

            switch (section.LocalName)
            {
                case "publisher":
                    info.Publisher ??= TextCleanup.Clean(ReadText(section));
                    break;
                case "year":
                    info.Year ??= TextCleanup.Year(ReadText(section));
                    break;
                case "isbn":
                    info.Isbn ??= TextCleanup.Isbn(ReadText(section));
                    break;
                case "sequence":
                    ReadSequence(section, info);
                    break;
            }
        }
    }

    private static void ReadSequence(XmlReader reader, BookInfo info)
    {
        if (info.Series is not null)
            return;

        info.Series = TextCleanup.Clean(reader.GetAttribute("name"));
        if (info.Series is not null)
            info.SeriesIndex = TextCleanup.SeriesNumber(reader.GetAttribute("number"));
    }

    /// <summary>Concatenated text of the current element; leaves the reader on its end tag.</summary>
    private static string ReadText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return "";

        var text = new StringBuilder();
        using var element = reader.ReadSubtree();
        while (element.Read())
        {
            if (element.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
                text.Append(element.Value);
        }

        return text.ToString();
    }

    private static string? ReadPerson(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return null;

        string? first = null, last = null, nickname = null;
        using var person = reader.ReadSubtree();
        while (person.Read())
        {
            if (person.NodeType != XmlNodeType.Element)
                continue;

            switch (person.LocalName)
            {
                case "first-name":
                    first ??= TextCleanup.Clean(ReadText(person));
                    break;
                case "last-name":
                    last ??= TextCleanup.Clean(ReadText(person));
                    break;
                case "nickname":
                    nickname ??= TextCleanup.Clean(ReadText(person));
                    break;
            }
        }

        var name = string.Join(' ', new[] { first, last }.Where(part => part is not null));
        return name.Length > 0 ? name : nickname;
    }

    private static string? ReadParagraphs(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return null;

        var text = new StringBuilder();
        using var annotation = reader.ReadSubtree();
        while (annotation.Read())
        {
            switch (annotation.NodeType)
            {
                case XmlNodeType.Element when annotation.LocalName is "empty-line":
                    text.Append("\n\n");
                    break;
                case XmlNodeType.EndElement when annotation.LocalName is "p" or "v" or "subtitle" or "text-author":
                    text.Append('\n');
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace:
                    text.Append(annotation.Value);
                    break;
            }
        }

        return TextCleanup.Paragraphs(text.ToString());
    }

    private static string? ReadCoverId(XmlReader reader)
    {
        if (reader.IsEmptyElement)
            return null;

        using var coverpage = reader.ReadSubtree();
        while (coverpage.Read())
        {
            if (coverpage.NodeType != XmlNodeType.Element || coverpage.LocalName != "image")
                continue;

            var href = coverpage.GetAttribute("href", XLinkNamespace);
            if (href is null && coverpage.MoveToFirstAttribute())
            {
                // Some tools write the link without declaring the xlink namespace.
                do
                {
                    if (coverpage.LocalName == "href")
                    {
                        href = coverpage.Value;
                        break;
                    }
                }
                while (coverpage.MoveToNextAttribute());

                coverpage.MoveToElement();
            }

            if (!string.IsNullOrWhiteSpace(href))
                return href.Trim().TrimStart('#');
        }

        return null;
    }

    private static byte[]? ReadBase64(XmlReader reader)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[1 << 16];
        int read;
        while ((read = reader.ReadElementContentAsBase64(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > BookReader.MaximumCoverBytes)
                return null;
        }

        return BookReader.LooksLikeImage(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)) ? buffer.ToArray() : null;
    }
}
