using System.Globalization;
using System.Text;

namespace Consysto.Collections;

/// <summary>Comparing, grouping and searching names the way a Russian reader expects.</summary>
public static class CollectionText
{
    private static readonly string[] CompoundExtensions = [".fb2.zip", ".tar.gz"];

    public static readonly StringComparer Comparer = CreateComparer();

    /// <summary>Upper case, "Ё" as "Е", punctuation as single spaces: the key names are grouped and searched by.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (!char.IsLetterOrDigit(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
                builder.Append(' ');

            pendingSpace = false;
            var upper = char.ToUpperInvariant(character);
            builder.Append(upper == 'Ё' ? 'Е' : upper);
        }

        return builder.ToString();
    }

    /// <summary>"Лев Толстой" → "Толстой Лев": people are listed by surname. "Толстой, Лев" only loses the comma.</summary>
    public static string SurnameFirst(string name)
    {
        var comma = name.IndexOf(',');
        if (comma > 0)
            return $"{name[..comma].Trim()} {name[(comma + 1)..].Trim()}".Trim();

        var space = name.LastIndexOf(' ');
        return space > 0 ? $"{name[(space + 1)..]} {name[..space]}" : name;
    }

    /// <summary>The index letter of a normalized key; digits and symbols share "#".</summary>
    public static string Letter(string key)
        => key.Length > 0 && char.IsLetter(key[0]) ? key[..1] : "#";

    /// <summary>Lower case; ".fb2.zip" and ".tar.gz" count as one extension.</summary>
    public static string Extension(string path)
    {
        foreach (var extension in CompoundExtensions)
        {
            if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                return extension;
        }

        return Path.GetExtension(path).ToLowerInvariant();
    }

    public static string TitleFromFileName(string fileName)
    {
        var extension = Extension(fileName);
        var name = extension.Length > 0 && fileName.Length > extension.Length ? fileName[..^extension.Length] : fileName;
        return name.Replace('_', ' ').Trim();
    }

    /// <summary>A number written as text ("2.5", "3") for ordering; items without one go last.</summary>
    public static double NumberOrder(string? number)
        => double.TryParse(number?.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.MaxValue;

    private static StringComparer CreateComparer()
    {
        try
        {
            return StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), CompareOptions.IgnoreCase);
        }
        catch (CultureNotFoundException)
        {
            // Invariant globalization mode
            return StringComparer.OrdinalIgnoreCase;
        }
    }
}
