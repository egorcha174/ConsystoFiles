using System.Globalization;
using Consysto.Collections;

namespace Consysto.MediaPreview.Music;

/// <summary>Field names of a track in a collection.</summary>
public static class MusicFields
{
    public const string Artists = "artists";
    public const string AlbumArtist = "albumArtist";
    public const string Album = "album";
    public const string Track = "track";
    public const string Disc = "disc";
    public const string Year = "year";
    public const string Genres = "genres";

    /// <summary>Whole seconds.</summary>
    public const string Duration = "duration";
}

/// <summary>
/// Music by artist, album, genre and year. Duplicates are the same artist, title and album in the same format (the same song
/// ripped twice, at another bitrate); the same song in another format or on another album is kept.
/// </summary>
public sealed class MusicCollection : CollectionKind
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".m4b", ".mp4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".wav", ".aiff", ".aif", ".ape", ".wv",
    };

    private static readonly CollectionFacet[] MusicFacets =
    [
        new(MusicFields.Artists, MusicFields.Artists) { Order = ByAlbum },
        new(MusicFields.Album, MusicFields.Album) { Order = ByTrack },
        new(MusicFields.Genres, MusicFields.Genres) { Order = ByAlbum },
        new(MusicFields.Year, MusicFields.Year) { Order = ByAlbum },
    ];

    public override string Id => "music";

    public override IReadOnlyList<CollectionFacet> Facets => MusicFacets;

    public override IReadOnlyList<string> SearchFields => [MusicFields.Artists, MusicFields.Album];

    public override bool Accepts(string fileName)
        => Extensions.Contains(Path.GetExtension(fileName));

    public override ItemMetadata Read(string path)
    {
        var info = TrackReader.Read(path, includeCover: true);
        var metadata = new ItemMetadata { Title = info.Title, HasCover = info.Cover is not null }
            .Set(MusicFields.Artists, info.Artists)
            .Set(MusicFields.AlbumArtist, info.AlbumArtist)
            .Set(MusicFields.Album, info.Album)
            .Set(MusicFields.Track, info.Track)
            .Set(MusicFields.Disc, info.Disc)
            .Set(MusicFields.Year, info.Year)
            .Set(MusicFields.Genres, info.Genres);

        if (info.Duration is { TotalSeconds: >= 1 } duration)
            metadata.Set(MusicFields.Duration, ((int)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture));

        return metadata;
    }

    public override string? SameItemKey(CollectionItem item)
    {
        if (item.Values(MusicFields.Artists) is not [var artist, ..])
            return null;

        var album = item.Value(MusicFields.Album) ?? string.Empty;
        return $"{item.Extension}|{CollectionText.Normalize(artist)}|{CollectionText.Normalize(item.Title)}|{CollectionText.Normalize(album)}";
    }

    /// <summary>The embedded cover, for the host to draw.</summary>
    public static byte[]? ReadCover(string path)
        => TrackReader.Read(path, includeCover: true).Cover;

    private static IEnumerable<CollectionItem> ByAlbum(IEnumerable<CollectionItem> items)
        => items
            .OrderBy(item => item.Value(MusicFields.Year) ?? "9999", StringComparer.Ordinal)
            .ThenBy(item => item.Value(MusicFields.Album), CollectionText.Comparer)
            .ThenBy(item => CollectionText.NumberOrder(item.Value(MusicFields.Disc)))
            .ThenBy(item => CollectionText.NumberOrder(item.Value(MusicFields.Track)));

    private static IEnumerable<CollectionItem> ByTrack(IEnumerable<CollectionItem> items)
        => items
            .OrderBy(item => CollectionText.NumberOrder(item.Value(MusicFields.Disc)))
            .ThenBy(item => CollectionText.NumberOrder(item.Value(MusicFields.Track)));
}
