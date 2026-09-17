using System.Globalization;
using System.Text.Json;

namespace Consysto.BookPreview.Opds;

/// <summary>OPDS 2.0: feeds with navigation, publications and groups, and single publication documents.</summary>
internal static class OpdsJsonParser
{
    public static OpdsFeed Parse(JsonDocument document, Uri address)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The catalog page is not a JSON object.");

        var feed = new OpdsFeed { Address = address };
        if (root.TryGetProperty("metadata", out var metadata))
        {
            feed.Title = TextCleanup.Clean(LocalizedString(metadata, "title"));
            feed.Subtitle = TextCleanup.Clean(LocalizedString(metadata, "subtitle"));
        }

        foreach (var link in Items(root, "links"))
        {
            var href = String(link, "href");
            if (href is null)
                continue;

            var relations = Relations(link);
            var templated = link.TryGetProperty("templated", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (relations.Contains("search"))
            {
                if (templated)
                    feed.SearchTemplate ??= OpdsSearch.FromUriTemplate(address, href);
                else if (String(link, "type")?.Contains("opensearchdescription", StringComparison.OrdinalIgnoreCase) == true)
                    feed.SearchDescription ??= OpdsUri.Resolve(address, href);

                continue;
            }

            if (templated)
                continue;

            if (relations.Contains("next"))
                feed.NextPage ??= OpdsUri.Resolve(address, href);
            else if (relations.Contains("previous") || relations.Contains("prev"))
                feed.PreviousPage ??= OpdsUri.Resolve(address, href);
            else if (relations.Contains("first") || relations.Contains("start"))
                feed.StartPage ??= OpdsUri.Resolve(address, href);
            else if (relations.Contains("up"))
                feed.UpPage ??= OpdsUri.Resolve(address, href);
        }

        AddEntries(feed, root, group: null);
        foreach (var group in Items(root, "groups"))
            AddEntries(feed, group, group.TryGetProperty("metadata", out var groupMetadata) ? TextCleanup.Clean(LocalizedString(groupMetadata, "title")) : null);

        // A publication document describes one book with its own acquisition links
        if (feed.Entries.Count == 0 && Items(root, "links").Any(link => Relations(link).Any(IsAcquisition)))
            feed.Entries.Add(ParsePublication(root, address, group: null));

        return feed;
    }

    private static void AddEntries(OpdsFeed feed, JsonElement container, string? group)
    {
        foreach (var navigation in Items(container, "navigation"))
        {
            if (OpdsUri.Resolve(feed.Address, String(navigation, "href")) is not { } target)
                continue;

            feed.Entries.Add(new OpdsEntry
            {
                Title = TextCleanup.Clean(LocalizedString(navigation, "title")),
                Navigation = target,
                Group = group,
            });
        }

        foreach (var publication in Items(container, "publications"))
            feed.Entries.Add(ParsePublication(publication, feed.Address, group));
    }

    private static OpdsEntry ParsePublication(JsonElement publication, Uri address, string? group)
    {
        var entry = new OpdsEntry { Group = group };

        if (publication.TryGetProperty("metadata", out var metadata))
        {
            entry.Id = String(metadata, "identifier");
            entry.Title = TextCleanup.Clean(LocalizedString(metadata, "title"));
            foreach (var author in Contributors(metadata, "author"))
                TextCleanup.AddTo(entry.Authors, author);

            entry.Summary = TextCleanup.FromHtml(String(metadata, "description"));
            entry.Language = metadata.TryGetProperty("language", out var language)
                ? TextCleanup.Clean(language.ValueKind == JsonValueKind.Array ? language.EnumerateArray().Select(AsString).FirstOrDefault() : AsString(language))
                : null;
            entry.Year = TextCleanup.Year(String(metadata, "published"));
            entry.Publisher = Contributors(metadata, "publisher").FirstOrDefault();

            foreach (var subject in Items(metadata, "subject"))
                TextCleanup.AddTo(entry.Categories, TextCleanup.Clean(subject.ValueKind == JsonValueKind.Object ? LocalizedString(subject, "name") : AsString(subject)));

            if (metadata.TryGetProperty("belongsTo", out var belongsTo) && belongsTo.ValueKind == JsonValueKind.Object)
            {
                var series = Items(belongsTo, "series").FirstOrDefault();
                if (series.ValueKind == JsonValueKind.Object)
                {
                    entry.Series = TextCleanup.Clean(LocalizedString(series, "name"));
                    if (series.TryGetProperty("position", out var position))
                        entry.SeriesIndex = TextCleanup.SeriesNumber(position.ValueKind == JsonValueKind.Number ? position.GetDouble().ToString(CultureInfo.InvariantCulture) : AsString(position));
                }
                else if (series.ValueKind == JsonValueKind.String)
                {
                    entry.Series = TextCleanup.Clean(series.GetString());
                }
            }
        }

        foreach (var link in Items(publication, "links"))
        {
            if (OpdsUri.Resolve(address, String(link, "href")) is not { } target)
                continue;

            var relations = Relations(link);
            var type = String(link, "type") ?? string.Empty;
            if (relations.FirstOrDefault(IsAcquisition) is { } relation)
            {
                var mediaType = IndirectType(link) ?? type;
                entry.Acquisitions.Add(new(target, mediaType, OpdsBookFormat.FromMediaType(mediaType, target), OpdsAtomParser.KindOf(relation)));
            }
            else if (relations.Contains("self") && OpdsAtomParser.IsCatalogType(type))
            {
                entry.Navigation ??= target;
            }
            else if (OpdsAtomParser.IsCatalogType(type) && !relations.Contains("self"))
            {
                entry.Related.Add(new(TextCleanup.Clean(String(link, "title")) ?? relations.FirstOrDefault() ?? target.Host, target));
            }
        }

        // The first image is the cover; the narrowest one serves as the thumbnail
        var images = Items(publication, "images").Where(image => String(image, "href") is not null).ToList();
        if (images.Count > 0)
        {
            entry.Image = OpdsUri.ResolveImage(address, String(images[0], "href"));
            var smallest = images.OrderBy(image => image.TryGetProperty("width", out var width) && width.TryGetInt32(out var pixels) ? pixels : int.MaxValue).First();
            entry.Thumbnail = OpdsUri.ResolveImage(address, String(smallest, "href"));
        }

        return entry;
    }

    private static bool IsAcquisition(string relation)
        => relation.StartsWith("http://opds-spec.org/acquisition", StringComparison.Ordinal);

    private static string? IndirectType(JsonElement link)
    {
        string? type = null;
        var current = link;
        while (current.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            && Items(properties, "indirectAcquisition").FirstOrDefault() is { ValueKind: JsonValueKind.Object } indirect)
        {
            type = String(indirect, "type") ?? type;
            current = indirect;
            if (!indirect.TryGetProperty("child", out var child) || child.ValueKind != JsonValueKind.Array)
                break;

            var next = child.EnumerateArray().FirstOrDefault();
            if (next.ValueKind != JsonValueKind.Object)
                break;

            type = String(next, "type") ?? type;
            current = next;
        }

        return type;
    }

    private static List<string> Relations(JsonElement link)
    {
        if (!link.TryGetProperty("rel", out var relation))
            return [];

        return relation.ValueKind switch
        {
            JsonValueKind.String => [relation.GetString()!],
            JsonValueKind.Array => [.. relation.EnumerateArray().Select(AsString).OfType<string>()],
            _ => [],
        };
    }

    private static IEnumerable<string> Contributors(JsonElement metadata, string property)
    {
        if (!metadata.TryGetProperty(property, out var value))
            yield break;

        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : [value];
        foreach (var item in items)
        {
            var name = item.ValueKind == JsonValueKind.Object ? LocalizedString(item, "name") : AsString(item);
            if (TextCleanup.Clean(name) is { } clean)
                yield return clean;
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return [];

        return value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [value];
    }

    private static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? AsString(value) : null;

    /// <summary>Titles may be plain strings or language maps ({"en": "...", "fr": "..."}).</summary>
    private static string? LocalizedString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind == JsonValueKind.Object
            ? value.EnumerateObject().Select(p => AsString(p.Value)).FirstOrDefault(s => s is not null)
            : AsString(value);
    }

    private static string? AsString(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
}
