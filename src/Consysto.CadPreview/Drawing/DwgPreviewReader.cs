using ACadSharp.IO;

namespace Consysto.CadPreview.Drawing;

/// <summary>Reads the thumbnail AutoCAD and Inventor store in the DWG header (Inventor 3D exports: 180×180 BMP).</summary>
public static class DwgPreviewReader
{
    /// <returns>A complete PNG or BMP file, or null when the DWG has no usable preview.</returns>
    public static byte[]? TryRead(string path)
    {
        try
        {
            using var reader = new DwgReader(path);
            var preview = reader.ReadPreview();
            var raw = preview?.RawImage;
            if (raw is null || raw.Length < 16)
                return null;

            if (raw[0] == 0x89 && raw[1] == (byte)'P')
                return raw;
            if (raw[0] == (byte)'B' && raw[1] == (byte)'M')
                return raw;
            // A bare DIB starts with its BITMAPINFOHEADER size.
            if (BitConverter.ToInt32(raw, 0) == 40)
                return WithBitmapFileHeader(raw);
            return null;
        }
        catch (Exception)
        {
            // The preview is a best-effort extra; a damaged header must not break the drawing itself.
            return null;
        }
    }

    private static byte[] WithBitmapFileHeader(byte[] dib)
    {
        int headerSize = BitConverter.ToInt32(dib, 0);
        int bitCount = BitConverter.ToUInt16(dib, 14);
        int usedColors = BitConverter.ToInt32(dib, 32);
        int paletteEntries = usedColors != 0 ? usedColors : bitCount <= 8 ? 1 << bitCount : 0;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(14 + headerSize + paletteEntries * 4).CopyTo(file, 10);
        dib.CopyTo(file, 14);
        return file;
    }
}
