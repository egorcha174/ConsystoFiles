using System.Text;
using Consysto.CadPreview.Inventor;

namespace Consysto.CadPreview;

/// <summary>
/// Which program version wrote a drawing or model: the AutoCAD format of a DWG/DXF, the Inventor release of an Inventor
/// document. Read from the first bytes of the file, so it is cheap enough for a column of a large folder.
/// </summary>
public static class CadVersionReader
{
    private const int DxfHeaderBytes = 256 * 1024;

    public static bool IsSupported(string? extension)
        => extension?.ToLowerInvariant() is ".dwg" or ".dxf" || InventorPropertyReader.IsSupported(extension);

    /// <returns>"AutoCAD 2018", "Inventor 2027"; null when the file does not say.</returns>
    public static string? Read(string path)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".dwg" => ReadDwg(path),
                ".dxf" => ReadDxf(path),
                _ => ReadInventor(path),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A DWG starts with its format code, e.g. "AC1032".</summary>
    private static string? ReadDwg(string path)
    {
        var code = new byte[6];
        using var stream = Open(path);
        return stream.Read(code, 0, code.Length) == code.Length ? AutoCadName(Encoding.ASCII.GetString(code)) : null;
    }

    /// <summary>The header section of a text DXF holds "$ACADVER", then group code 1, then the format code.</summary>
    private static string? ReadDxf(string path)
    {
        using var stream = Open(path);
        var buffer = new byte[DxfHeaderBytes];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        var text = Encoding.Latin1.GetString(buffer, 0, read);
        if (text.StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
            return null;

        var marker = text.IndexOf("$ACADVER", StringComparison.Ordinal);
        if (marker < 0)
            return null;

        var lines = text[(marker + "$ACADVER".Length)..].Split('\n', 4);
        return lines.Length >= 3 ? AutoCadName(lines[2].Trim()) : null;
    }

    /// <summary>Inventor writes "2027 (Build 310192000, 192)" into its design tracking properties.</summary>
    private static string? ReadInventor(string path)
    {
        var written = InventorPropertyReader.Read(path).LastUpdatedWith;
        if (string.IsNullOrWhiteSpace(written))
            return null;

        var release = written.Split(' ', 2)[0];
        return $"Inventor {release}";
    }

    private static FileStream Open(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);

    /// <summary>The release that introduced a format; later releases save in the same format.</summary>
    private static string? AutoCadName(string code)
        => code switch
        {
            "AC1009" => "AutoCAD R12",
            "AC1012" => "AutoCAD R13",
            "AC1014" => "AutoCAD R14",
            "AC1015" => "AutoCAD 2000",
            "AC1018" => "AutoCAD 2004",
            "AC1021" => "AutoCAD 2007",
            "AC1024" => "AutoCAD 2010",
            "AC1027" => "AutoCAD 2013",
            "AC1032" => "AutoCAD 2018",
            _ when code.StartsWith("AC", StringComparison.Ordinal) => code,
            _ => null,
        };
}
