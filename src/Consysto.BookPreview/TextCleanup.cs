using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Consysto.BookPreview;

internal static partial class TextCleanup
{
    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\d{4}")]
    private static partial Regex FourDigits();

    /// <summary>Single line, collapsed spaces; null when nothing is left.</summary>
    public static string? Clean(string? value)
    {
        if (value is null)
            return null;

        var text = Spaces().Replace(value.Replace('\r', ' ').Replace('\n', ' '), " ").Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>Descriptions stored as HTML (EPUB, Kindle) turned into plain paragraphs.</summary>
    public static string? FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var text = BlockBreak().Replace(html, "\n");
        text = Tag().Replace(text, "");
        return Paragraphs(WebUtility.HtmlDecode(text));
    }

    /// <summary>Trims every line and keeps at most one empty line between paragraphs.</summary>
    public static string? Paragraphs(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var result = new StringBuilder();
        var blankLine = false;
        foreach (var rawLine in text.Replace("\r", "").Split('\n'))
        {
            var line = Spaces().Replace(rawLine, " ").Trim();
            if (line.Length == 0)
            {
                blankLine = result.Length > 0;
                continue;
            }

            if (result.Length > 0)
                result.Append(blankLine ? "\n\n" : "\n");

            result.Append(line);
            blankLine = false;
        }

        return result.Length == 0 ? null : result.ToString();
    }

    /// <summary>The first plausible four-digit year; calibre's "undefined" 0101 and similar are dropped.</summary>
    public static string? Year(string? value)
    {
        if (value is null)
            return null;

        var match = FourDigits().Match(value);
        return match.Success && int.Parse(match.Value, CultureInfo.InvariantCulture) >= 1000 ? match.Value : null;
    }

    public static string? Isbn(string? value)
    {
        var text = Clean(value);
        if (text is null)
            return null;

        text = text.Split([',', ';'], 2)[0].Trim();
        foreach (var prefix in (string[])["urn:isbn:", "isbn:", "isbn"])
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }

        var digits = text.Count(c => char.IsAsciiDigit(c) || c is 'X' or 'x');
        return digits is 10 or 13 ? text : null;
    }

    /// <summary>Series position: "3.0" becomes "3"; zero, which FictionBook tools write for "unknown", becomes null.</summary>
    public static string? SeriesNumber(string? value)
    {
        var text = Clean(value);
        if (text is null)
            return null;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return text;

        if (number <= 0)
            return null;

        return number == Math.Floor(number)
            ? ((long)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString(CultureInfo.InvariantCulture);
    }

    public static void AddTo(List<string> list, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
            list.Add(value);
    }
}
