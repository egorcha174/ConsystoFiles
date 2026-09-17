using System.Text;

namespace Consysto.MediaPreview.Music;

/// <summary>FLAC metadata blocks: stream info (duration), Vorbis comments and pictures.</summary>
internal static class FlacReader
{
    public static void Read(Stream stream, TrackInfo info, bool includeCover)
    {
        stream.Position = 4;
        while (true)
        {
            var header = Binary.Read(stream, 4);
            if (header.Length < 4)
                return;

            var isLast = (header[0] & 0x80) != 0;
            var type = header[0] & 0x7F;
            var length = header[1] << 16 | header[2] << 8 | header[3];

            var wanted = type is 0 or 4 || type == 6 && includeCover && length <= TrackReader.MaximumCoverBytes + 1024;
            if (wanted)
            {
                var block = Binary.Read(stream, length);
                switch (type)
                {
                    case 0 when block.Length >= 18:
                        var sampleRate = block[10] << 12 | block[11] << 4 | block[12] >> 4;
                        var totalSamples = (long)(block[13] & 0x0F) << 32 | Binary.UInt32(block, 14, true);
                        if (sampleRate > 0 && totalSamples > 0)
                            info.Duration = TimeSpan.FromSeconds((double)totalSamples / sampleRate);
                        break;
                    case 4:
                        TrackReader.ReadVorbisComments(block, info, includeCover);
                        break;
                    case 6:
                        ReadPicture(block, info);
                        break;
                }
            }
            else
            {
                stream.Seek(length, SeekOrigin.Current);
            }

            if (isLast)
                return;
        }
    }

    /// <summary>A FLAC picture block, also stored base64-encoded in Ogg comments.</summary>
    public static void ReadPicture(ReadOnlySpan<byte> block, TrackInfo info)
    {
        if (block.Length < 32)
            return;

        var pictureType = Binary.UInt32(block, 0, true);
        var offset = 4;
        offset += 4 + (int)Binary.UInt32(block, offset, true);
        if (offset + 4 > block.Length)
            return;

        offset += 4 + (int)Binary.UInt32(block, offset, true);
        offset += 16;
        if (offset + 4 > block.Length)
            return;

        var length = (int)Binary.UInt32(block, offset, true);
        offset += 4;
        if (length <= 0 || offset + length > block.Length)
            return;

        var image = block.Slice(offset, length);
        if (Binary.LooksLikeImage(image) && (info.Cover is null || pictureType == 3))
            info.Cover = image.ToArray();
    }
}

/// <summary>Ogg Vorbis and Opus: the identification and comment packets, and the last page for the duration.</summary>
internal static class OggReader
{
    private const int MaximumCommentBytes = TrackReader.MaximumCoverBytes * 2;

    public static void Read(Stream stream, TrackInfo info, bool includeCover)
    {
        uint? serial = null;
        var packets = new List<byte[]>();
        var current = new MemoryStream();
        var sampleRate = 0;
        var preSkip = 0;
        var isOpus = false;

        stream.Position = 0;
        while (packets.Count < 2)
        {
            var header = Binary.Read(stream, 27);
            if (header.Length < 27 || !Binary.Matches(header, 0, "OggS"u8))
                break;

            var pageSerial = Binary.UInt32(header, 14, false);
            var segments = Binary.Read(stream, header[26]);
            var dataLength = segments.Sum(segment => segment);
            serial ??= pageSerial;
            if (pageSerial != serial)
            {
                stream.Seek(dataLength, SeekOrigin.Current);
                continue;
            }

            var data = Binary.Read(stream, dataLength);
            var position = 0;
            foreach (var segment in segments)
            {
                current.Write(data, position, Math.Min(segment, data.Length - position));
                position += segment;
                if (current.Length > MaximumCommentBytes)
                    return;

                // A segment shorter than 255 bytes ends the packet
                if (segment < 255)
                {
                    packets.Add(current.ToArray());
                    current.SetLength(0);
                    if (packets.Count == 2)
                        break;
                }
            }
        }

        if (packets.Count == 0)
            return;

        var identification = packets[0];
        if (Binary.Matches(identification, 0, "OpusHead"u8) && identification.Length >= 12)
        {
            isOpus = true;
            sampleRate = 48000;
            preSkip = Binary.UInt16(identification, 10, false);
        }
        else if (Binary.Matches(identification, 0, "vorbis"u8) && identification.Length >= 16)
        {
            sampleRate = (int)Binary.UInt32(identification, 12, false);
        }

        if (packets.Count == 2)
        {
            var comments = packets[1];
            if (isOpus && Binary.Matches(comments, 0, "OpusTags"u8))
                TrackReader.ReadVorbisComments(comments.AsSpan(8), info, includeCover);
            else if (Binary.Matches(comments, 0, "vorbis"u8))
                TrackReader.ReadVorbisComments(comments.AsSpan(7), info, includeCover);
        }

        if (sampleRate > 0 && serial is { } streamSerial && LastGranule(stream, streamSerial) is { } granule && granule > preSkip)
            info.Duration = TimeSpan.FromSeconds((double)(granule - preSkip) / sampleRate);
    }

    private static long? LastGranule(Stream stream, uint serial)
    {
        var tailLength = (int)Math.Min(stream.Length, 256 * 1024);
        stream.Seek(-tailLength, SeekOrigin.End);
        var tail = Binary.Read(stream, tailLength);
        for (var index = tail.Length - 27; index >= 0; index--)
        {
            if (Binary.Matches(tail, index, "OggS"u8) && Binary.UInt32(tail, index + 14, false) == serial)
                return (long)((ulong)Binary.UInt32(tail, index + 10, false) << 32 | Binary.UInt32(tail, index + 6, false));
        }

        return null;
    }
}

/// <summary>MP4 and M4A: the iTunes-style item list under moov/udta/meta/ilst, and the movie header for the duration.</summary>
internal static class Mp4Reader
{
    private const long MaximumMovieBytes = 128L * 1024 * 1024;

    public static void Read(Stream stream, TrackInfo info, bool includeCover)
    {
        stream.Position = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var start = stream.Position;
            var (type, size, headerLength) = ReadAtomHeader(stream);
            if (size < headerLength)
                return;

            if (type == "moov")
            {
                if (size > MaximumMovieBytes)
                    return;

                var movie = Binary.Read(stream, (int)(size - headerLength));
                ReadContainer(movie, info, includeCover);
                return;
            }

            stream.Position = start + size;
        }
    }

    private static (string Type, long Size, int HeaderLength) ReadAtomHeader(Stream stream)
    {
        var header = Binary.Read(stream, 8);
        if (header.Length < 8)
            return (string.Empty, 0, 8);

        long size = Binary.UInt32(header, 0, true);
        var type = Encoding.Latin1.GetString(header, 4, 4);
        if (size == 1)
            return (type, (long)Binary.UInt64BigEndian(Binary.Read(stream, 8), 0), 16);

        return (type, size == 0 ? stream.Length - stream.Position + 8 : size, 8);
    }

    private static void ReadContainer(ReadOnlySpan<byte> data, TrackInfo info, bool includeCover)
    {
        var offset = 0;
        while (offset + 8 <= data.Length)
        {
            var size = (int)Binary.UInt32(data, offset, true);
            var type = Encoding.Latin1.GetString(data.Slice(offset + 4, 4));
            if (size < 8 || offset + size > data.Length)
                return;

            var body = data.Slice(offset + 8, size - 8);
            switch (type)
            {
                case "udta" or "ilst":
                    if (type == "ilst")
                        ReadItems(body, info, includeCover);
                    else
                        ReadContainer(body, info, includeCover);
                    break;
                case "meta":
                    // A full atom (version and flags) in MP4, a plain container in QuickTime files
                    ReadContainer(body.Length >= 8 && Binary.Matches(body, 4, "hdlr"u8) ? body : body[Math.Min(4, body.Length)..], info, includeCover);
                    break;
                case "mvhd" when body.Length >= 20:
                    var isVersion1 = body[0] == 1;
                    var timescale = Binary.UInt32(body, isVersion1 ? 20 : 12, true);
                    var duration = isVersion1 && body.Length >= 32 ? Binary.UInt64BigEndian(body, 24) : Binary.UInt32(body, 16, true);
                    if (timescale > 0 && duration > 0)
                        info.Duration = TimeSpan.FromSeconds((double)duration / timescale);
                    break;
            }

            offset += size;
        }
    }

    private static void ReadItems(ReadOnlySpan<byte> list, TrackInfo info, bool includeCover)
    {
        var offset = 0;
        while (offset + 8 <= list.Length)
        {
            var size = (int)Binary.UInt32(list, offset, true);
            if (size < 8 || offset + size > list.Length)
                return;

            var name = Encoding.Latin1.GetString(list.Slice(offset + 4, 4));
            var item = list.Slice(offset + 8, size - 8);
            offset += size;

            // Each item holds a "data" atom: type, locale, then the value
            if (item.Length < 16 || !Binary.Matches(item, 4, "data"u8))
                continue;

            var dataSize = (int)Binary.UInt32(item, 0, true);
            if (dataSize < 16 || dataSize > item.Length)
                continue;

            var value = item.Slice(16, dataSize - 16);
            string Text(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

            switch (name)
            {
                case "©nam":
                    info.Title ??= Binary.Clean(Text(value));
                    break;
                case "©ART":
                    TrackReader.AddText(info.Artists, Text(value));
                    break;
                case "aART":
                    info.AlbumArtist ??= Binary.Clean(Text(value));
                    break;
                case "©alb":
                    info.Album ??= Binary.Clean(Text(value));
                    break;
                case "©day":
                    info.Year ??= TrackReader.Year(Text(value));
                    break;
                case "©gen":
                    TrackReader.AddText(info.Genres, Text(value));
                    break;
                case "trkn" when value.Length >= 4:
                    info.Track ??= TrackReader.Number(Binary.UInt16(value, 2, true).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "disk" when value.Length >= 4:
                    info.Disc ??= TrackReader.Number(Binary.UInt16(value, 2, true).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "covr" when includeCover && info.Cover is null && value.Length <= TrackReader.MaximumCoverBytes && Binary.LooksLikeImage(value):
                    info.Cover = value.ToArray();
                    break;
            }
        }
    }
}
