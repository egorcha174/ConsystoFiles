namespace Consysto.CadPreview.Inventor;

/// <summary>A document an Inventor file is built from: a part of an assembly, or the part a derived part came from.</summary>
/// <param name="RecordedPath">The path as Inventor saved it, which may point at a drive the files have since left.</param>
/// <param name="ResolvedPath">The file as found on this machine, or null when it is not next to the document either.</param>
public sealed record InventorReference(string RecordedPath, string? ResolvedPath)
{
    public string Name => Path.GetFileName(RecordedPath);

    public bool IsFound => ResolvedPath is not null;
}

/// <summary>
/// The documents an ipt/iam/idw/ipn refers to. Inventor keeps them as plain UTF-16 paths in its UFRxDoc stream;
/// the format is not documented, so only whole paths ending in a known extension are taken and anything else is ignored.
/// </summary>
public static class InventorReferenceReader
{
    private const string ReferenceStreamName = "UFRxDoc";
    private const long MaximumStreamBytes = 16 * 1024 * 1024;
    private const int MaximumFoldersSearched = 400;
    private const int MaximumFolderDepth = 3;

    private static readonly string[] DocumentExtensions = [".ipt", ".iam", ".idw", ".ipn"];

    public static bool IsSupported(string? extension)
        => InventorPreviewReader.IsSupported(extension);

    /// <returns>The referenced documents in the order Inventor lists them, without the document itself. Empty when there are none.</returns>
    public static IReadOnlyList<InventorReference> Read(string path)
    {
        var recorded = ReadRecordedPaths(path);
        if (recorded.Count == 0)
            return [];

        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        var byName = new Lazy<Dictionary<string, string>>(() => IndexFolder(folder));
        var ownName = Path.GetFileName(path);

        var references = new List<InventorReference>();
        foreach (var reference in recorded)
        {
            // The document itself is in the list; a file of the same name in another folder is a different document
            if (string.Equals(Path.GetFileName(reference), ownName, StringComparison.OrdinalIgnoreCase))
                continue;

            references.Add(new InventorReference(reference, Resolve(reference, byName)));
        }

        return references;
    }

    private static List<string> ReadRecordedPaths(string path)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Inventor keeps open documents locked for writing; sharing ReadWrite still lets us read them.
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var root = OpenMcdf.RootStorage.Open(file);
            if (!root.TryOpenStream(ReferenceStreamName, out var stream))
                return paths;

            using (stream)
            {
                if (stream.Length > MaximumStreamBytes)
                    return paths;

                var data = new byte[stream.Length];
                stream.ReadExactly(data);
                foreach (var text in ReadStrings(data))
                {
                    if (ExtractDocumentPath(text) is { } documentPath && seen.Add(documentPath))
                        paths.Add(documentPath);
                }
            }
        }
        catch (Exception)
        {
            // References are a best-effort extra: a damaged or foreign compound file simply has none.
            paths.Clear();
        }

        return paths;
    }

    /// <summary>UTF-16 runs at both byte alignments: the records around a path are not always of even length.</summary>
    private static IEnumerable<string> ReadStrings(byte[] data)
        => ReadStrings(data, 0).Concat(ReadStrings(data, 1));

    private static IEnumerable<string> ReadStrings(byte[] data, int start)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = start; i + 1 < data.Length; i += 2)
        {
            char c = (char)(data[i] | (data[i + 1] << 8));
            if (c >= ' ' && c != (char)0x7F)
            {
                builder.Append(c);
                continue;
            }

            if (builder.Length >= 8)
                yield return builder.ToString();

            builder.Clear();
        }

        if (builder.Length >= 8)
            yield return builder.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex DocumentPathPattern = new(
        @"(?:[A-Za-z]:\\|\\\\)[^\x00-\x1f\uFFFF""*?<>|]*?\.(?:ipt|iam|idw|ipn)(?![A-Za-z0-9])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// A whole path to a document inside a run of text. Older files follow the path with a separator (":", a space,
    /// FFFF) in the same run, and the stream also holds notes that merely mention a template file.
    /// </summary>
    private static string? ExtractDocumentPath(string text)
    {
        var match = DocumentPathPattern.Match(text);
        if (!match.Success)
            return null;

        // A note like "Document N:\...\Standard (mm).iam was created using ..." mentions a template, it is not a reference
        if (match.Index > 0 || text.Length - (match.Index + match.Length) > 2)
            return null;

        return match.Value;
    }

    private static string? Resolve(string recordedPath, Lazy<Dictionary<string, string>> byName)
    {
        if (File.Exists(recordedPath))
            return recordedPath;

        // The folder may have been copied to another drive: the same file name next to the document is the same part
        return byName.Value.TryGetValue(Path.GetFileName(recordedPath), out var found) ? found : null;
    }

    /// <summary>
    /// File names of the document's own folder and the folders under it, e.g. the "ДЕТАЛИ" folder of an assembly, plus the
    /// files of the folder above: a derived part in "ДЕТАЛИ" is made from the master part that sits one level up.
    /// </summary>
    private static Dictionary<string, string> IndexFolder(string? folder)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (folder is null || !Directory.Exists(folder))
            return index;

        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true };
        var folders = new Queue<(string Path, int Depth)>();
        folders.Enqueue((folder, 0));
        int searched = 0;

        while (folders.Count > 0 && searched < MaximumFoldersSearched)
        {
            var (current, depth) = folders.Dequeue();
            searched++;
            try
            {
                foreach (var file in Directory.EnumerateFiles(current, "*", options))
                {
                    if (DocumentExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        index.TryAdd(Path.GetFileName(file), file);
                }

                // A document far from its parts is better left unresolved than matched to a namesake across the whole drive
                if (depth >= MaximumFolderDepth)
                    continue;

                foreach (var child in Directory.EnumerateDirectories(current, "*", options))
                {
                    // Inventor's own backups hold older copies of the same names
                    if (!Path.GetFileName(child).Equals("OldVersions", StringComparison.OrdinalIgnoreCase))
                        folders.Enqueue((child, depth + 1));
                }
            }
            catch (Exception)
            {
                // An unreadable folder simply adds nothing
            }
        }

        // The folder above is searched last, so a part next to the document wins over a namesake there
        if (Path.GetDirectoryName(folder) is { } parent && Directory.Exists(parent))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(parent, "*", options))
                {
                    if (DocumentExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        index.TryAdd(Path.GetFileName(file), file);
                }
            }
            catch (Exception)
            {
                // An unreadable folder simply adds nothing
            }
        }

        return index;
    }
}
