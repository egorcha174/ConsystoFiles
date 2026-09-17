using MonoTorrent;
using MonoTorrent.Client;

namespace Consysto.Torrents;

/// <summary>What a download is doing, copied out of the engine so the UI never touches engine objects.</summary>
public sealed record TorrentStatus(
    string Id,
    string Name,
    string SavePath,
    TorrentActivity Activity,
    double Progress,
    long Size,
    long DownloadRate,
    long UploadRate,
    int Peers,
    string? Error)
{
    /// <summary>Connected peers that have the whole torrent.</summary>
    public int Seeds { get; init; }

    /// <summary>Connected peers still downloading.</summary>
    public int Leechers { get; init; }

    /// <summary>Uploaded divided by downloaded, as qBittorrent shows it.</summary>
    public double Ratio { get; init; }

    /// <summary>Time left at the current speed; null when not downloading or the speed is zero.</summary>
    public TimeSpan? Remaining { get; init; }

    public bool IsComplete { get; init; }

    /// <summary>Host names of the trackers, for filtering.</summary>
    public IReadOnlyList<string> TrackerHosts { get; init; } = [];
}

public enum TorrentActivity
{
    /// <summary>A magnet link is still fetching the list of files.</summary>
    Metadata,
    Checking,
    Downloading,
    Seeding,
    Paused,
    Stopped,
    Error,
}

/// <summary>
/// One BitTorrent engine for the app. Downloads survive restarts: the engine state (the list of torrents, where they go)
/// and fast-resume data are kept in the state folder, so a restart does not hash everything again.
/// </summary>
public sealed partial class TorrentService : IAsyncDisposable
{
    private const string StateFileName = "engine.state";
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(1);

    private readonly string stateDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ClientEngine? engine;
    private Timer? timer;

    public TorrentService(string stateDirectory)
    {
        this.stateDirectory = stateDirectory;
    }

    /// <summary>Raised on a worker thread about once a second while the engine runs, and after every change.</summary>
    public event EventHandler? Updated;

    public IReadOnlyList<TorrentStatus> Torrents { get; private set; } = [];

    public bool IsStarted => engine is not null;

    /// <summary>Restores the downloads of the previous run and resumes those that were running.</summary>
    public async Task StartAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (engine is not null)
                return;

            Directory.CreateDirectory(stateDirectory);
            engine = await RestoreOrCreateAsync();
            foreach (var manager in engine.Torrents)
            {
                if (manager.State is TorrentState.Stopped)
                    await manager.StartAsync();
            }

            timer = new Timer(_ => Refresh(), null, TimeSpan.Zero, UpdateInterval);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<string> AddTorrentFileAsync(string torrentPath, string saveDirectory)
        => AddAsync(async running => await running.AddAsync(await Torrent.LoadAsync(torrentPath), saveDirectory));

    public Task<string> AddMagnetAsync(string magnetLink, string saveDirectory)
    {
        if (!MagnetLink.TryParse(magnetLink, out var link) || link is null)
            throw new ArgumentException("Not a magnet link.", nameof(magnetLink));

        return AddAsync(running => running.AddAsync(link, saveDirectory));
    }

    public static bool IsMagnetLink(string? text)
        => text is not null && text.TrimStart().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) && MagnetLink.TryParse(text.Trim(), out _);

    public Task PauseAsync(string id)
        => WithManagerAsync(id, manager => manager.State is TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata
            ? manager.PauseAsync()
            : Task.CompletedTask);

    public Task ResumeAsync(string id)
        => WithManagerAsync(id, manager => manager.StartAsync());

    /// <param name="deleteFiles">Also delete what was downloaded; otherwise the files stay where they are.</param>
    public Task RemoveAsync(string id, bool deleteFiles)
        => WithManagerAsync(id, async manager =>
        {
            await manager.StopAsync();
            await engine!.RemoveAsync(manager, deleteFiles ? RemoveMode.CacheDataAndDownloadedData : RemoveMode.CacheDataOnly);
        });

    public async ValueTask DisposeAsync()
    {
        if (timer is not null)
            await timer.DisposeAsync();

        await gate.WaitAsync();
        try
        {
            if (engine is null)
                return;

            await SaveStateAsync();
            await engine.StopAllAsync(TimeSpan.FromSeconds(5));
            engine.Dispose();
            engine = null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> AddAsync(Func<ClientEngine, Task<TorrentManager>> add)
    {
        await StartAsync();
        await gate.WaitAsync();
        try
        {
            var manager = await add(engine!);
            await manager.StartAsync();
            await SaveStateAsync();
            return IdOf(manager);
        }
        finally
        {
            gate.Release();
            Refresh();
        }
    }

    private async Task WithManagerAsync(string id, Func<TorrentManager, Task> action)
    {
        await gate.WaitAsync();
        try
        {
            if (engine?.Torrents.FirstOrDefault(manager => IdOf(manager) == id) is { } manager)
            {
                await action(manager);
                await SaveStateAsync();
            }
        }
        finally
        {
            gate.Release();
            Refresh();
        }
    }

    private async Task<ClientEngine> RestoreOrCreateAsync()
    {
        var statePath = Path.Combine(stateDirectory, StateFileName);
        if (File.Exists(statePath))
        {
            try
            {
                return await ClientEngine.RestoreStateAsync(await File.ReadAllBytesAsync(statePath));
            }
            catch (Exception)
            {
                // A damaged state starts an empty engine; the downloaded files themselves are untouched
            }
        }

        var settings = new EngineSettingsBuilder
        {
            CacheDirectory = Path.Combine(stateDirectory, "cache"),
            AutoSaveLoadFastResume = true,
            AutoSaveLoadMagnetLinkMetadata = true,
            AllowPortForwarding = true,
        }.ToSettings();
        return new ClientEngine(settings);
    }

    private async Task SaveStateAsync()
    {
        if (engine is null)
            return;

        var statePath = Path.Combine(stateDirectory, StateFileName);
        var temporary = statePath + ".tmp";
        await File.WriteAllBytesAsync(temporary, await engine.SaveStateAsync());
        File.Move(temporary, statePath, overwrite: true);
    }

    private void Refresh()
    {
        var running = engine;
        if (running is null)
            return;

        try
        {
            Torrents = running.Torrents.Select(StatusOf).ToArray();
        }
        catch (InvalidOperationException)
        {
            // The list changed while it was being read; the next tick catches up
            return;
        }

        Updated?.Invoke(this, EventArgs.Empty);
    }

    private static TorrentStatus StatusOf(TorrentManager manager)
    {
        var size = manager.Torrent?.Size ?? 0;
        var received = manager.Monitor.DataBytesReceived;
        var sent = manager.Monitor.DataBytesSent;
        var left = size - (long)(size * manager.Progress / 100d);
        var rate = manager.Monitor.DownloadRate;
        return BaseStatusOf(manager) with
        {
            Seeds = manager.Peers.Seeds,
            Leechers = manager.Peers.Leechs,
            Ratio = received > 0 ? (double)sent / received : 0,
            Remaining = manager.State == TorrentState.Downloading && rate > 0 ? TimeSpan.FromSeconds(left / (double)rate) : null,
            IsComplete = manager.Complete,
            TrackerHosts = manager.TrackerManager.Tiers.SelectMany(tier => tier.Trackers).Select(tracker => tracker.Uri.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static TorrentStatus BaseStatusOf(TorrentManager manager)
        => new(
            IdOf(manager),
            manager.Torrent?.Name ?? manager.Name,
            manager.SavePath,
            manager.State switch
            {
                TorrentState.Metadata => TorrentActivity.Metadata,
                TorrentState.Hashing or TorrentState.FetchingHashes => TorrentActivity.Checking,
                TorrentState.Downloading => TorrentActivity.Downloading,
                TorrentState.Seeding => TorrentActivity.Seeding,
                TorrentState.Paused => TorrentActivity.Paused,
                TorrentState.Error => TorrentActivity.Error,
                _ => TorrentActivity.Stopped,
            },
            manager.Progress / 100d,
            manager.Torrent?.Size ?? 0,
            manager.Monitor.DownloadRate,
            manager.Monitor.UploadRate,
            manager.OpenConnections,
            manager.Error?.Exception.Message);

    private static string IdOf(TorrentManager manager)
        => manager.InfoHashes.V1OrV2.ToHex();
}
