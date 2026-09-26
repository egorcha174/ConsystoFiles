using System.Buffers.Binary;
using System.IO.Compression;

namespace Consysto.CadPreview.Print;

/// <summary>
/// QOI, the "Quite OK Image" format some slicers store thumbnails in for printers with small screens. Decoded here and
/// written back as PNG, because the picture decoders of Windows do not know it.
/// </summary>
internal static class QoiDecoder
{
    private const int MaxPixels = 4096 * 4096;

    public static byte[]? ToPng(byte[] data)
    {
        if (data.Length < 22 || data[0] != 'q' || data[1] != 'o' || data[2] != 'i' || data[3] != 'f')
            return null;

        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            return null;

        var pixels = new byte[width * height * 4];
        Span<byte> index = stackalloc byte[64 * 4];
        byte r = 0, g = 0, b = 0, a = 255;
        var position = 14;
        var run = 0;
        for (var pixel = 0; pixel < pixels.Length; pixel += 4)
        {
            if (run > 0)
            {
                run--;
            }
            else if (position < data.Length)
            {
                var tag = data[position++];
                if (tag == 0xFE && position + 3 <= data.Length)
                {
                    r = data[position++]; g = data[position++]; b = data[position++];
                }
                else if (tag == 0xFF && position + 4 <= data.Length)
                {
                    r = data[position++]; g = data[position++]; b = data[position++]; a = data[position++];
                }
                else if ((tag & 0xC0) == 0x00)
                {
                    var slot = (tag & 0x3F) * 4;
                    r = index[slot]; g = index[slot + 1]; b = index[slot + 2]; a = index[slot + 3];
                }
                else if ((tag & 0xC0) == 0x40)
                {
                    r += (byte)(((tag >> 4) & 0x03) - 2);
                    g += (byte)(((tag >> 2) & 0x03) - 2);
                    b += (byte)((tag & 0x03) - 2);
                }
                else if ((tag & 0xC0) == 0x80 && position < data.Length)
                {
                    var next = data[position++];
                    var dg = (tag & 0x3F) - 32;
                    r += (byte)(dg - 8 + ((next >> 4) & 0x0F));
                    g += (byte)dg;
                    b += (byte)(dg - 8 + (next & 0x0F));
                }
                else if ((tag & 0xC0) == 0xC0)
                {
                    run = tag & 0x3F;
                }

                var hash = (r * 3 + g * 5 + b * 7 + a * 11) % 64 * 4;
                index[hash] = r; index[hash + 1] = g; index[hash + 2] = b; index[hash + 3] = a;
            }

            pixels[pixel] = r; pixels[pixel + 1] = g; pixels[pixel + 2] = b; pixels[pixel + 3] = a;
        }

        return EncodePng(width, height, pixels);
    }

    /// <summary>A plain RGBA PNG: one IDAT, no filtering.</summary>
    private static byte[] EncodePng(int width, int height, byte[] rgba)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        using var raw = new MemoryStream();
        using (var deflate = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var row = 0; row < height; row++)
            {
                deflate.WriteByte(0);
                deflate.Write(rgba, row * width * 4, width * 4);
            }
        }

        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crc = Crc32(Crc32(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(uint crc, byte[] data)
    {
        foreach (var value in data)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
