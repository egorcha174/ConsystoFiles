using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Consysto.CadPreview.Artwork;

/// <summary>
/// Rewrites an SVG into the part the system SVG engine (Direct2D, through Win2D) reads.
///
/// That engine knows shapes and presentation attributes but neither style sheets nor a DOCTYPE. CorelDRAW, Inkscape
/// and Illustrator write exactly that: <c>&lt;!DOCTYPE svg …&gt;</c> and fills given by classes in a
/// <c>&lt;style&gt;</c> block (<c>.fil0 {fill:#1A1A1A}</c>). Such a file did not load at all (08.10.2026: a CorelDRAW
/// wolf showed «not supported»). Here the DOCTYPE is dropped and every class or <c>style</c> declaration of a known
/// presentation property becomes an attribute, with the CSS order of precedence: attribute &lt; class &lt; style.
/// </summary>
public static partial class SvgNormalizer
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    // Presentation properties Direct2D understands as attributes
    private static readonly HashSet<string> Presentation = new(StringComparer.OrdinalIgnoreCase)
    {
        "fill", "fill-opacity", "fill-rule", "stroke", "stroke-width", "stroke-opacity", "stroke-linecap",
        "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "opacity", "display",
        "visibility", "color", "clip-rule", "stop-color", "stop-opacity", "overflow",
    };

    public static string Normalize(string markup)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        XDocument document;
        using (var reader = XmlReader.Create(new StringReader(markup), settings))
            document = XDocument.Load(reader, LoadOptions.None);

        var rules = new List<(string Selector, Dictionary<string, string> Declarations)>();
        foreach (var style in document.Descendants().Where(e => e.Name.LocalName == "style").ToList())
        {
            rules.AddRange(ParseStyleSheet(style.Value));
            style.Remove();
        }

        foreach (var element in document.Descendants())
        {
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Rules by element name first, then by class — later and more specific wins, as in CSS
            foreach (var (selector, declarations) in rules)
                if (!selector.StartsWith('.') && selector.Equals(element.Name.LocalName, StringComparison.OrdinalIgnoreCase))
                    Merge(resolved, declarations);
            if (element.Attribute("class")?.Value is { } classes)
                foreach (var name in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    foreach (var (selector, declarations) in rules)
                        if (selector == "." + name)
                            Merge(resolved, declarations);
            if (element.Attribute("style")?.Value is { } inline)
            {
                Merge(resolved, ParseDeclarations(inline));
                element.Attribute("style")!.Remove();
            }

            foreach (var (property, value) in resolved)
                if (Presentation.Contains(property))
                    element.SetAttributeValue(property, value);
        }

        if (document.Root is { } root)
            FitToViewport(root);

        document.DocumentType?.Remove();
        return document.Root?.ToString(SaveOptions.DisableFormatting) ?? markup;
    }

    /// <summary>
    /// A size of its own («1065.68mm») makes the engine draw the picture at that size and the preview shows a corner
    /// of it. The picture is fitted to the asked square instead: its viewBox is kept (or made from width and height)
    /// and the root fills the viewport.
    /// </summary>
    private static void FitToViewport(XElement root)
    {
        if (root.Attribute("viewBox") is null
            && Length(root.Attribute("width")?.Value) is { } width && Length(root.Attribute("height")?.Value) is { } height)
            root.SetAttributeValue("viewBox", FormattableString.Invariant($"0 0 {width} {height}"));
        if (root.Attribute("viewBox") is null)
            return;
        root.SetAttributeValue("width", "100%");
        root.SetAttributeValue("height", "100%");
    }

    // The number of a length such as "160mm" or "300px"; a percentage says nothing about the picture
    private static double? Length(string? value)
    {
        if (value is null || value.Contains('%'))
            return null;
        var number = LengthPattern().Match(value);
        return number.Success && double.TryParse(number.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) && result > 0 ? result : null;
    }

    [GeneratedRegex(@"^\s*[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?")]
    private static partial Regex LengthPattern();

    private static void Merge(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        foreach (var (key, value) in source)
            target[key] = value;
    }

    private static IEnumerable<(string, Dictionary<string, string>)> ParseStyleSheet(string css)
    {
        css = CommentPattern().Replace(css, "");
        foreach (Match rule in RulePattern().Matches(css))
        {
            var declarations = ParseDeclarations(rule.Groups["body"].Value);
            foreach (var selector in rule.Groups["selectors"].Value.Split(','))
            {
                var trimmed = selector.Trim();
                // Only plain ".class" and "element" selectors; anything fancier is left to the engine's defaults
                if (trimmed.Length > 0 && SimpleSelector().IsMatch(trimmed))
                    yield return (trimmed, declarations);
            }
        }
    }

    private static Dictionary<string, string> ParseDeclarations(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split(';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0)
                continue;
            var name = part[..colon].Trim();
            var value = part[(colon + 1)..].Replace("!important", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (name.Length > 0 && value.Length > 0)
                result[name] = value;
        }
        return result;
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentPattern();

    [GeneratedRegex(@"(?<selectors>[^{}@]+)\{(?<body>[^{}]*)\}")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^\.?[A-Za-z_][\w\-]*$")]
    private static partial Regex SimpleSelector();
}
