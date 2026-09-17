using System.Globalization;
using System.Text;
using System.Xml;
using Consysto.BookPreview.Library;
using Consysto.Collections;

namespace Consysto.BookPreview.Server;

internal sealed record CatalogPage(byte[] Body, string MediaType);

/// <summary>
/// The pages of the served catalog: start page, newest, authors, series, genres, folders, all books and search.
/// Texts are Russian, the language of the reader on the phone. Long lists are split by first letter, books into pages.
/// </summary>
internal sealed class CatalogBuilder(string title)
{
    private const int PageSize = 50;
    private const int LetterThreshold = 60;

    private static readonly char[] InvalidNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public CatalogPage Root(CollectionSnapshot library)
    {
        using var feed = new AtomFeedWriter("/opds", title, AtomFeedWriter.NavigationType, up: null, library.Scanned);
        feed.Navigation("new", "Новые поступления", "/opds/new", "Недавно добавленные книги", AtomFeedWriter.AcquisitionType);
        feed.Navigation("authors", "Авторы", "/opds/authors", Count(library.Authors.Count, "автор", "автора", "авторов"), AtomFeedWriter.NavigationType);
        feed.Navigation("series", "Серии", "/opds/series", Count(library.Series.Count, "серия", "серии", "серий"), AtomFeedWriter.NavigationType);
        feed.Navigation("genres", "Жанры", "/opds/genres", Count(library.Genres.Count, "жанр", "жанра", "жанров"), AtomFeedWriter.NavigationType);
        feed.Navigation("folders", "Папки", "/opds/folders", string.Join(", ", library.Roots.Select(FolderName)), AtomFeedWriter.NavigationType);
        feed.Navigation("all", "Все книги", "/opds/all", BookCount(library.Items.Count), AtomFeedWriter.AcquisitionType);
        return feed.Finish(AtomFeedWriter.NavigationType);
    }

    /// <param name="address">This list, e.g. "/opds/authors".</param>
    /// <param name="groupAddress">The books of one group, e.g. "/opds/author".</param>
    public CatalogPage Groups(string address, string heading, (string One, string Few, string Many) unit, IReadOnlyList<CollectionGroup> groups, string? letter, string groupAddress)
    {
        var self = letter is null ? address : Href(address, ("letter", letter));
        using var feed = new AtomFeedWriter(self, letter is null ? heading : $"{heading}: {letter}", AtomFeedWriter.NavigationType, letter is null ? "/opds" : address, null);

        if (letter is null && groups.Count > LetterThreshold)
        {
            foreach (var byLetter in groups.GroupBy(group => CollectionText.Letter(group.Key)).OrderBy(byLetter => byLetter.Key, CollectionText.Comparer))
                feed.Navigation($"{address}:{byLetter.Key}", byLetter.Key, Href(address, ("letter", byLetter.Key)), Count(byLetter.Count(), unit.One, unit.Few, unit.Many), AtomFeedWriter.NavigationType);
        }
        else
        {
            foreach (var group in letter is null ? groups : groups.Where(group => CollectionText.Letter(group.Key) == letter))
                feed.Navigation($"{groupAddress}:{group.Key}", group.Name, Href(groupAddress, ("name", group.Key)), BookCount(group.Items.Count), AtomFeedWriter.AcquisitionType);
        }

        return feed.Finish(AtomFeedWriter.NavigationType);
    }

    public CatalogPage Books(string address, (string Name, string Value)[] query, string heading, IReadOnlyList<CollectionItem> books, int page, string up)
    {
        var pageCount = Math.Max(1, (books.Count + PageSize - 1) / PageSize);
        page = Math.Min(page, pageCount);

        using var feed = new AtomFeedWriter(PageAddress(address, query, page), heading, AtomFeedWriter.AcquisitionType, up, null);
        feed.Paging(books.Count, PageSize, page, pageCount, number => PageAddress(address, query, number));
        foreach (var book in books.Skip((page - 1) * PageSize).Take(PageSize))
            feed.Book(book);

        return feed.Finish(AtomFeedWriter.AcquisitionType);
    }

    /// <summary>"0/Фантастика/Лем": the number of the library folder, then the subfolders. Null when there is no such folder.</summary>
    public CatalogPage? Folder(CollectionSnapshot library, string? path, int page)
    {
        if (string.IsNullOrEmpty(path))
        {
            if (library.Roots.Count != 1)
            {
                using var roots = new AtomFeedWriter("/opds/folders", "Папки", AtomFeedWriter.NavigationType, "/opds", library.Scanned);
                for (var index = 0; index < library.Roots.Count; index++)
                    roots.Navigation($"folder:{index}", FolderName(library.Roots[index]), Href("/opds/folders", ("path", index.ToString(CultureInfo.InvariantCulture))), BookCount(library.CountInRoot(index)), AtomFeedWriter.AcquisitionType);

                return roots.Finish(AtomFeedWriter.NavigationType);
            }

            path = "0";
        }

        var slash = path.IndexOf('/');
        if (!int.TryParse(slash < 0 ? path : path[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var rootIndex) || rootIndex >= library.Roots.Count)
            return null;

        var relative = slash < 0 ? string.Empty : path[(slash + 1)..].Trim('/');
        var (folders, books) = library.GetFolder(rootIndex, relative);
        var parentSlash = relative.LastIndexOf('/');
        var up = relative.Length == 0
            ? library.Roots.Count == 1 ? "/opds" : "/opds/folders"
            : Href("/opds/folders", ("path", parentSlash < 0 ? $"{rootIndex}" : $"{rootIndex}/{relative[..parentSlash]}"));
        var heading = relative.Length == 0 ? FolderName(library.Roots[rootIndex]) : relative[(parentSlash + 1)..];

        var pageCount = Math.Max(1, (books.Count + PageSize - 1) / PageSize);
        page = Math.Min(page, pageCount);
        var type = books.Count > 0 ? AtomFeedWriter.AcquisitionType : AtomFeedWriter.NavigationType;
        (string, string)[] query = [("path", path)];

        using var feed = new AtomFeedWriter(PageAddress("/opds/folders", query, page), heading, type, up, library.Scanned);
        if (books.Count > 0)
            feed.Paging(books.Count, PageSize, page, pageCount, number => PageAddress("/opds/folders", query, number));

        if (page == 1)
        {
            foreach (var folder in folders)
                feed.Navigation($"folder:{rootIndex}/{folder.RelativePath}", folder.Name, Href("/opds/folders", ("path", $"{rootIndex}/{folder.RelativePath}")), BookCount(folder.ItemCount), AtomFeedWriter.AcquisitionType);
        }

        foreach (var book in books.Skip((page - 1) * PageSize).Take(PageSize))
            feed.Book(book);

        return feed.Finish(type);
    }

    /// <summary>Every word must occur in the title, an author or the series.</summary>
    public CatalogPage Search(CollectionSnapshot library, string? query, int page)
    {
        var found = CollectionText.Normalize(query ?? string.Empty).Length == 0 ? [] : library.Search(query);
        return Books("/opds/search", [("q", query ?? string.Empty)], $"Поиск: {query}", found, page, "/opds");
    }

    public byte[] OpenSearchDescription(string? host)
    {
        // Some readers do not resolve a relative template against the description address
        var origin = host is not null && host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or ':' or '-' or '[' or ']')
            ? "http://" + host
            : string.Empty;

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
        {
            const string OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";
            writer.WriteStartDocument();
            writer.WriteStartElement("OpenSearchDescription", OpenSearch);
            writer.WriteElementString("ShortName", OpenSearch, title);
            writer.WriteElementString("Description", OpenSearch, "Поиск по названию, автору и серии");
            writer.WriteElementString("InputEncoding", OpenSearch, "UTF-8");
            writer.WriteElementString("OutputEncoding", OpenSearch, "UTF-8");
            foreach (var type in new[] { AtomFeedWriter.AcquisitionType, "application/atom+xml" })
            {
                writer.WriteStartElement("Url", OpenSearch);
                writer.WriteAttributeString("type", type);
                writer.WriteAttributeString("template", origin + "/opds/search?q={searchTerms}");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return stream.ToArray();
    }

    /// <summary>"Автор - Название.fb2.zip": the name the reader saves the book under.</summary>
    public static string DownloadName(CollectionItem book)
    {
        var name = book.Authors.Count > 0 ? $"{book.Authors[0]} - {book.Title}" : book.Title;
        var clean = new StringBuilder(name.Length);
        foreach (var character in name)
            clean.Append(char.IsControl(character) || Array.IndexOf(InvalidNameCharacters, character) >= 0 ? '_' : character);

        var result = clean.ToString().Trim().TrimEnd('.');
        if (result.Length > 120)
            result = result[..120].TrimEnd();

        return (result.Length > 0 ? result : book.Id) + book.Extension;
    }

    public static string Href(string path, params (string Name, string Value)[] query)
        => query.Length == 0
            ? path
            : path + "?" + string.Join("&", query.Select(pair => $"{pair.Name}={Uri.EscapeDataString(pair.Value)}"));

    private static string PageAddress(string address, (string Name, string Value)[] query, int page)
        => page == 1 ? Href(address, query) : Href(address, [.. query, ("page", page.ToString(CultureInfo.InvariantCulture))]);

    private static string FolderName(string root)
        => Path.GetFileName(Path.TrimEndingDirectorySeparator(root)) is { Length: > 0 } name ? name : root;

    private static string BookCount(int count)
        => Count(count, "книга", "книги", "книг");

    private static string Count(int count, string one, string few, string many)
    {
        var word = (count % 100, count % 10) switch
        {
            ( >= 11 and <= 14, _) => many,
            (_, 1) => one,
            (_, >= 2 and <= 4) => few,
            _ => many,
        };
        return $"{count} {word}";
    }
}
