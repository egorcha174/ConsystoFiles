using System.Security.Cryptography;
using System.Text;

namespace Consysto.Collections;

/// <summary>A file found in a collection folder, with what it says about itself.</summary>
public sealed class CollectionItem
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoFields = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>Derived from the path, so it stays the same between scans; used in addresses.</summary>
    public required string Id { get; init; }

    public required string Path { get; init; }

    /// <summary>The collection folder the file was found in.</summary>
    public required string Root { get; init; }

    public required long Size { get; init; }

    /// <summary>Last write time, UTC.</summary>
    public required DateTime Modified { get; init; }

    /// <summary>When the file appeared in the collection, UTC: a copied file gets a new creation time, a downloaded one a new write time.</summary>
    public required DateTime Added { get; init; }

    /// <summary>The title written in the file, else the file name.</summary>
    public required string Title { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Fields { get; init; } = NoFields;

    /// <summary>The file holds a picture of itself, or the host draws one.</summary>
    public bool HasCover { get; init; }

    /// <summary>SHA-256 of the file, computed only when another item has the same size: identical copies are found by it.</summary>
    public string? ContentHash { get; internal set; }

    /// <summary>
    /// Hash of the start and end of the file. Files of equal size are compared by it first, and only those that also match
    /// here are hashed in full, which keeps a catalogue of many same-sized drawings from reading gigabytes.
    /// </summary>
    public string? SampleHash { get; internal set; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Lower case; ".fb2.zip" and ".tar.gz" count as one extension.</summary>
    public string Extension => CollectionText.Extension(Path);

    internal string SearchText { get; set; } = string.Empty;

    public IReadOnlyList<string> Values(string field)
        => Fields.TryGetValue(field, out var values) ? values : [];

    public string? Value(string field)
        => Values(field) is [var first, ..] ? first : null;

    public static string IdOf(string path)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16].ToLowerInvariant();
}
