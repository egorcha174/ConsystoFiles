namespace Consysto.BookPreview;

public enum BookFormat
{
    Epub,
    Fb2,
    Mobi,
    Azw3,
    Pdf,
    Djvu,
}

/// <summary>What a book says about itself: the fields a library shows, plus the embedded cover.</summary>
public sealed class BookInfo
{
    public required BookFormat Format { get; init; }

    public string? Title { get; set; }

    public List<string> Authors { get; } = [];

    public string? Series { get; set; }

    /// <summary>Position in the series as written in the book ("3", "2.5"); null when absent.</summary>
    public string? SeriesIndex { get; set; }

    /// <summary>Plain text; paragraphs separated by line breaks.</summary>
    public string? Annotation { get; set; }

    public List<string> Genres { get; } = [];

    /// <summary>Language code as stored in the book ("ru", "en-US", "rus").</summary>
    public string? Language { get; set; }

    public string? Year { get; set; }

    public string? Publisher { get; set; }

    public string? Isbn { get; set; }

    public List<string> Translators { get; } = [];

    /// <summary>Encoded image (JPEG, PNG, GIF, BMP or WebP) as stored in the book.</summary>
    public byte[]? Cover { get; set; }
}
