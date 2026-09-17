using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Consysto.Collections;

/// <summary>
/// The items of one collection in a set of folders. Files are neither moved nor changed: what they say about themselves is kept
/// in an index file keyed by path, size and write time, so a scan reads only new and changed files. Folders are watched, and a
/// change schedules a scan a few seconds after the last event.
/// </summary>
public sealed class CollectionIndex : IDisposable
{
    private const int IndexVersion = 2;
    private static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PartialSaveInterval = TimeSpan.FromSeconds(20);

    private readonly CollectionKind kind;
    private readonly string? indexPath;
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly Lock watcherLock = new();
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly Timer changeTimer;
    private readonly CancellationTokenSource lifetime = new();
    private Dictionary<string, CollectionItem> known = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> roots = [];
    private bool indexLoaded;
    private int scanning;
    private CollectionScanProgress? progress;
    private long lastProgressTicks;

    /// <param name="indexPath">Where the index is kept between runs; null keeps it in memory only.</param>
    public CollectionIndex(CollectionKind kind, string? indexPath = null)
    {
        this.kind = kind;
        this.indexPath = indexPath;
        changeTimer = new(_ => _ = ScanAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public CollectionKind Kind => kind;

    public CollectionSnapshot Snapshot { get; private set; } = CollectionSnapshot.Empty;

    public IReadOnlyList<string> Roots => roots;

    public bool IsScanning => Volatile.Read(ref scanning) > 0;

    /// <summary>Raised on a worker thread when a scan starts and ends.</summary>
    public event EventHandler? Changed;

    /// <summary>What the running scan is doing; null when no scan runs.</summary>
    public CollectionScanProgress? Progress => Volatile.Read(ref progress);

    /// <summary>Raised on a worker thread, at most a few times a second, while a scan runs.</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>Replaces the collection folders, watches them and starts a scan.</summary>
    public void SetRoots(IEnumerable<string> folders)
    {
        roots = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => Path.GetFullPath(folder.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        lock (watcherLock)
        {
            StopWatchers();
            StartWatchers(roots);
        }

        _ = ScanAsync();
    }

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        try
        {
            await scanGate.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Interlocked.Increment(ref scanning);
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await Task.Run(() => Scan(linked.Token), linked.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Volatile.Write(ref progress, null);
            Interlocked.Decrement(ref scanning);
            scanGate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        changeTimer.Dispose();
        lock (watcherLock)
            StopWatchers();
    }

    private void Scan(CancellationToken cancellationToken)
    {
        var currentRoots = roots;
        if (!indexLoaded)
        {
            indexLoaded = true;
            known = LoadIndex();
            Publish(currentRoots, known.Values, null);
        }

        var files = new List<(string Root, FileInfo File)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (var root in currentRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    foreach (var file in new DirectoryInfo(root).EnumerateFiles("*", enumeration))
                    {
                        if (kind.Accepts(file.Name) && kind.AcceptsPath(file.FullName) && seen.Add(file.FullName))
                            files.Add((root, file));

                        Report(CollectionScanStage.Listing, files.Count, 0, file.DirectoryName, force: false);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A drive went away during the scan; its items drop out until it is back
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        var previous = known;
        var items = new ConcurrentBag<CollectionItem>();
        var readCount = 0;
        var doneCount = 0;
        var lastSave = DateTime.UtcNow;
        var saveLock = new Lock();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken };
        Report(CollectionScanStage.Reading, 0, files.Count, null, force: true);
        Parallel.ForEach(files, parallel, entry =>
        {
            var (root, file) = entry;
            if (previous.TryGetValue(file.FullName, out var item)
                && item.Size == file.Length
                && item.Modified == file.LastWriteTimeUtc
                && string.Equals(item.Root, root, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(item);
                Report(CollectionScanStage.Reading, Interlocked.Increment(ref doneCount), files.Count, file.FullName, force: false);
                return;
            }

            Report(CollectionScanStage.Reading, Volatile.Read(ref doneCount), files.Count, file.FullName, force: false);
            items.Add(ReadItem(root, file));
            Interlocked.Increment(ref readCount);
            Interlocked.Increment(ref doneCount);

            // A long first scan keeps what it has read, so closing the app does not start it over, and shows it meanwhile
            if (DateTime.UtcNow - lastSave > PartialSaveInterval && saveLock.TryEnter())
            {
                try
                {
                    if (DateTime.UtcNow - lastSave > PartialSaveInterval)
                    {
                        var partial = new Dictionary<string, CollectionItem>(previous, StringComparer.OrdinalIgnoreCase);
                        foreach (var read in items.ToArray())
                            partial[read.Path] = read;
                        SaveIndex(partial.Values);
                        Publish(currentRoots, partial.Values, null);
                        Changed?.Invoke(this, EventArgs.Empty);
                        lastSave = DateTime.UtcNow;
                    }
                }
                finally
                {
                    saveLock.Exit();
                }
            }
        });

        var current = new Dictionary<string, CollectionItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            current.TryAdd(item.Path, item);

        // Identical copies share a size: those files are sampled at both ends, and only matching samples are hashed in full
        var unsampled = current.Values
            .GroupBy(item => item.Size)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Where(item => item.SampleHash is null)
            .ToArray();
        var comparedCount = 0;
        Report(CollectionScanStage.Comparing, 0, unsampled.Length, null, force: true);
        Parallel.ForEach(unsampled, parallel, item =>
        {
            Report(CollectionScanStage.Comparing, Volatile.Read(ref comparedCount), unsampled.Length, item.Path, force: false);
            item.SampleHash = SampleHashOf(item.Path, item.Size);
            Interlocked.Increment(ref comparedCount);
        });

        var unhashed = current.Values
            .Where(item => item.SampleHash is not null)
            .GroupBy(item => (item.Size, item.SampleHash))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .Where(item => item.ContentHash is null)
            .ToArray();
        var hashedCount = 0;
        Report(CollectionScanStage.Comparing, 0, unhashed.Length, null, force: true);
        Parallel.ForEach(unhashed, parallel, item =>
        {
            Report(CollectionScanStage.Comparing, Volatile.Read(ref hashedCount), unhashed.Length, item.Path, force: false);
            item.ContentHash = HashOf(item.Path);
            Interlocked.Increment(ref hashedCount);
        });

        known = current;
        Report(CollectionScanStage.Saving, current.Count, current.Count, null, force: true);
        if (readCount > 0 || unsampled.Length > 0 || unhashed.Length > 0 || current.Count != previous.Count || !current.Keys.All(previous.ContainsKey))
            SaveIndex(current.Values);

        Publish(currentRoots, current.Values, DateTime.UtcNow);
    }

    /// <summary>Progress goes out a few times a second; a change of stage always does.</summary>
    private void Report(CollectionScanStage stage, int done, int total, string? path, bool force)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref lastProgressTicks);
        if (!force && (now - last < ProgressInterval.Ticks || Interlocked.CompareExchange(ref lastProgressTicks, now, last) != last))
            return;
        if (force)
            Interlocked.Exchange(ref lastProgressTicks, now);

        Volatile.Write(ref progress, new CollectionScanProgress(stage, done, total, path));
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Publish(IReadOnlyList<string> currentRoots, IEnumerable<CollectionItem> items, DateTime? scanned)
    {
        var rootSet = new HashSet<string>(currentRoots, StringComparer.OrdinalIgnoreCase);
        Snapshot = new CollectionSnapshot(kind, items.Where(item => rootSet.Contains(item.Root)), currentRoots, scanned);
    }

    private CollectionItem ReadItem(string root, FileInfo file)
    {
        ItemMetadata? metadata = null;
        try
        {
            metadata = kind.Read(file.FullName);
        }
        catch (Exception)
        {
            // A damaged file is still listed, by its file name
        }

        return Prepare(new CollectionItem
        {
            Id = CollectionItem.IdOf(file.FullName),
            Path = file.FullName,
            Root = root,
            Size = file.Length,
            Modified = file.LastWriteTimeUtc,
            Added = file.CreationTimeUtc > file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc,
            Title = string.IsNullOrWhiteSpace(metadata?.Title) ? kind.TitleFromFileName(file.Name) : metadata.Title,
            Fields = metadata?.Fields ?? new Dictionary<string, IReadOnlyList<string>>(),
            HasCover = metadata?.HasCover == true,
        });
    }

    private CollectionItem Prepare(CollectionItem item)
    {
        item.SearchText = CollectionText.Normalize(string.Join(' ', [item.Title, .. kind.SearchFields.SelectMany(item.Values)]));
        return item;
    }

    private const int SampleBytes = 16 * 1024;

    /// <summary>SHA-256 of the first and last 16 KB; a small file is hashed whole.</summary>
    private static string? SampleHashOf(string path, long size)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, SampleBytes);
            if (size <= SampleBytes * 2)
                return Convert.ToHexString(SHA256.HashData(stream));

            var buffer = new byte[SampleBytes * 2];
            stream.ReadExactly(buffer, 0, SampleBytes);
            stream.Seek(-SampleBytes, SeekOrigin.End);
            stream.ReadExactly(buffer, SampleBytes, SampleBytes);
            return Convert.ToHexString(SHA256.HashData(buffer));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? HashOf(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void StartWatchers(IReadOnlyList<string> folders)
    {
        foreach (var folder in folders)
        {
            try
            {
                if (!Directory.Exists(folder))
                    continue;

                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Created += OnFolderChanged;
                watcher.Changed += OnFolderChanged;
                watcher.Deleted += OnFolderChanged;
                watcher.Renamed += OnFolderChanged;
                watcher.Error += (_, _) => ScheduleScan();
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // Not watchable (a disconnected share): picked up on the next scan
            }
        }
    }

    private void StopWatchers()
    {
        foreach (var watcher in watchers)
            watcher.Dispose();

        watchers.Clear();
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e)
        => ScheduleScan();

    private void ScheduleScan()
    {
        try
        {
            // Every event restarts the delay, so a file being copied is read once, after the copy
            changeTimer.Change(ChangeDelay, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private Dictionary<string, CollectionItem> LoadIndex()
    {
        var items = new Dictionary<string, CollectionItem>(StringComparer.OrdinalIgnoreCase);
        if (indexPath is null || !File.Exists(indexPath))
            return items;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(indexPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.GetInt32() != IndexVersion
                || Text(root, "kind") != kind.Id
                || !root.TryGetProperty("kindVersion", out var kindVersion) || kindVersion.GetInt32() != kind.Version)
            {
                return items;
            }

            foreach (var element in root.GetProperty("items").EnumerateArray())
            {
                var path = Text(element, "path");
                var itemRoot = Text(element, "root");
                var title = Text(element, "title");
                if (path is null || itemRoot is null || title is null || !kind.Accepts(Path.GetFileName(path)))
                    continue;

                var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                if (element.TryGetProperty("fields", out var stored) && stored.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in stored.EnumerateObject())
                    {
                        if (field.Value.ValueKind == JsonValueKind.Array)
                            fields[field.Name] = field.Value.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToArray();
                    }
                }

                items[path] = Prepare(new CollectionItem
                {
                    Id = CollectionItem.IdOf(path),
                    Path = path,
                    Root = itemRoot,
                    Size = element.GetProperty("size").GetInt64(),
                    Modified = new DateTime(element.GetProperty("modified").GetInt64(), DateTimeKind.Utc),
                    Added = new DateTime(element.GetProperty("added").GetInt64(), DateTimeKind.Utc),
                    Title = title,
                    Fields = fields,
                    HasCover = element.TryGetProperty("hasCover", out var hasCover) && hasCover.ValueKind == JsonValueKind.True,
                    ContentHash = Text(element, "sha256"),
                    SampleHash = Text(element, "sample"),
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
        {
            // A damaged index is rebuilt by the scan
            items.Clear();
        }

        return items;
    }

    private void SaveIndex(IEnumerable<CollectionItem> items)
    {
        if (indexPath is null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            var temporaryPath = indexPath + ".tmp";
            using (var stream = File.Create(temporaryPath))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("version", IndexVersion);
                writer.WriteString("kind", kind.Id);
                writer.WriteNumber("kindVersion", kind.Version);
                writer.WriteStartArray("items");
                foreach (var item in items)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", item.Path);
                    writer.WriteString("root", item.Root);
                    writer.WriteNumber("size", item.Size);
                    writer.WriteNumber("modified", item.Modified.Ticks);
                    writer.WriteNumber("added", item.Added.Ticks);
                    writer.WriteString("title", item.Title);
                    if (item.Fields.Count > 0)
                    {
                        writer.WriteStartObject("fields");
                        foreach (var (name, values) in item.Fields)
                        {
                            writer.WriteStartArray(name);
                            foreach (var value in values)
                                writer.WriteStringValue(value);

                            writer.WriteEndArray();
                        }

                        writer.WriteEndObject();
                    }

                    if (item.HasCover)
                        writer.WriteBoolean("hasCover", true);
                    if (item.ContentHash is not null)
                        writer.WriteString("sha256", item.ContentHash);
                    if (item.SampleHash is not null)
                        writer.WriteString("sample", item.SampleHash);

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            File.Move(temporaryPath, indexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the index the next start reads every file again, nothing worse
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>The steps of a scan, in the order they run.</summary>
public enum CollectionScanStage
{
    /// <summary>Walking the folders; <see cref="CollectionScanProgress.Done"/> counts the files found so far.</summary>
    Listing,

    /// <summary>Reading new and changed files; unchanged ones come from the index and pass quickly.</summary>
    Reading,

    /// <summary>Hashing files of equal size to find identical copies.</summary>
    Comparing,

    /// <summary>Writing the index.</summary>
    Saving,
}

/// <param name="Path">The folder being walked or the file being read; null between steps.</param>
public sealed record CollectionScanProgress(CollectionScanStage Stage, int Done, int Total, string? Path);
