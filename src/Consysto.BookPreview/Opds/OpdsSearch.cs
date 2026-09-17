using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Consysto.BookPreview.Opds;

/// <summary>Search templates: OpenSearch descriptions (OPDS 1) and RFC 6570 link templates (OPDS 2), both reduced to {searchTerms}.</summary>
internal static partial class OpdsSearch
{
    public const string Placeholder = "{searchTerms}";

    // Survives URI resolution, which would escape the braces of the placeholder
    private const string Marker = "CONSYSTOSEARCHTERMS";

    [GeneratedRegex(@"\{\?([^}]*)\}")]
    private static partial Regex FormQueryExpansion();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Variable();

    /// <summary>Resolves an OpenSearch-style template against the page it came from; parameters other than the search terms are dropped.</summary>
    public static string? ResolveTemplate(Uri address, string template)
    {
        var marked = template.Replace(Placeholder, Marker, StringComparison.Ordinal);
        marked = Variable().Replace(marked, match => match.Value is "{startPage}" or "{startIndex}" ? "1" : string.Empty);
        return OpdsUri.Resolve(address, marked)?.AbsoluteUri.Replace(Marker, Placeholder, StringComparison.Ordinal);
    }

    /// <summary>OPDS 2 search links use form-style query expansion: <c>/search{?query,title,author}</c>.</summary>
    public static string? FromUriTemplate(Uri address, string template)
    {
        var expanded = FormQueryExpansion().Replace(template, match =>
        {
            var names = match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var name = names.FirstOrDefault(n => n is "query" or "q" or "searchTerms" or "search" or "term") ?? names.FirstOrDefault();
            if (name is null)
                return string.Empty;

            var separator = template.IndexOf('?') is var question and >= 0 && question < match.Index ? "&" : "?";
            return $"{separator}{name}={Placeholder}";
        });

        expanded = expanded.Replace("{query}", Placeholder, StringComparison.Ordinal);
        return expanded.Contains(Placeholder, StringComparison.Ordinal) ? ResolveTemplate(address, expanded) : null;
    }

    public static string? ParseDescription(XDocument document, Uri address)
    {
        var urls = document.Root?.Elements().Where(e => e.Name.LocalName == "Url").ToList() ?? [];
        string? TypeOf(XElement url) => (string?)url.Attribute("type");

        var url = urls.FirstOrDefault(u => TypeOf(u)?.Contains("opds-catalog", StringComparison.OrdinalIgnoreCase) == true)
            ?? urls.FirstOrDefault(u => TypeOf(u)?.Contains("atom+xml", StringComparison.OrdinalIgnoreCase) == true)
            ?? urls.FirstOrDefault(u => TypeOf(u)?.Contains("opds+json", StringComparison.OrdinalIgnoreCase) == true);

        var template = (string?)url?.Attribute("template");
        return template is not null && template.Contains(Placeholder, StringComparison.Ordinal) ? ResolveTemplate(address, template) : null;
    }

    public static Uri BuildAddress(string template, string terms)
        => new(template.Replace(Placeholder, Uri.EscapeDataString(terms.Trim()), StringComparison.Ordinal));
}

internal static class OpdsUri
{
    public static Uri? Resolve(Uri address, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        try
        {
            var target = new Uri(address, href.Trim());
            return target.Scheme is "http" or "https" or "file" ? target : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Images may be embedded as data: URIs, which stay as they are.</summary>
    public static string? ResolveImage(Uri address, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;

        return href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? href.Trim() : Resolve(address, href)?.AbsoluteUri;
    }
}
