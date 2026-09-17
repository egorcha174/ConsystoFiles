using System.Globalization;
using Consysto.Collections;

namespace Consysto.MediaPreview.Photos;

/// <summary>Field names of a photo in a collection.</summary>
public static class PhotoFields
{
    /// <summary>"2007-06-07 14:22:00": sortable text, local camera time; the file date when the photo has none.</summary>
    public const string Taken = "taken";
    public const string Year = "year";
    public const string Camera = "camera";

    /// <summary>"4000×3000".</summary>
    public const string Size = "size";

    /// <summary>"55.751244,37.618423".</summary>
    public const string Location = "location";
}

/// <summary>
/// Photos and pictures, by year and camera, in the order they were taken. Only identical files are duplicates: two frames of a
/// burst share the camera and the second, and are still different photos.
/// </summary>
public sealed class PhotoCollection : CollectionKind
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".avif",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".pef", ".srw",
    };

    private static readonly CollectionFacet[] PhotoFacets =
    [
        new(PhotoFields.Year, PhotoFields.Year) { Order = ByTaken },
        new(PhotoFields.Camera, PhotoFields.Camera) { Order = ByTaken },
    ];

    public override string Id => "photos";

    public override IReadOnlyList<CollectionFacet> Facets => PhotoFacets;

    public override IReadOnlyList<string> SearchFields => [PhotoFields.Camera, PhotoFields.Year];

    public override bool Accepts(string fileName)
        => Extensions.Contains(Path.GetExtension(fileName));

    public override ItemMetadata Read(string path)
    {
        // Every picture has a thumbnail: the host asks the shell for one
        var metadata = new ItemMetadata { HasCover = true };
        PhotoInfo? info = null;
        try
        {
            info = PhotoReader.Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable headers: the photo is still listed, by its file date
        }

        var taken = info?.Taken ?? File.GetLastWriteTime(path);
        metadata
            .Set(PhotoFields.Taken, taken.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .Set(PhotoFields.Year, taken.Year.ToString(CultureInfo.InvariantCulture))
            .Set(PhotoFields.Camera, info?.Camera);

        if (info?.Width is > 0 and var width && info.Height is > 0 and var height)
            metadata.Set(PhotoFields.Size, $"{width}×{height}");
        if (info?.Latitude is { } latitude && info.Longitude is { } longitude)
            metadata.Set(PhotoFields.Location, string.Create(CultureInfo.InvariantCulture, $"{latitude:0.######},{longitude:0.######}"));

        return metadata;
    }

    /// <summary>Oldest first, as an album is leafed through.</summary>
    public override string SortKey(CollectionItem item)
        => $"{item.Value(PhotoFields.Taken)} {item.Title}";

    private static IEnumerable<CollectionItem> ByTaken(IEnumerable<CollectionItem> items)
        => items.OrderBy(item => item.Value(PhotoFields.Taken), StringComparer.Ordinal);
}
