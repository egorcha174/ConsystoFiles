using System.Text;

namespace Consysto.MediaPreview.Music;

/// <summary>ID3v2.2, 2.3 and 2.4 tags at the start of an MP3 file, and the old 128-byte ID3v1 tag at its end.</summary>
internal static class Id3Reader
{
    private const int MaximumTagBytes = 64 * 1024 * 1024;

    // ID3v1 genre numbers, still used as "(17)" in ID3v2 genre frames
    private static readonly string[] Genres =
    [
        "Blues", "Classic Rock", "Country", "Dance", "Disco", "Funk", "Grunge", "Hip-Hop", "Jazz", "Metal", "New Age", "Oldies",
        "Other", "Pop", "R&B", "Rap", "Reggae", "Rock", "Techno", "Industrial", "Alternative", "Ska", "Death Metal", "Pranks",
        "Soundtrack", "Euro-Techno", "Ambient", "Trip-Hop", "Vocal", "Jazz+Funk", "Fusion", "Trance", "Classical", "Instrumental",
        "Acid", "House", "Game", "Sound Clip", "Gospel", "Noise", "Alternative Rock", "Bass", "Soul", "Punk", "Space", "Meditative",
        "Instrumental Pop", "Instrumental Rock", "Ethnic", "Gothic", "Darkwave", "Techno-Industrial", "Electronic", "Pop-Folk",
        "Eurodance", "Dream", "Southern Rock", "Comedy", "Cult", "Gangsta", "Top 40", "Christian Rap", "Pop/Funk", "Jungle",
        "Native American", "Cabaret", "New Wave", "Psychedelic", "Rave", "Showtunes", "Trailer", "Lo-Fi", "Tribal", "Acid Punk",
        "Acid Jazz", "Polka", "Retro", "Musical", "Rock & Roll", "Hard Rock",
    ];

    private static readonly Dictionary<string, string> Version22Frames = new(StringComparer.Ordinal)
    {
        ["TT2"] = "TIT2", ["TP1"] = "TPE1", ["TP2"] = "TPE2", ["TAL"] = "TALB", ["TRK"] = "TRCK",
        ["TPA"] = "TPOS", ["TYE"] = "TYER", ["TCO"] = "TCON", ["PIC"] = "APIC",
    };

    public static void ReadVersion2(Stream stream, TrackInfo info, bool includeCover)
    {
        var header = Binary.Read(stream, 10);
        if (header.Length < 10)
            return;

        var major = header[3];
        var flags = header[5];
        var size = Syncsafe(header, 6);
        if (major is < 2 or > 4 || size <= 0 || size > MaximumTagBytes)
            return;

        var tag = Binary.Read(stream, size);

        // Whole-tag unsynchronisation: every FF 00 stands for FF
        if ((flags & 0x80) != 0 && major < 4)
            tag = RemoveUnsynchronisation(tag);

        var offset = 0;
        if ((flags & 0x40) != 0 && major >= 3 && tag.Length >= 4)
            offset = major == 3 ? 4 + (int)Binary.UInt32(tag, 0, true) : Syncsafe(tag, 0);

        var idLength = major == 2 ? 3 : 4;
        var headerLength = major == 2 ? 6 : 10;
        while (offset + headerLength <= tag.Length && tag[offset] != 0)
        {
            var id = Encoding.ASCII.GetString(tag, offset, idLength);
            var frameSize = major switch
            {
                2 => tag[offset + 3] << 16 | tag[offset + 4] << 8 | tag[offset + 5],
                3 => (int)Binary.UInt32(tag, offset + 4, true),
                _ => Syncsafe(tag, offset + 4),
            };
            var frameFlags = major == 4 ? tag[offset + 9] : (byte)0;
            offset += headerLength;
            if (frameSize <= 0 || offset + frameSize > tag.Length)
                break;

            var frame = tag.AsSpan(offset, frameSize);
            offset += frameSize;

            if (major == 4)
            {
                if ((frameFlags & 0x01) != 0 && frame.Length >= 4)
                    frame = frame[4..];
                if ((frameFlags & 0x02) != 0)
                    frame = RemoveUnsynchronisation(frame.ToArray());
            }

            if (major == 2)
                id = Version22Frames.GetValueOrDefault(id, id);

            ReadFrame(id, frame, info, includeCover, major == 2);
        }
    }

    public static void ReadVersion1(Stream stream, TrackInfo info)
    {
        if (stream.Length < 128)
            return;

        stream.Seek(-128, SeekOrigin.End);
        var tag = Binary.Read(stream, 128);
        if (!Binary.Matches(tag, 0, "TAG"u8))
            return;

        string? Field(int offset, int length)
        {
            var field = tag.AsSpan(offset, length);
            var end = field.IndexOf((byte)0);
            return Binary.Clean(Binary.SingleByte(end < 0 ? field : field[..end]));
        }

        info.Title ??= Field(3, 30);
        if (info.Artists.Count == 0)
            TrackReader.AddText(info.Artists, Field(33, 30));
        info.Album ??= Field(63, 30);
        info.Year ??= TrackReader.Year(Field(93, 4));
        if (tag[125] == 0 && tag[126] != 0)
            info.Track ??= tag[126].ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (info.Genres.Count == 0 && tag[127] < Genres.Length)
            info.Genres.Add(Genres[tag[127]]);
    }

    private static void ReadFrame(string id, ReadOnlySpan<byte> frame, TrackInfo info, bool includeCover, bool isVersion22)
    {
        switch (id)
        {
            case "TIT2":
                info.Title ??= Binary.Clean(Texts(frame).FirstOrDefault());
                break;
            case "TPE1":
                foreach (var artist in Texts(frame))
                    TrackReader.AddText(info.Artists, artist);
                break;
            case "TPE2":
                info.AlbumArtist ??= Binary.Clean(Texts(frame).FirstOrDefault());
                break;
            case "TALB":
                info.Album ??= Binary.Clean(Texts(frame).FirstOrDefault());
                break;
            case "TRCK":
                info.Track ??= TrackReader.Number(Texts(frame).FirstOrDefault());
                break;
            case "TPOS":
                info.Disc ??= TrackReader.Number(Texts(frame).FirstOrDefault());
                break;
            case "TYER" or "TDRC" or "TDOR" or "TORY":
                info.Year ??= TrackReader.Year(Texts(frame).FirstOrDefault());
                break;
            case "TCON":
                foreach (var genre in Texts(frame))
                    TrackReader.AddText(info.Genres, GenreName(genre));
                break;
            case "APIC" when includeCover:
                ReadPicture(frame, info, isVersion22);
                break;
        }
    }

    /// <summary>"(17)", "17" and "(17)Rock" name genre 17; anything else is the genre itself.</summary>
    private static string? GenreName(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('(') && value.IndexOf(')') is > 1 and var close)
        {
            var rest = value[(close + 1)..].Trim();
            if (rest.Length > 0)
                return rest;

            value = value[1..close];
        }

        return int.TryParse(value, out var number) && number >= 0 && number < Genres.Length ? Genres[number] : value;
    }

    private static void ReadPicture(ReadOnlySpan<byte> frame, TrackInfo info, bool isVersion22)
    {
        if (frame.Length < 4)
            return;

        var encoding = frame[0];
        int offset;
        if (isVersion22)
        {
            offset = 4;
        }
        else
        {
            var mimeEnd = frame[1..].IndexOf((byte)0);
            if (mimeEnd < 0)
                return;

            offset = 1 + mimeEnd + 1;
        }

        if (offset >= frame.Length)
            return;

        var pictureType = frame[offset];
        offset++;
        offset += TerminatedLength(frame[offset..], encoding);
        if (offset >= frame.Length)
            return;

        var image = frame[offset..];
        if (image.Length > TrackReader.MaximumCoverBytes || !Binary.LooksLikeImage(image))
            return;

        // The front cover (type 3) wins over whatever came first
        if (info.Cover is null || pictureType == 3)
            info.Cover = image.ToArray();
    }

    private static IEnumerable<string> Texts(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
            return [];

        var text = Decode(frame[1..], frame[0]);
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string Decode(ReadOnlySpan<byte> data, byte encoding)
        => encoding switch
        {
            1 => data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF
                ? Encoding.BigEndianUnicode.GetString(data[2..])
                : Encoding.Unicode.GetString(data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? data[2..] : data),
            2 => Encoding.BigEndianUnicode.GetString(data),
            3 => Encoding.UTF8.GetString(data),
            _ => Binary.SingleByte(data),
        };

    /// <summary>Length of a text with its terminator: one zero byte, or two for UTF-16.</summary>
    private static int TerminatedLength(ReadOnlySpan<byte> data, byte encoding)
    {
        if (encoding is 1 or 2)
        {
            for (var index = 0; index + 1 < data.Length; index += 2)
            {
                if (data[index] == 0 && data[index + 1] == 0)
                    return index + 2;
            }

            return data.Length;
        }

        var end = data.IndexOf((byte)0);
        return end < 0 ? data.Length : end + 1;
    }

    private static int Syncsafe(ReadOnlySpan<byte> data, int offset)
        => data[offset] << 21 | data[offset + 1] << 14 | data[offset + 2] << 7 | data[offset + 3];

    private static byte[] RemoveUnsynchronisation(byte[] data)
    {
        var result = new List<byte>(data.Length);
        for (var index = 0; index < data.Length; index++)
        {
            result.Add(data[index]);
            if (data[index] == 0xFF && index + 1 < data.Length && data[index + 1] == 0x00)
                index++;
        }

        return [.. result];
    }
}
