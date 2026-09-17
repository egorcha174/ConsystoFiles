using System.Text;

namespace Consysto.CadPreview.Inventor;

/// <summary>One property of an OLE property set: its id, the name a user-defined set gives it, and the value.</summary>
public sealed record StoredProperty(uint Id, string? Name, object? Value);

/// <summary>A property set stream: the format id says which set it is (summary, design tracking, user-defined).</summary>
public sealed record StoredPropertySet(Guid FormatId, string StreamName, IReadOnlyList<StoredProperty> Properties);

/// <summary>
/// Reads the OLE property set streams ([MS-OLEPS]) of a compound file. Inventor keeps its iProperties there, one stream
/// per set, the same way Office once did; only the value types those sets use are decoded, others come back as null.
/// </summary>
public static class PropertySetReader
{
    private const long MaximumStreamBytes = 4 * 1024 * 1024;
    private const uint DictionaryId = 0;
    private const uint CodePageId = 1;

    public static IReadOnlyList<StoredPropertySet> Read(string path)
    {
        var sets = new List<StoredPropertySet>();
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var root = OpenMcdf.RootStorage.Open(file);
            foreach (var entry in root.EnumerateEntries())
            {
                // Property set streams are the ones whose names start with the control character 5
                if (entry.Type != OpenMcdf.EntryType.Stream || entry.Name.Length == 0 || entry.Name[0] != '')
                    continue;

                using var stream = root.OpenStream(entry.Name);
                if (stream.Length is < 48 or > MaximumStreamBytes)
                    continue;

                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                if (Parse(data, entry.Name) is { } set)
                    sets.Add(set);
            }
        }
        catch (Exception)
        {
            // A damaged or foreign file has no properties to show
        }

        return sets;
    }

    private static StoredPropertySet? Parse(byte[] data, string streamName)
    {
        // Header: byte order FFFE, version, system id, class id, section count; then format id and offset of the first section
        if (BitConverter.ToUInt16(data, 0) != 0xFFFE)
            return null;

        var sectionCount = BitConverter.ToInt32(data, 24);
        if (sectionCount < 1)
            return null;

        var formatId = new Guid(data.AsSpan(28, 16));
        var sectionOffset = BitConverter.ToInt32(data, 44);
        if (sectionOffset < 0 || sectionOffset + 8 > data.Length)
            return null;

        var count = BitConverter.ToInt32(data, sectionOffset + 4);
        if (count < 0 || sectionOffset + 8 + count * 8L > data.Length)
            return null;

        var codePage = 1252;
        var entries = new List<(uint Id, int Offset)>(count);
        for (var i = 0; i < count; i++)
        {
            var id = BitConverter.ToUInt32(data, sectionOffset + 8 + i * 8);
            var offset = sectionOffset + BitConverter.ToInt32(data, sectionOffset + 12 + i * 8);
            if (offset < sectionOffset || offset + 4 > data.Length)
                continue;
            entries.Add((id, offset));
            if (id == CodePageId && BitConverter.ToUInt16(data, offset) == 2)
                codePage = BitConverter.ToUInt16(data, offset + 4);
        }

        var names = new Dictionary<uint, string>();
        foreach (var (id, offset) in entries)
        {
            if (id == DictionaryId)
                ReadDictionary(data, offset, codePage, names);
        }

        var properties = new List<StoredProperty>();
        foreach (var (id, offset) in entries)
        {
            if (id is DictionaryId or CodePageId)
                continue;
            properties.Add(new StoredProperty(id, names.GetValueOrDefault(id), ReadValue(data, offset, codePage)));
        }

        return new StoredPropertySet(formatId, streamName, properties);
    }

    private static void ReadDictionary(byte[] data, int offset, int codePage, Dictionary<uint, string> names)
    {
        var count = BitConverter.ToInt32(data, offset);
        var position = offset + 4;
        for (var i = 0; i < count && position + 8 <= data.Length; i++)
        {
            var id = BitConverter.ToUInt32(data, position);
            var length = BitConverter.ToInt32(data, position + 4);
            position += 8;
            if (length < 0)
                return;

            if (codePage == 1200)
            {
                // UTF-16 names are counted in characters and padded to a multiple of four bytes
                var bytes = length * 2;
                if (position + bytes > data.Length)
                    return;
                names[id] = Encoding.Unicode.GetString(data, position, bytes).TrimEnd('\0');
                position += (bytes + 3) & ~3;
            }
            else
            {
                if (position + length > data.Length)
                    return;
                names[id] = GetEncoding(codePage).GetString(data, position, length).TrimEnd('\0');
                position += length;
            }
        }
    }

    private static object? ReadValue(byte[] data, int offset, int codePage)
    {
        var type = BitConverter.ToUInt16(data, offset);
        var value = offset + 4;
        try
        {
            return type switch
            {
                0x02 => (int)BitConverter.ToInt16(data, value),                 // VT_I2
                0x03 or 0x16 => BitConverter.ToInt32(data, value),              // VT_I4, VT_INT
                0x04 => (double)BitConverter.ToSingle(data, value),             // VT_R4
                0x05 => BitConverter.ToDouble(data, value),                     // VT_R8
                0x06 => BitConverter.ToInt64(data, value) / 10000m,             // VT_CY
                0x07 => DateTime.FromOADate(BitConverter.ToDouble(data, value)), // VT_DATE
                0x0B => BitConverter.ToUInt16(data, value) != 0,                // VT_BOOL
                0x12 => (int)BitConverter.ToUInt16(data, value),                // VT_UI2
                0x13 or 0x17 => BitConverter.ToUInt32(data, value),             // VT_UI4, VT_UINT
                0x14 => BitConverter.ToInt64(data, value),                      // VT_I8
                0x15 => BitConverter.ToUInt64(data, value),                     // VT_UI8
                0x1E => ReadAnsiString(data, value, codePage),                  // VT_LPSTR
                0x1F => ReadUnicodeString(data, value),                         // VT_LPWSTR
                0x40 => ReadFileTime(data, value),                              // VT_FILETIME
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadAnsiString(byte[] data, int offset, int codePage)
    {
        var length = BitConverter.ToInt32(data, offset);
        if (length < 0 || offset + 4 + length > data.Length)
            return null;
        return codePage == 1200
            ? Encoding.Unicode.GetString(data, offset + 4, length).TrimEnd('\0')
            : GetEncoding(codePage).GetString(data, offset + 4, length).TrimEnd('\0');
    }

    private static string? ReadUnicodeString(byte[] data, int offset)
    {
        var length = BitConverter.ToInt32(data, offset);
        if (length < 0 || offset + 4 + length * 2L > data.Length)
            return null;
        return Encoding.Unicode.GetString(data, offset + 4, length * 2).TrimEnd('\0');
    }

    private static DateTime? ReadFileTime(byte[] data, int offset)
    {
        var ticks = BitConverter.ToInt64(data, offset);
        return ticks <= 0 ? null : DateTime.FromFileTimeUtc(ticks);
    }

    private static Encoding GetEncoding(int codePage)
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }
}
