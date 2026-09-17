using Consysto.Collections;

namespace Consysto.BookPreview.Library;

/// <summary>Field names of a book in a collection.</summary>
public static class BookFields
{
    public const string Authors = "authors";
    public const string Series = "series";
    public const string SeriesIndex = "seriesIndex";
    public const string Annotation = "annotation";
    public const string Genres = "genres";
    public const string Language = "language";
    public const string Year = "year";
    public const string Publisher = "publisher";
}

/// <summary>
/// Books: EPUB, FictionBook, Kindle, PDF and DjVu, grouped by author, series and genre. Duplicates are the same title
/// and authors in the same format; the same book in another format is kept on purpose. Books without authors are matched only
/// as identical files, since their title is often just the file name.
/// </summary>
/// <param name="hostDrawsPdfCovers">The host renders the first page of a PDF, so PDF books are listed as having a cover.</param>
public sealed class BookCollection(bool hostDrawsPdfCovers = false) : CollectionKind
{
    private static readonly CollectionFacet[] BookFacets =
    [
        new(BookFields.Authors, BookFields.Authors)
        {
            DisplayName = CollectionText.SurnameFirst,
            Order = books => books
                .OrderBy(book => book.Series is null)
                .ThenBy(book => book.Series, CollectionText.Comparer)
                .ThenBy(book => CollectionText.NumberOrder(book.SeriesIndex)),
        },
        new(BookFields.Series, BookFields.Series)
        {
            Order = books => books.OrderBy(book => CollectionText.NumberOrder(book.SeriesIndex)),
        },
        new(BookFields.Genres, BookFields.Genres),
    ];

    public override string Id => "books";

    public override IReadOnlyList<CollectionFacet> Facets => BookFacets;

    public override IReadOnlyList<string> SearchFields => [BookFields.Authors, BookFields.Series];

    public override bool Accepts(string fileName)
        => BookReader.GetFormat(fileName) is not null;

    public override ItemMetadata Read(string path)
    {
        var format = BookReader.GetFormat(path);
        var info = BookReader.Read(path, includeCover: true);
        var metadata = new ItemMetadata
        {
            Title = info?.Title,
            HasCover = info?.Cover is not null || format == BookFormat.Pdf && hostDrawsPdfCovers,
        };

        if (info is not null)
        {
            metadata
                .Set(BookFields.Authors, info.Authors)
                .Set(BookFields.Series, info.Series)
                .Set(BookFields.SeriesIndex, info.SeriesIndex)
                .Set(BookFields.Annotation, info.Annotation)
                .Set(BookFields.Genres, info.Genres)
                .Set(BookFields.Language, info.Language)
                .Set(BookFields.Year, info.Year)
                .Set(BookFields.Publisher, info.Publisher);
        }

        return metadata;
    }

    public override string? SameItemKey(CollectionItem item)
    {
        if (item.Authors.Count == 0)
            return null;

        var authors = item.Authors
            .Select(author => CollectionText.Normalize(CollectionText.SurnameFirst(author)))
            .Order(StringComparer.Ordinal);
        return $"{item.Format}|{CollectionText.Normalize(item.Title)}|{string.Join('|', authors)}";
    }
}

/// <summary>Book fields of a collection item and book facets of a snapshot, by name.</summary>
public static class BookItems
{
    extension(CollectionItem book)
    {
        public IReadOnlyList<string> Authors => book.Values(BookFields.Authors);

        public string? Series => book.Value(BookFields.Series);

        public string? SeriesIndex => book.Value(BookFields.SeriesIndex);

        public string? Annotation => book.Value(BookFields.Annotation);

        public IReadOnlyList<string> Genres => book.Values(BookFields.Genres);

        public string? Language => book.Value(BookFields.Language);

        public string? Year => book.Value(BookFields.Year);

        public string? Publisher => book.Value(BookFields.Publisher);

        /// <summary>From the extension, so a damaged book has one too.</summary>
        public BookFormat? Format => BookReader.GetFormat(book.Path);
    }

    extension(CollectionSnapshot library)
    {
        public IReadOnlyList<CollectionGroup> Authors => library.Facet(BookFields.Authors);

        public IReadOnlyList<CollectionGroup> Series => library.Facet(BookFields.Series);

        public IReadOnlyList<CollectionGroup> Genres => library.Facet(BookFields.Genres);
    }
}
