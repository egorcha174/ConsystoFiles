using System.Collections.Concurrent;

namespace Consysto.Collections;

/// <summary>A named set of items: an author, a series, a camera. <see cref="Key"/> is the normalized name.</summary>
public sealed record CollectionGroup(string Key, string Name, IReadOnlyList<CollectionItem> Items);

/// <summary>A subfolder that holds items, directly or deeper. <see cref="RelativePath"/> uses "/".</summary>
public sealed record CollectionFolder(string RelativePath, string Name, int ItemCount);

/// <summary>The collection as one scan left it. It never changes, so a page is built from one consistent state.</summary>
public sealed class CollectionSnapshot
{
    private readonly CollectionKind? kind;
    private readonly Lazy<Dictionary<string, CollectionItem>> byId;
    private readonly Lazy<IReadOnlyList<CollectionItem>> newest;
    private readonly Lazy<IReadOnlyList<DuplicateGroup>> duplicates;
    private readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<CollectionGroup>>> facets = new(StringComparer.Ordinal);

    public static CollectionSnapshot Empty { get; } = new(null, [], [], null);

    internal CollectionSnapshot(CollectionKind? kind, IEnumerable<CollectionItem> items, IReadOnlyList<string> roots, DateTime? scanned)
    {
        this.kind = kind;
        Items = items
            .OrderBy(item => kind?.SortKey(item) ?? item.Title, CollectionText.Comparer)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Roots = roots;
        Scanned = scanned;

        byId = new(() => Items.GroupBy(item => item.Id).ToDictionary(group => group.Key, group => group.First()));
        newest = new(() => Items.OrderByDescending(item => item.Added).ToArray());
        duplicates = new(() => kind is null ? [] : CollectionDuplicates.Find(Items, kind));
    }

    /// <summary>By title.</summary>
    public IReadOnlyList<CollectionItem> Items { get; }

    /// <summary>Most recently added first.</summary>
    public IReadOnlyList<CollectionItem> Newest => newest.Value;

    public IReadOnlyList<string> Roots { get; }

    /// <summary>End of the scan, UTC; null before the first scan finished.</summary>
    public DateTime? Scanned { get; }

    /// <summary>Identical files, and copies of the same thing as the kind of collection defines it.</summary>
    public IReadOnlyList<DuplicateGroup> Duplicates => duplicates.Value;

    /// <summary>The groups of one of the kind's facets, by name; empty for an unknown facet.</summary>
    public IReadOnlyList<CollectionGroup> Facet(string id)
    {
        if (kind?.Facets.FirstOrDefault(facet => facet.Id == id) is not { } definition)
            return [];

        return facets.GetOrAdd(id, _ => new(() => Group(definition))).Value;
    }

    public CollectionItem? Find(string id)
        => byId.Value.GetValueOrDefault(id);

    /// <summary>Items (of <paramref name="items"/>, else all) whose title or searched fields contain every word of the query; all of them for an empty query.</summary>
    public IReadOnlyList<CollectionItem> Search(string? query, IReadOnlyList<CollectionItem>? items = null)
    {
        items ??= Items;
        var terms = Terms(query);
        return terms.Length == 0
            ? items
            : items.Where(item => terms.All(term => item.SearchText.Contains(term, StringComparison.Ordinal))).ToArray();
    }

    /// <summary>Whether a name (an author, a series) contains every word of the query.</summary>
    public static bool Matches(string name, string? query)
    {
        var normalized = CollectionText.Normalize(name);
        return Terms(query).All(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    public int CountInRoot(int rootIndex)
        => rootIndex >= 0 && rootIndex < Roots.Count ? Items.Count(item => string.Equals(item.Root, Roots[rootIndex], StringComparison.OrdinalIgnoreCase)) : 0;

    /// <summary>The subfolders and the items directly inside a folder of a collection folder (relative path with "/", empty for the collection folder itself).</summary>
    public (IReadOnlyList<CollectionFolder> Folders, IReadOnlyList<CollectionItem> Items) GetFolder(int rootIndex, string relativePath)
    {
        if (rootIndex < 0 || rootIndex >= Roots.Count)
            return ([], []);

        var root = Roots[rootIndex];
        var relative = relativePath.Replace('\\', '/').Trim('/');
        var directory = relative.Length == 0 ? root : Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;

        var folders = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CollectionItem>();
        foreach (var item in Items)
        {
            if (!string.Equals(item.Root, root, StringComparison.OrdinalIgnoreCase) || !item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = item.Path[prefix.Length..];
            var separator = rest.IndexOf(Path.DirectorySeparatorChar);
            if (separator < 0)
            {
                items.Add(item);
            }
            else
            {
                var name = rest[..separator];
                folders[name] = folders.GetValueOrDefault(name) + 1;
            }
        }

        return (
            folders
                .OrderBy(pair => pair.Key, CollectionText.Comparer)
                .Select(pair => new CollectionFolder(relative.Length == 0 ? pair.Key : $"{relative}/{pair.Key}", pair.Key, pair.Value))
                .ToArray(),
            items);
    }

    private IReadOnlyList<CollectionGroup> Group(CollectionFacet facet)
        => Items
            .SelectMany(item => item.Values(facet.Field).Select(value => (Name: facet.DisplayName?.Invoke(value) ?? value, Item: item)))
            .GroupBy(pair => CollectionText.Normalize(pair.Name))
            .Where(group => group.Key.Length > 0)
            .Select(group =>
            {
                var members = group.Select(pair => pair.Item).Distinct();
                return new CollectionGroup(group.Key, group.First().Name, (facet.Order?.Invoke(members) ?? members).ToArray());
            })
            .OrderBy(group => group.Name, CollectionText.Comparer)
            .ToArray();

    private static string[] Terms(string? query)
        => CollectionText.Normalize(query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
