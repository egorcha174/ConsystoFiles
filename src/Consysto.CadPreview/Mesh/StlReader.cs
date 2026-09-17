using System.Numerics;
using System.Text;

namespace Consysto.CadPreview.Mesh;

public static class StlReader
{
    public static Mesh3D Read(string path) => Read(File.ReadAllBytes(path));

    internal static Mesh3D Read(byte[] bytes)
    {
        var builder = new MeshBuilder();
        if (IsBinary(bytes))
            ReadBinary(bytes, builder);
        else
            ReadAscii(bytes, builder);
        return builder.ToMesh(MeshUp.Y);
    }

    // Exporters disagree on the header: plenty of binary files start with "solid" too. The exact size of a binary
    // file (84 + 50 bytes per triangle) is the reliable signal; failing that, ASCII must actually contain facets.
    private static bool IsBinary(byte[] bytes)
    {
        if (bytes.Length < 84)
            return false;

        long declared = BitConverter.ToUInt32(bytes, 80);
        if (84 + declared * 50 == bytes.Length)
            return true;

        var head = bytes.AsSpan(0, Math.Min(bytes.Length, 1024));
        bool looksAscii = head.TrimStart(" \t\r\n"u8).StartsWith("solid"u8) && Ascii.IsValid(head) && head.IndexOf("facet"u8) >= 0;
        return !looksAscii;
    }

    private static void ReadBinary(byte[] bytes, MeshBuilder builder)
    {
        // A truncated file keeps the triangles that are actually there.
        long declared = BitConverter.ToUInt32(bytes, 80);
        long count = Math.Min(declared, (bytes.Length - 84) / 50);
        for (long i = 0, offset = 84; i < count; i++, offset += 50)
        {
            builder.AddTriangle(
                ReadVector(bytes, (int)offset + 12),
                ReadVector(bytes, (int)offset + 24),
                ReadVector(bytes, (int)offset + 36));
        }
    }

    private static void ReadAscii(byte[] bytes, MeshBuilder builder)
    {
        ReadOnlySpan<byte> text = bytes;
        Span<Vector3> corners = stackalloc Vector3[3];
        int corner = 0;

        foreach (var range in text.Split((byte)'\n'))
        {
            var rest = AsciiTokens.TrimLine(text[range]);
            var keyword = AsciiTokens.Next(ref rest);
            if (!Ascii.EqualsIgnoreCase(keyword, "vertex"u8))
            {
                // Every facet starts its three corners anew, even if a broken one had fewer.
                if (Ascii.EqualsIgnoreCase(keyword, "outer"u8))
                    corner = 0;
                continue;
            }

            corners[corner++] = new Vector3(AsciiTokens.NextFloat(ref rest), AsciiTokens.NextFloat(ref rest), AsciiTokens.NextFloat(ref rest));
            if (corner == 3)
            {
                builder.AddTriangle(corners[0], corners[1], corners[2]);
                corner = 0;
            }
        }
    }

    private static Vector3 ReadVector(byte[] bytes, int offset)
        => new(BitConverter.ToSingle(bytes, offset), BitConverter.ToSingle(bytes, offset + 4), BitConverter.ToSingle(bytes, offset + 8));
}
