using System.Globalization;
using System.Text;

namespace Consysto.MediaPreview.Photos;

/// <summary>What a photo says about itself.</summary>
public sealed class PhotoInfo
{
    /// <summary>Local time on the camera, as EXIF stores it.</summary>
    public DateTime? Taken { get; set; }

    public string? Make { get; set; }

    public string? Model { get; set; }

    public int? Width { get; set; }

    public int? Height { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    /// <summary>"Canon EOS 5D": the model, with the maker in front unless the model already names it.</summary>
    public string? Camera
    {
        get
        {
            if (Model is null)
                return Make;
            if (Make is null)
                return Model;

            var maker = Make.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return Model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? Model : $"{Make} {Model}";
        }
    }
}

/// <summary>
/// Date taken, camera, size and location from JPEG, PNG, WebP, GIF, BMP and TIFF-based files (TIFF, DNG and most camera raw
/// formats), read from the file headers without decoding the picture.
/// </summary>
public static class PhotoReader
{
    private const int TiffHeaderBytes = 512 * 1024;

    public static PhotoInfo Read(string path)
    {
        var info = new PhotoInfo();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        var header = Binary.Read(stream, 32);
        stream.Position = 0;

        if (header is [0xFF, 0xD8, ..])
            ReadJpeg(stream, info);
        else if (Binary.Matches(header, 0, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            ReadPng(stream, info);
        else if (Binary.Matches(header, 0, "RIFF"u8) && Binary.Matches(header, 8, "WEBP"u8))
            ReadWebp(stream, info);
        else if (Binary.Matches(header, 0, "II*\0"u8) || Binary.Matches(header, 0, "MM\0*"u8))
            ReadTiff(Binary.Read(stream, TiffHeaderBytes), info, useImageSize: true);
        else if (Binary.Matches(header, 0, "GIF8"u8) && header.Length >= 10)
            (info.Width, info.Height) = (Binary.UInt16(header, 6, false), Binary.UInt16(header, 8, false));
        else if (Binary.Matches(header, 0, "BM"u8) && header.Length >= 26)
            (info.Width, info.Height) = ((int)Binary.UInt32(header, 18, false), Math.Abs((int)Binary.UInt32(header, 22, false)));

        return info;
    }

    private static void ReadJpeg(Stream stream, PhotoInfo info)
    {
        stream.Position = 2;
        while (true)
        {
            var marker = Binary.Read(stream, 2);
            if (marker.Length < 2 || marker[0] != 0xFF)
                return;

            var type = marker[1];
            if (type == 0xFF)
            {
                stream.Position--;
                continue;
            }

            if (type is 0x01 or >= 0xD0 and <= 0xD7)
                continue;

            // Start of scan: the picture follows, no more headers
            if (type is 0xDA or 0xD9)
                return;

            var lengthBytes = Binary.Read(stream, 2);
            if (lengthBytes.Length < 2)
                return;

            var length = Binary.UInt16(lengthBytes, 0, true) - 2;
            if (length < 0)
                return;

            var isFrame = type is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (type == 0xE1 || isFrame)
            {
                var payload = Binary.Read(stream, length);
                if (type == 0xE1 && Binary.Matches(payload, 0, "Exif\0\0"u8))
                    ReadTiff(payload.AsSpan(6), info, useImageSize: false);
                else if (isFrame && payload.Length >= 5)
                    (info.Height, info.Width) = (Binary.UInt16(payload, 1, true), Binary.UInt16(payload, 3, true));
            }
            else
            {
                stream.Seek(length, SeekOrigin.Current);
            }
        }
    }

    private static void ReadPng(Stream stream, PhotoInfo info)
    {
        stream.Position = 8;
        for (var chunkIndex = 0; chunkIndex < 256; chunkIndex++)
        {
            var header = Binary.Read(stream, 8);
            if (header.Length < 8)
                return;

            var length = (int)Binary.UInt32(header, 0, true);
            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (length < 0)
                return;

            switch (type)
            {
                case "IHDR":
                    var ihdr = Binary.Read(stream, length);
                    if (ihdr.Length >= 8)
                        (info.Width, info.Height) = ((int)Binary.UInt32(ihdr, 0, true), (int)Binary.UInt32(ihdr, 4, true));
                    break;
                case "eXIf":
                    ReadTiff(Binary.Read(stream, length), info, useImageSize: false);
                    break;
                case "IEND":
                    return;
                default:
                    stream.Seek(length, SeekOrigin.Current);
                    break;
            }

            stream.Seek(4, SeekOrigin.Current);
        }
    }

    private static void ReadWebp(Stream stream, PhotoInfo info)
    {
        stream.Position = 12;
        for (var chunkIndex = 0; chunkIndex < 64; chunkIndex++)
        {
            var header = Binary.Read(stream, 8);
            if (header.Length < 8)
                return;

            var type = Encoding.ASCII.GetString(header, 0, 4);
            var length = (int)Binary.UInt32(header, 4, false);
            if (length < 0)
                return;

            var data = type is "VP8X" or "EXIF" or "VP8 " ? Binary.Read(stream, length) : null;
            if (data is null)
                stream.Seek(length, SeekOrigin.Current);

            switch (type)
            {
                case "VP8X" when data!.Length >= 10:
                    info.Width = (data[4] | data[5] << 8 | data[6] << 16) + 1;
                    info.Height = (data[7] | data[8] << 8 | data[9] << 16) + 1;
                    break;
                case "VP8 " when data!.Length >= 10 && info.Width is null:
                    info.Width = Binary.UInt16(data, 6, false) & 0x3FFF;
                    info.Height = Binary.UInt16(data, 8, false) & 0x3FFF;
                    break;
                case "EXIF":
                    ReadTiff(Binary.Matches(data!, 0, "Exif\0\0"u8) ? data.AsSpan(6) : data, info, useImageSize: false);
                    break;
            }

            if ((length & 1) == 1)
                stream.Seek(1, SeekOrigin.Current);
        }
    }

    /// <summary>EXIF is a small TIFF: IFD0 (maker, model), the EXIF IFD (date taken, pixel size) and the GPS IFD.</summary>
    private static void ReadTiff(ReadOnlySpan<byte> data, PhotoInfo info, bool useImageSize)
    {
        if (data.Length < 8)
            return;

        var bigEndian = data[0] == (byte)'M';
        if (!(bigEndian ? data[1] == (byte)'M' : data[0] == (byte)'I' && data[1] == (byte)'I'))
            return;

        var tiff = new TiffReader(data, bigEndian);
        var ifd0 = tiff.ReadIfd((int)Binary.UInt32(data, 4, bigEndian));

        info.Make ??= Binary.Clean(tiff.Text(ifd0, 0x010F));
        info.Model ??= Binary.Clean(tiff.Text(ifd0, 0x0110));
        var changed = ParseDate(tiff.Text(ifd0, 0x0132));

        if (useImageSize)
        {
            info.Width ??= tiff.Number(ifd0, 0x0100);
            info.Height ??= tiff.Number(ifd0, 0x0101);
        }

        if (tiff.Number(ifd0, 0x8769) is { } exifOffset)
        {
            var exif = tiff.ReadIfd(exifOffset);
            info.Taken ??= ParseDate(tiff.Text(exif, 0x9003)) ?? ParseDate(tiff.Text(exif, 0x9004));
            if (tiff.Number(exif, 0xA002) is { } width && tiff.Number(exif, 0xA003) is { } height)
                (info.Width, info.Height) = (width, height);
        }

        info.Taken ??= changed;

        if (tiff.Number(ifd0, 0x8825) is { } gpsOffset)
        {
            var gps = tiff.ReadIfd(gpsOffset);
            if (tiff.Degrees(gps, 2) is { } latitude && tiff.Degrees(gps, 4) is { } longitude)
            {
                info.Latitude = tiff.Text(gps, 1)?.StartsWith('S') == true ? -latitude : latitude;
                info.Longitude = tiff.Text(gps, 3)?.StartsWith('W') == true ? -longitude : longitude;
            }
        }
    }

    private static DateTime? ParseDate(string? text)
        => text is not null && DateTime.TryParseExact(text.Trim('\0', ' '), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year > 1900
            ? date
            : null;

    private readonly ref struct TiffReader(ReadOnlySpan<byte> data, bool bigEndian)
    {
        private readonly ReadOnlySpan<byte> data = data;

        public Dictionary<ushort, (ushort Type, uint Count, int ValueOffset)> ReadIfd(int offset)
        {
            var entries = new Dictionary<ushort, (ushort, uint, int)>();
            if (offset <= 0 || offset + 2 > data.Length)
                return entries;

            var count = Binary.UInt16(data, offset, bigEndian);
            for (var index = 0; index < count; index++)
            {
                var entry = offset + 2 + index * 12;
                if (entry + 12 > data.Length)
                    break;

                var tag = Binary.UInt16(data, entry, bigEndian);
                var type = Binary.UInt16(data, entry + 2, bigEndian);
                var valueCount = Binary.UInt32(data, entry + 4, bigEndian);
                var size = TypeSize(type) * (long)valueCount;
                var valueOffset = size <= 4 ? entry + 8 : (int)Binary.UInt32(data, entry + 8, bigEndian);
                entries.TryAdd(tag, (type, valueCount, valueOffset));
            }

            return entries;
        }

        public string? Text(Dictionary<ushort, (ushort Type, uint Count, int ValueOffset)> ifd, ushort tag)
        {
            if (!ifd.TryGetValue(tag, out var entry) || entry.Type != 2 || entry.ValueOffset < 0 || entry.ValueOffset + entry.Count > data.Length)
                return null;

            return Binary.SingleByte(data.Slice(entry.ValueOffset, (int)entry.Count)).TrimEnd('\0');
        }

        public int? Number(Dictionary<ushort, (ushort Type, uint Count, int ValueOffset)> ifd, ushort tag)
        {
            if (!ifd.TryGetValue(tag, out var entry) || entry.ValueOffset < 0 || entry.ValueOffset + 4 > data.Length)
                return null;

            return entry.Type switch
            {
                3 => Binary.UInt16(data, entry.ValueOffset, bigEndian),
                4 or 9 => (int)Binary.UInt32(data, entry.ValueOffset, bigEndian),
                _ => null,
            };
        }

        /// <summary>Degrees, minutes and seconds as three rationals.</summary>
        public double? Degrees(Dictionary<ushort, (ushort Type, uint Count, int ValueOffset)> ifd, ushort tag)
        {
            if (!ifd.TryGetValue(tag, out var entry) || entry.Type != 5 || entry.Count < 3 || entry.ValueOffset < 0 || entry.ValueOffset + 24 > data.Length)
                return null;

            var degrees = 0.0;
            var unit = 1.0;
            for (var index = 0; index < 3; index++)
            {
                var denominator = Binary.UInt32(data, entry.ValueOffset + index * 8 + 4, bigEndian);
                if (denominator != 0)
                    degrees += Binary.UInt32(data, entry.ValueOffset + index * 8, bigEndian) / (double)denominator / unit;

                unit *= 60;
            }

            return degrees is > 0 and <= 180 ? degrees : null;
        }

        private static int TypeSize(ushort type)
            => type switch
            {
                3 or 8 => 2,
                4 or 9 or 11 => 4,
                5 or 10 or 12 => 8,
                _ => 1,
            };
    }
}
