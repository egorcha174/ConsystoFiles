namespace Consysto.BookPreview.Opds;

/// <summary>One page of an OPDS catalog, OPDS 1.x (Atom) or OPDS 2.0 (JSON), brought to a common shape.</summary>
public sealed class OpdsFeed
{
    /// <summary>Address the page was finally served from, after redirects; relative links are resolved against it.</summary>
    public required Uri Address { get; init; }

    public string? Title { get; set; }

    public string? Subtitle { get; set; }

    /// <summary>Absolute http(s) address or a data: URI.</summary>
    public string? Icon { get; set; }

    public Uri? StartPage { get; set; }

    public Uri? UpPage { get; set; }

    public Uri? PreviousPage { get; set; }

    public Uri? NextPage { get; set; }

    /// <summary>OPDS 1 OpenSearch description; <see cref="OpdsClient.GetSearchTemplateAsync"/> turns it into <see cref="SearchTemplate"/>.</summary>
    public Uri? SearchDescription { get; set; }

    /// <summary>Absolute search address with a <c>{searchTerms}</c> placeholder.</summary>
    public string? SearchTemplate { get; set; }

    public List<OpdsEntry> Entries { get; } = [];
}

/// <summary>A catalog entry: either a way into another page (navigation) or a book with download links, or both.</summary>
public sealed class OpdsEntry
{
    public string? Id { get; set; }

    public string? Title { get; set; }

    public List<string> Authors { get; } = [];

    /// <summary>Plain text; paragraphs separated by line breaks.</summary>
    public string? Summary { get; set; }

    /// <summary>Heading of the OPDS 2 group the entry was listed under.</summary>
    public string? Group { get; set; }

    public string? Language { get; set; }

    public string? Year { get; set; }

    public string? Publisher { get; set; }

    public List<string> Categories { get; } = [];

    public string? Series { get; set; }

    public string? SeriesIndex { get; set; }

    /// <summary>Absolute http(s) address or a data: URI.</summary>
    public string? Thumbnail { get; set; }

    /// <summary>Absolute http(s) address or a data: URI.</summary>
    public string? Image { get; set; }

    /// <summary>Catalog page the entry leads to: a subsection, or the full record of a book.</summary>
    public Uri? Navigation { get; set; }

    public List<OpdsAcquisition> Acquisitions { get; } = [];

    /// <summary>Other catalog pages about the entry ("by the same author", "readers also downloaded").</summary>
    public List<OpdsLink> Related { get; } = [];

    public bool IsPublication
        => Acquisitions.Count > 0;
}

public sealed record OpdsLink(string Title, Uri Address);

/// <param name="Title">Label the catalog gives the link ("EPUB (no images)"), when it gives one.</param>
/// <param name="Length">Size in bytes announced by the catalog.</param>
public sealed record OpdsAcquisition(Uri Address, string MediaType, OpdsBookFormat Format, OpdsAcquisitionKind Kind, string? Title = null, long? Length = null)
{
    /// <summary>Links that hand over the file itself; buying, borrowing and subscribing need the catalog's web site.</summary>
    public bool IsDirectDownload
        => Kind is OpdsAcquisitionKind.Download or OpdsAcquisitionKind.OpenAccess or OpdsAcquisitionKind.Sample;
}

public enum OpdsAcquisitionKind
{
    Download,
    OpenAccess,
    Sample,
    Borrow,
    Buy,
    Subscribe,
}

/// <summary>Raised when the catalog answers 401; the caller asks for a user name and password and retries.</summary>
public sealed class OpdsAuthenticationRequiredException(Uri address, string? realm)
    : Exception($"The catalog at {address.Host} requires a user name and password.")
{
    public Uri Address { get; } = address;

    public string? Realm { get; } = realm;
}
