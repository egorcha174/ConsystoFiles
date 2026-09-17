namespace Consysto.Collections;

/// <summary>
/// A kind of collection — books, drawings, photos: which files belong to it, what they say about themselves and how they are
/// grouped. The index, the watching of folders, search and duplicates are the same for every kind.
/// </summary>
public abstract class CollectionKind
{
    /// <summary>Stored in the index: an index written by another kind, or another version of this one, is read again from the files.</summary>
    public abstract string Id { get; }

    public virtual int Version => 1;

    public abstract bool Accepts(string fileName);

    /// <summary>Checked after <see cref="Accepts"/> with the full path, for kinds that skip whole folders such as backups.</summary>
    public virtual bool AcceptsPath(string path)
        => true;

    /// <summary>What the file says about itself. May throw for a damaged file, which is then listed by its file name.</summary>
    public abstract ItemMetadata Read(string path);

    /// <summary>The ways to group items, e.g. books by author, series and genre.</summary>
    public virtual IReadOnlyList<CollectionFacet> Facets => [];

    /// <summary>Fields searched besides the title.</summary>
    public virtual IReadOnlyList<string> SearchFields => [];

    /// <summary>
    /// Items with the same key are copies of one thing in different files (another edition, another archive). Null for an item
    /// that should be matched only as an identical file.
    /// </summary>
    public virtual string? SameItemKey(CollectionItem item)
        => null;

    public virtual string TitleFromFileName(string fileName)
        => CollectionText.TitleFromFileName(fileName);

    /// <summary>The order of all items; by title unless the kind knows better (photos by the date they were taken).</summary>
    public virtual string SortKey(CollectionItem item)
        => item.Title;
}

/// <summary>What a file says about itself: a title and named fields, each with one or more values.</summary>
public sealed class ItemMetadata
{
    public string? Title { get; set; }

    public bool HasCover { get; set; }

    public Dictionary<string, IReadOnlyList<string>> Fields { get; } = new(StringComparer.Ordinal);

    /// <summary>Empty values are not stored.</summary>
    public ItemMetadata Set(string field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            Fields[field] = [value];

        return this;
    }

    public ItemMetadata Set(string field, IEnumerable<string?> values)
    {
        var list = values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
        if (list.Length > 0)
            Fields[field] = list;

        return this;
    }
}

/// <summary>A way to group items by the values of one field.</summary>
public sealed record CollectionFacet(string Id, string Field)
{
    /// <summary>How a value is shown and grouped, e.g. authors surname first.</summary>
    public Func<string, string>? DisplayName { get; init; }

    /// <summary>The order of the items of one group; by title when not set.</summary>
    public Func<IEnumerable<CollectionItem>, IEnumerable<CollectionItem>>? Order { get; init; }
}
