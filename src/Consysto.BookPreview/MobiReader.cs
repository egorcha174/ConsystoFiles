using System.Buffers.Binary;
using System.Text;

namespace Consysto.BookPreview;

/// <summary>
/// Mobipocket, AZW and AZW3 (KF8): a PalmDB container whose record 0 holds the MOBI header and the EXTH
/// metadata block; images are separate records counted from the header's first image index.
/// </summary>
internal static class MobiReader
{
    private const int PalmHeaderLength = 78;
    private const int MaximumHeaderRecordBytes = 1 << 20;
    private const uint NoIndex = 0xFFFFFFFF;

    public static BookInfo Read(string path, BookFormat format, bool includeCover)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        var palmHeader = ReadAt(file, 0, PalmHeaderLength);
        if (Encoding.ASCII.GetString(palmHeader, 60, 8) != "BOOKMOBI")
            throw new InvalidDataException("The file is not a Mobipocket book.");

        int recordCount = BinaryPrimitives.ReadUInt16BigEndian(palmHeader.AsSpan(76));
        if (recordCount == 0)
            throw new InvalidDataException("The book has no records.");

        var recordList = ReadAt(file, PalmHeaderLength, recordCount * 8);
        long RecordStart(int index) => BinaryPrimitives.ReadUInt32BigEndian(recordList.AsSpan(index * 8));
        long RecordEnd(int index) => index + 1 < recordCount ? RecordStart(index + 1) : file.Length;

        var header = ReadRecord(file, RecordStart(0), RecordEnd(0), MaximumHeaderRecordBytes);
        if (header is null || header.Length < 132 || Encoding.ASCII.GetString(header, 16, 4) != "MOBI")
            throw new InvalidDataException("The book has no MOBI header.");

        var mobiHeaderLength = U32(header, 20);
        var encoding = U32(header, 28) == 65001 ? Encoding.UTF8 : Encoding.GetEncoding(1252);
        var fullNameOffset = U32(header, 84);
        var fullNameLength = U32(header, 88);
        var firstImageIndex = U32(header, 108);
        var exthFlags = U32(header, 128);

        var info = new BookInfo { Format = format };
        uint? coverOffset = null;
        uint? thumbnailOffset = null;

        long exth = 16L + mobiHeaderLength;
        if ((exthFlags & 0x40) != 0 && exth + 12 <= header.Length && Encoding.ASCII.GetString(header, (int)exth, 4) == "EXTH")
        {
            var count = U32(header, exth + 8);
            var position = exth + 12;
            for (uint i = 0; i < count && position + 8 <= header.Length; i++)
            {
                var type = U32(header, position);
                var length = U32(header, position + 4);
                if (length < 8 || position + length > header.Length)
                    break;

                var data = header.AsSpan((int)position + 8, (int)length - 8);
                switch (type)
                {
                    case 100:
                        TextCleanup.AddTo(info.Authors, TextCleanup.Clean(encoding.GetString(data)));
                        break;
                    case 101:
                        info.Publisher ??= TextCleanup.Clean(encoding.GetString(data));
                        break;
                    case 103:
                        info.Annotation ??= TextCleanup.FromHtml(encoding.GetString(data));
                        break;
                    case 104:
                        info.Isbn ??= TextCleanup.Isbn(encoding.GetString(data));
                        break;
                    case 105:
                        TextCleanup.AddTo(info.Genres, TextCleanup.Clean(encoding.GetString(data)));
                        break;
                    case 106:
                        info.Year ??= TextCleanup.Year(encoding.GetString(data));
                        break;
                    case 503:
                        info.Title ??= TextCleanup.Clean(encoding.GetString(data));
                        break;
                    case 524:
                        info.Language ??= TextCleanup.Clean(encoding.GetString(data));
                        break;
                    case 201 when data.Length >= 4:
                        coverOffset = BinaryPrimitives.ReadUInt32BigEndian(data);
                        break;
                    case 202 when data.Length >= 4:
                        thumbnailOffset = BinaryPrimitives.ReadUInt32BigEndian(data);
                        break;
                }

                position += length;
            }
        }

        if (info.Title is null && (long)fullNameOffset + fullNameLength <= header.Length)
            info.Title = TextCleanup.Clean(encoding.GetString(header, (int)fullNameOffset, (int)fullNameLength));

        if (includeCover && firstImageIndex != NoIndex)
        {
            foreach (var offset in new[] { coverOffset, thumbnailOffset })
            {
                if (offset is not { } imageOffset || imageOffset == NoIndex)
                    continue;

                var index = (long)firstImageIndex + imageOffset;
                if (index >= recordCount)
                    continue;

                var image = ReadRecord(file, RecordStart((int)index), RecordEnd((int)index), BookReader.MaximumCoverBytes);
                if (image is not null && BookReader.LooksLikeImage(image))
                {
                    info.Cover = image;
                    break;
                }
            }
        }

        return info;
    }

    private static uint U32(byte[] data, long offset)
        => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)offset, 4));

    private static byte[]? ReadRecord(FileStream file, long start, long end, int maximumLength)
    {
        var length = end - start;
        if (start < 0 || length <= 0 || length > maximumLength || end > file.Length)
            return null;

        return ReadAt(file, start, (int)length);
    }

    private static byte[] ReadAt(FileStream file, long offset, int length)
    {
        if (offset + length > file.Length)
            throw new InvalidDataException("The book is truncated.");

        var buffer = new byte[length];
        file.Position = offset;
        file.ReadExactly(buffer);
        return buffer;
    }
}
