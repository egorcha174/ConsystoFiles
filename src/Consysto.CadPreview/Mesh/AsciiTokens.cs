using System.Globalization;

namespace Consysto.CadPreview.Mesh;

/// <summary>Allocation-free tokenizing of the ASCII mesh formats (STL, OBJ), straight from the file bytes.</summary>
internal static class AsciiTokens
{
    public static ReadOnlySpan<byte> TrimLine(ReadOnlySpan<byte> line) => line.Trim(" \t\r\f\v"u8);

    /// <summary>Returns the next whitespace-separated token and advances <paramref name="rest"/> past it.</summary>
    public static ReadOnlySpan<byte> Next(ref ReadOnlySpan<byte> rest)
    {
        rest = rest.TrimStart(" \t\r\f\v"u8);
        int end = rest.IndexOfAny(" \t\r\f\v"u8);
        if (end < 0)
            end = rest.Length;

        var token = rest[..end];
        rest = rest[end..];
        return token;
    }

    public static float NextFloat(ref ReadOnlySpan<byte> rest)
        => float.TryParse(Next(ref rest), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : float.NaN;
}
