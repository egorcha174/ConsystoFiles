namespace Consysto.Collections;

public enum DuplicateKind
{
    /// <summary>Every file in the group has the same content.</summary>
    IdenticalFiles,

    /// <summary>Copies of one thing in different files: another edition, another archive, an edited copy.</summary>
    SameItem,
}

/// <param name="Items">The copy suggested to keep comes first.</param>
public sealed record DuplicateGroup(DuplicateKind Kind, string Title, IReadOnlyList<CollectionItem> Items);

/// <summary>Identical files are found by their hash; copies of the same thing by the key the kind of collection gives.</summary>
internal static class CollectionDuplicates
{
    public static IReadOnlyList<DuplicateGroup> Find(IReadOnlyList<CollectionItem> items, CollectionKind kind)
    {
        var parent = Enumerable.Range(0, items.Count).ToArray();

        int Root(int index)
        {
            while (parent[index] != index)
            {
                parent[index] = parent[parent[index]];
                index = parent[index];
            }

            return index;
        }

        void JoinBy(Func<CollectionItem, string?> key)
        {
            var first = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < items.Count; index++)
            {
                if (key(items[index]) is not { } value)
                    continue;

                if (first.TryGetValue(value, out var other))
                    parent[Root(index)] = Root(other);
                else
                    first[value] = index;
            }
        }

        JoinBy(item => item.ContentHash is null ? null : $"{item.Size}:{item.ContentHash}");
        JoinBy(kind.SameItemKey);

        return Enumerable.Range(0, items.Count)
            .GroupBy(Root)
            .Where(group => group.Count() > 1)
            .Select(group =>
            {
                var members = group.Select(index => items[index]).ToArray();
                var identical = members.All(item => item.ContentHash is not null && item.ContentHash == members[0].ContentHash);

                // Of identical files the one with the shortest path stays; of different files, the newest
                var ordered = identical
                    ? members.OrderBy(item => item.Path.Length).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    : members.OrderByDescending(item => item.Modified);
                return new DuplicateGroup(identical ? DuplicateKind.IdenticalFiles : DuplicateKind.SameItem, members[0].Title, ordered.ToArray());
            })
            .OrderBy(group => group.Title, CollectionText.Comparer)
            .ToArray();
    }
}
