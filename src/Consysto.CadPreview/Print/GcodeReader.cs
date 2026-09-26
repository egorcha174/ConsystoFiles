using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Consysto.CadPreview.Print;

/// <summary>
/// Reads what a slicer wrote into a print job: G-code and the binary G-code of PrusaSlicer, a sliced 3MF project
/// (OrcaSlicer, Bambu Studio, Flash Studio) and the .gx of FlashPrint. Only the head and the tail of a G-code file are read,
/// where slicers keep their comments, so a folder of large jobs is still quick.
/// </summary>
public static partial class GcodeReader
{
    private const int HeadBytes = 1024 * 1024;
    private const int TailBytes = 512 * 1024;
    private const int MaxThumbnailBytes = 8 * 1024 * 1024;
    private const int MaxMetadataBytes = 16 * 1024 * 1024;

    /// <summary>A year: anything longer is a broken field, not a print.</summary>
    private const double MaxSeconds = 365 * 24 * 3600;

    private static readonly string[] TextExtensions = [".gcode", ".gco", ".g"];

    public static bool IsSupported(string? extension)
        => extension is not null
            && (TextExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                || extension.Equals(".bgcode", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".gx", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A print job by its name. A 3MF is one only as ".gcode.3mf", the name slicers give a sliced project; a plain .3mf is
    /// a model and keeps the columns of models.
    /// </summary>
    public static bool IsPrintFile(string? path)
        => path is not null
            && (IsSupported(Path.GetExtension(path)) || path.EndsWith(".gcode.3mf", StringComparison.OrdinalIgnoreCase));

    public static PrintInfo Read(string path)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".bgcode" => ReadBinary(path),
                ".gx" => ReadFlashPrint(path),
                ".3mf" => ReadThreeMf(path),
                _ => ReadText(path),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or OverflowException or FormatException)
        {
            return new PrintInfo();
        }
    }

    // ---- G-code text ----

    private static PrintInfo ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Parse(ReadHeadAndTail(stream));
    }

    /// <summary>The first megabyte and the last half of one: the header and thumbnails, and the settings some slicers append.</summary>
    private static string ReadHeadAndTail(Stream stream)
    {
        var length = stream.Length;
        if (length <= HeadBytes + TailBytes)
            return ReadText(stream, (int)length);

        var head = ReadText(stream, HeadBytes);
        stream.Seek(-TailBytes, SeekOrigin.End);
        return head + "\n" + ReadText(stream, TailBytes);
    }

    /// <summary>The same for a stream that cannot seek, such as a file inside a zip: the middle is read and dropped.</summary>
    private static string ReadHeadAndTailForward(Stream stream)
    {
        var head = new byte[HeadBytes];
        var headLength = stream.ReadAtLeast(head, HeadBytes, throwOnEndOfStream: false);
        if (headLength < HeadBytes)
            return Encoding.UTF8.GetString(head, 0, headLength);

        // A ring of the last bytes read
        var tail = new byte[TailBytes];
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            for (var offset = 0; offset < read;)
            {
                var at = (int)(total % TailBytes);
                var count = Math.Min(read - offset, TailBytes - at);
                Buffer.BlockCopy(chunk, offset, tail, at, count);
                offset += count;
                total += count;
            }
        }

        if (total == 0)
            return Encoding.UTF8.GetString(head, 0, headLength);

        var start = total > TailBytes ? (int)(total % TailBytes) : 0;
        var length = (int)Math.Min(total, TailBytes);
        var ordered = new byte[length];
        var first = Math.Min(length, TailBytes - start);
        Buffer.BlockCopy(tail, start, ordered, 0, first);
        Buffer.BlockCopy(tail, 0, ordered, first, length - first);
        return Encoding.UTF8.GetString(head, 0, headLength) + "\n" + Encoding.UTF8.GetString(ordered);
    }

    private static string ReadText(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    /// <summary>Settings as "; key = value" or "; key: value", Cura's ";KEY:value", and the thumbnail blocks.</summary>
    internal static PrintInfo Parse(string text, IDictionary<string, string>? extra = null)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
                values.TryAdd(key, value);
        }

        byte[]? thumbnail = null;
        var thumbnailArea = 0;
        string? slicer = null;

        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length < 2 || line[0] != ';')
                continue;

            var body = line.AsSpan(1).Trim();
            var thumbnailStart = ThumbnailStart().Match(line);
            if (thumbnailStart.Success)
            {
                var format = thumbnailStart.Groups["format"].Value;
                var area = long.TryParse(thumbnailStart.Groups["w"].Value, CultureInfo.InvariantCulture, out var w) && long.TryParse(thumbnailStart.Groups["h"].Value, CultureInfo.InvariantCulture, out var h) && w < 100_000 && h < 100_000
                    ? (int)(w * h)
                    : 0;
                var data = ReadThumbnailBlock(reader);
                if (area > thumbnailArea && Picture(format, data) is { } picture)
                {
                    thumbnail = picture;
                    thumbnailArea = area;
                }

                continue;
            }

            if (slicer is null && body.StartsWith("generated by ", StringComparison.OrdinalIgnoreCase))
                slicer = GeneratedBy(body[13..].ToString());
            else if (slicer is null && body.StartsWith("Generated with ", StringComparison.OrdinalIgnoreCase))
                slicer = GeneratedBy(body[15..].ToString());

            // "; key = value", "; key: value", ";KEY:value"
            var separator = body.IndexOf('=');
            var colon = body.IndexOf(':');
            if (separator < 0 || (colon >= 0 && colon < separator))
                separator = colon;
            if (separator <= 0)
                continue;

            var key = body[..separator].Trim().ToString();
            var value = body[(separator + 1)..].Trim().ToString();
            if (key.Length is > 0 and < 80 && value.Length > 0)
                values.TryAdd(key, value);

            // Orca writes both times on one line: "; model printing time: 1h 2m; total estimated time: 1h 5m"
            var total = value.IndexOf("total estimated time:", StringComparison.OrdinalIgnoreCase);
            if (total >= 0)
                values.TryAdd("total estimated time", value[(total + 21)..].Trim());
        }

        if (slicer is null && values.TryGetValue("Producer", out var producer))
            slicer = producer;

        return new PrintInfo
        {
            PrintTime = Time(values),
            FilamentGrams = Sum(values, "total filament used [g]", "filament used [g]", "total filament weight [g]", "filament_weight"),
            FilamentMeters = Sum(values, "filament used [mm]") / 1000 ?? CuraMeters(values),
            FilamentType = FilamentTypes(values),
            LayerHeight = First(values, "layer_height", "Layer height", "layer height"),
            NozzleDiameter = First(values, "nozzle_diameter", "machine_nozzle_size"),
            Layers = (int?)First(values, "total layer number", "total layers count", "total_layer_count", "LAYER_COUNT"),
            Printer = Text(values, "printer_model", "MACHINE_NAME", "TARGET_MACHINE.NAME", "printer_settings_id"),
            Slicer = slicer,
            Thumbnail = thumbnail,
        };
    }

    /// <summary>The base64 lines of a thumbnail, up to "; thumbnail end".</summary>
    private static string ReadThumbnailBlock(StringReader reader)
    {
        var data = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var body = line.TrimStart(';', ' ');
            if (body.StartsWith("thumbnail", StringComparison.OrdinalIgnoreCase) && body.Contains("end", StringComparison.OrdinalIgnoreCase))
                break;
            data.Append(body.Trim());
            if (data.Length > MaxThumbnailBytes * 4 / 3)
                break;
        }

        return data.ToString();
    }

    private static byte[]? Picture(string format, string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return format.Equals("QOI", StringComparison.OrdinalIgnoreCase) ? QoiDecoder.ToPng(bytes) : bytes;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>"Flash Studio 1.7.13 on 2026-09-26 at 08:37:47" → "Flash Studio 1.7.13"; "Cura_SteamEngine 5.7.1" → "Cura 5.7.1".</summary>
    private static string GeneratedBy(string text)
    {
        var at = text.IndexOf(" on ", StringComparison.Ordinal);
        if (at > 0)
            text = text[..at];
        text = text.Replace("Cura_SteamEngine", "Cura", StringComparison.OrdinalIgnoreCase);
        var plus = text.IndexOf('+');
        return (plus > 0 ? text[..plus] : text).Trim();
    }

    private static TimeSpan? Time(Dictionary<string, string> values)
    {
        foreach (var key in new[] { "total estimated time", "estimated printing time (normal mode)", "estimated printing time", "model printing time", "print_time", "Build time" })
        {
            if (values.TryGetValue(key, out var text) && Duration(text) is { } time)
                return time;
        }

        // Cura: ";TIME:3765" in seconds
        return values.TryGetValue("TIME", out var seconds) && double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s is > 0 and < MaxSeconds
            ? TimeSpan.FromSeconds(s)
            : null;
    }

    /// <summary>"1d 2h 3m 4s", "1h 2m 45s", "1 hours 2 minutes" or plain seconds.</summary>
    internal static TimeSpan? Duration(string text)
    {
        // Orca puts both times on one line: "model printing time: 1h 2m; total estimated time: 1h 5m"
        var semicolon = text.IndexOf(';');
        if (semicolon > 0)
            text = text[..semicolon];

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
            return plain is > 0 and < MaxSeconds ? TimeSpan.FromSeconds(plain) : null;

        var total = TimeSpan.Zero;
        var found = false;
        foreach (Match part in DurationPart().Matches(text))
        {
            if (!double.TryParse(part.Groups["n"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value > MaxSeconds)
                return null;
            total += char.ToLowerInvariant(part.Groups["u"].Value[0]) switch
            {
                'd' => TimeSpan.FromDays(value),
                'h' => TimeSpan.FromHours(value),
                'm' => TimeSpan.FromMinutes(value),
                _ => TimeSpan.FromSeconds(value),
            };
            found = true;
        }

        return found && total > TimeSpan.Zero && total.TotalSeconds < MaxSeconds ? total : null;
    }

    /// <summary>"20.00" or, for several filaments, "12.3, 4.5" summed.</summary>
    private static double? Sum(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!values.TryGetValue(key, out var text))
                continue;

            double sum = 0;
            var any = false;
            foreach (var part in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                {
                    sum += value;
                    any = true;
                }
            }

            if (any && sum > 0)
                return sum;
        }

        return null;
    }

    /// <summary>Cura: ";Filament used: 1.23456m, 0m".</summary>
    private static double? CuraMeters(Dictionary<string, string> values)
        => values.TryGetValue("Filament used", out var text) ? Sum(new(StringComparer.OrdinalIgnoreCase) { ["x"] = text.Replace("m", string.Empty, StringComparison.Ordinal) }, "x") : null;

    private static double? First(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var text)
                && double.TryParse(text.Split([',', ';'])[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
                return value;
        }

        return null;
    }

    private static string? Text(Dictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var text) && text.Trim('"', ' ') is { Length: > 0 } value)
                return value;
        }

        return null;
    }

    private static string? FilamentTypes(Dictionary<string, string> values)
    {
        if (Text(values, "filament_type", "filament type", "Filament type", "MATERIAL") is not { } text)
            return null;

        var types = text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(type => type.Trim('"'))
            .Where(type => type.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return types.Count > 0 ? string.Join(", ", types) : null;
    }

    // ---- Binary G-code of PrusaSlicer ----

    private enum BlockType : ushort { FileMetadata = 0, Gcode = 1, SlicerMetadata = 2, PrinterMetadata = 3, PrintMetadata = 4, Thumbnail = 5 }

    /// <summary>
    /// "GCDE", then blocks: type, compression, sizes, parameters, data and a checksum. Metadata is "key=value" text; the
    /// G-code itself is not needed. Metadata packed with heatshrink is skipped: only none and deflate are unpacked.
    /// </summary>
    private static PrintInfo ReadBinary(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 10 || reader.ReadUInt32() != 0x45444347)
            return new PrintInfo();

        reader.ReadUInt32();
        var checksumSize = reader.ReadUInt16() == 1 ? 4 : 0;

        var text = new StringBuilder();
        byte[]? thumbnail = null;
        var thumbnailArea = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var type = (BlockType)reader.ReadUInt16();
            var compression = reader.ReadUInt16();
            var size = reader.ReadUInt32();
            var stored = compression == 0 ? size : reader.ReadUInt32();

            if (type == BlockType.Gcode)
                break;

            if (type == BlockType.Thumbnail)
            {
                var format = reader.ReadUInt16();
                var area = reader.ReadUInt16() * reader.ReadUInt16();
                var data = stored <= MaxThumbnailBytes ? reader.ReadBytes((int)stored) : null;
                if (data is not null && compression == 0 && area > thumbnailArea)
                {
                    thumbnail = format == 2 ? QoiDecoder.ToPng(data) : data;
                    thumbnailArea = area;
                }
                if (data is null)
                    stream.Seek(stored, SeekOrigin.Current);
            }
            else
            {
                reader.ReadUInt16();
                if (stored > MaxMetadataBytes || size > MaxMetadataBytes)
                {
                    stream.Seek(stored + checksumSize, SeekOrigin.Current);
                    continue;
                }

                var data = reader.ReadBytes((int)stored);
                if (Unpack(data, compression, (int)size) is { } unpacked)
                {
                    foreach (var line in Encoding.UTF8.GetString(unpacked).Split('\n'))
                        text.Append("; ").Append(line.TrimEnd('\r')).Append('\n');
                }
            }

            stream.Seek(checksumSize, SeekOrigin.Current);
        }

        var info = Parse(text.ToString());
        return thumbnail is null ? info : WithThumbnail(info, thumbnail);
    }

    /// <summary>No more than the size the block declares, which is itself bounded.</summary>
    private static byte[]? Unpack(byte[] data, ushort compression, int size)
    {
        if (compression == 0)
            return data;
        if (compression != 1)
            return null;

        using var input = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        var output = new byte[size];
        var read = input.ReadAtLeast(output, size, throwOnEndOfStream: false);
        return output[..read];
    }

    // ---- FlashPrint .gx ----

    /// <summary>"xgcode 1.0", a header of offsets, a BMP of the model and then plain G-code.</summary>
    private static PrintInfo ReadFlashPrint(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var head = new byte[Math.Min(stream.Length, HeadBytes)];
        stream.ReadExactly(head);
        if (head.Length < 64 || !Encoding.ASCII.GetString(head, 0, 6).Equals("xgcode", StringComparison.Ordinal))
            return Parse(Encoding.UTF8.GetString(head));

        // The header is a table of offsets; rather than trust its layout, the bitmap is found by its own "BM" header and size
        var bitmapStart = head.AsSpan(12, Math.Min(256, head.Length - 12)).IndexOf("BM"u8) is var found and >= 0 ? found + 12 : -1;
        byte[]? bitmap = null;
        var gcodeStart = 0;
        if (bitmapStart > 0 && bitmapStart + 6 <= head.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(bitmapStart + 2));
            if (size > 54 && bitmapStart + size <= head.Length)
            {
                bitmap = head[bitmapStart..(bitmapStart + size)];
                gcodeStart = bitmapStart + size;
            }
        }

        var info = Parse(Encoding.UTF8.GetString(head, gcodeStart, head.Length - gcodeStart));
        return bitmap is null ? info : WithThumbnail(info, bitmap);
    }

    // ---- Sliced 3MF ----

    /// <summary>
    /// A project of OrcaSlicer, Bambu Studio or Flash Studio: the G-code of the first plate says how long and how much, the
    /// project settings say what printer and plastic, and the plate picture is what the slicer showed.
    /// </summary>
    private static PrintInfo ReadThreeMf(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var gcode = archive.Entries.FirstOrDefault(entry => GcodeEntry().IsMatch(entry.FullName));

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (archive.GetEntry("Metadata/project_settings.config") is { Length: < 16 * 1024 * 1024 } config)
        {
            using var configStream = config.Open();
            using var document = JsonDocument.Parse(configStream);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                settings[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()!,
                    JsonValueKind.Array => string.Join(";", property.Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString())),
                    _ => property.Value.ToString(),
                };
            }
        }

        var text = string.Empty;
        // Settings of an unsliced project do not make a print job
        if (gcode is null)
            return new PrintInfo();

        using (var entry = gcode.Open())
            text = ReadHeadAndTailForward(entry);

        // The plate picture is larger than the one inside the G-code
        var info = Parse(text, settings);
        var picture = archive.GetEntry("Metadata/plate_1.png");
        if (picture is null || picture.Length > MaxThumbnailBytes)
            return info;

        using var pictureStream = picture.Open();
        using var bytes = new MemoryStream();
        pictureStream.CopyTo(bytes);
        return WithThumbnail(info, bytes.ToArray());
    }

    private static PrintInfo WithThumbnail(PrintInfo info, byte[] thumbnail)
        => new()
        {
            PrintTime = info.PrintTime,
            FilamentGrams = info.FilamentGrams,
            FilamentMeters = info.FilamentMeters,
            FilamentType = info.FilamentType,
            LayerHeight = info.LayerHeight,
            NozzleDiameter = info.NozzleDiameter,
            Layers = info.Layers,
            Printer = info.Printer,
            Slicer = info.Slicer,
            Thumbnail = thumbnail,
        };

    [GeneratedRegex(@"^;\s*thumbnail(?:_(?<format>PNG|JPG|QOI))?\s+begin\s+(?<w>\d+)\s*x\s*(?<h>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ThumbnailStart();

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)\s*(?<u>d|h|m|s)", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPart();

    [GeneratedRegex(@"^Metadata/plate_\d+\.gcode$", RegexOptions.IgnoreCase)]
    private static partial Regex GcodeEntry();
}
