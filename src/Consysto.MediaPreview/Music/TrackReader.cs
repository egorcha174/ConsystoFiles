using System.Text;

namespace Consysto.MediaPreview.Music;

/// <summary>What a music file says about itself.</summary>
public sealed class TrackInfo
{
    public string? Title { get; set; }

    public List<string> Artists { get; } = [];

    public string? AlbumArtist { get; set; }

    public string? Album { get; set; }

    /// <summary>The track number alone, without the album total ("3", not "3/12").</summary>
    public string? Track { get; set; }

    public string? Disc { get; set; }

    public string? Year { get; set; }

    public List<string> Genres { get; } = [];

    public TimeSpan? Duration { get; set; }

    /// <summary>Encoded image as stored in the file; the front cover when the file tells which one it is.</summary>
    public byte[]? Cover { get; set; }
}

/// <summary>
/// Tags and the album cover of MP3 (ID3v2.2–2.4, ID3v1), FLAC, Ogg Vorbis, Opus and MP4/M4A files, read in managed code from
/// the file headers.
/// </summary>
public static class TrackReader
{
    internal const int MaximumCoverBytes = 20 * 1024 * 1024;

    public static TrackInfo Read(string path, bool includeCover = true)
    {
        var info = new TrackInfo();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var header = Binary.Read(stream, 12);
        stream.Position = 0;

        if (Binary.Matches(header, 0, "ID3"u8))
        {
            Id3Reader.ReadVersion2(stream, info, includeCover);
            if (info.Title is null)
                Id3Reader.ReadVersion1(stream, info);
        }
        else if (Binary.Matches(header, 0, "fLaC"u8))
        {
            FlacReader.Read(stream, info, includeCover);
        }
        else if (Binary.Matches(header, 0, "OggS"u8))
        {
            OggReader.Read(stream, info, includeCover);
        }
        else if (Binary.Matches(header, 4, "ftyp"u8))
        {
            Mp4Reader.Read(stream, info, includeCover);
        }
        else if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            Id3Reader.ReadVersion1(stream, info);
        }

        return info;
    }

    internal static void AddText(List<string> values, string? text)
    {
        if (Binary.Clean(text) is { } cleaned && !values.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
            values.Add(cleaned);
    }

    /// <summary>"3/12" → "3"; "03" → "3".</summary>
    internal static string? Number(string? text)
    {
        var value = Binary.Clean(text)?.Split('/')[0].Trim();
        return int.TryParse(value, out var number) && number > 0 ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    /// <summary>"1989-04-05" → "1989".</summary>
    internal static string? Year(string? text)
        => Binary.Clean(text) is { Length: >= 4 } value && int.TryParse(value[..4], out var year) && year is > 1000 and < 3000 ? value[..4] : null;

    /// <summary>Vorbis comments: FLAC, Ogg Vorbis and Opus share them.</summary>
    internal static void ReadVorbisComments(ReadOnlySpan<byte> data, TrackInfo info, bool includeCover)
    {
        if (data.Length < 8)
            return;

        var offset = 4 + (int)Binary.UInt32(data, 0, false);
        if (offset + 4 > data.Length || offset < 4)
            return;

        var count = Binary.UInt32(data, offset, false);
        offset += 4;
        for (var index = 0; index < count && offset + 4 <= data.Length; index++)
        {
            var length = (int)Binary.UInt32(data, offset, false);
            offset += 4;
            if (length < 0 || offset + length > data.Length)
                return;

            var comment = Encoding.UTF8.GetString(data.Slice(offset, length));
            offset += length;

            var equals = comment.IndexOf('=');
            if (equals <= 0)
                continue;

            var value = comment[(equals + 1)..];
            switch (comment[..equals].ToUpperInvariant())
            {
                case "TITLE":
                    info.Title ??= Binary.Clean(value);
                    break;
                case "ARTIST":
                    AddText(info.Artists, value);
                    break;
                case "ALBUMARTIST" or "ALBUM ARTIST":
                    info.AlbumArtist ??= Binary.Clean(value);
                    break;
                case "ALBUM":
                    info.Album ??= Binary.Clean(value);
                    break;
                case "TRACKNUMBER":
                    info.Track ??= Number(value);
                    break;
                case "DISCNUMBER":
                    info.Disc ??= Number(value);
                    break;
                case "DATE" or "YEAR":
                    info.Year ??= Year(value);
                    break;
                case "GENRE":
                    AddText(info.Genres, value);
                    break;
                case "METADATA_BLOCK_PICTURE" when includeCover:
                    try
                    {
                        FlacReader.ReadPicture(Convert.FromBase64String(value), info);
                    }
                    catch (FormatException)
                    {
                    }

                    break;
                case "COVERART" when includeCover && info.Cover is null:
                    try
                    {
                        var image = Convert.FromBase64String(value);
                        if (Binary.LooksLikeImage(image))
                            info.Cover = image;
                    }
                    catch (FormatException)
                    {
                    }

                    break;
            }
        }
    }
}
