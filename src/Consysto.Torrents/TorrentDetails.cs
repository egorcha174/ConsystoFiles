using MonoTorrent;
using MonoTorrent.Client;

namespace Consysto.Torrents;

/// <summary>One file of a torrent, with how much of it is here and whether it is wanted.</summary>
public sealed record TorrentFileStatus(int Index, string Path, long Size, double Progress, bool IsWanted);

/// <summary>A peer the torrent is connected to.</summary>
public sealed record TorrentPeerStatus(string Address, string Client, double Progress, long DownloadRate, long UploadRate, bool IsSeeder);

/// <summary>A tracker of the torrent and what it last said.</summary>
public sealed record TorrentTrackerStatus(string Address, string Status, string? Message);

/// <summary>Files, peers and trackers of one download, for the details panel.</summary>
public sealed record TorrentDetails(
    string Id,
    IReadOnlyList<TorrentFileStatus> Files,
    IReadOnlyList<TorrentPeerStatus> Peers,
    IReadOnlyList<TorrentTrackerStatus> Trackers);

public sealed partial class TorrentService
{
    /// <summary>Files, peers and trackers of a download; null when there is no such download.</summary>
    public async Task<TorrentDetails?> GetDetailsAsync(string id)
    {
        if (Find(id) is not { } manager)
            return null;

        var files = manager.Files
            .Select((file, index) => new TorrentFileStatus(index, file.Path, file.Length, file.BitField.PercentComplete / 100d, file.Priority != Priority.DoNotDownload))
            .ToArray();

        var peers = (await manager.GetPeersAsync())
            .Select(peer => new TorrentPeerStatus(
                peer.Uri.Host + ":" + peer.Uri.Port,
                peer.ClientApp.Client.ToString(),
                peer.BitField.PercentComplete / 100d,
                peer.Monitor.DownloadRate,
                peer.Monitor.UploadRate,
                peer.IsSeeder))
            .ToArray();

        var trackers = manager.TrackerManager.Tiers
            .SelectMany(tier => tier.Trackers)
            .Select(tracker => new TorrentTrackerStatus(tracker.Uri.ToString(), tracker.Status.ToString(), tracker.FailureMessage ?? tracker.WarningMessage))
            .ToArray();

        return new TorrentDetails(id, files, peers, trackers);
    }

    /// <summary>Skip or get back a file of a download; a skipped file is not downloaded or kept complete.</summary>
    public Task SetFileWantedAsync(string id, int fileIndex, bool wanted)
        => WithManagerAsync(id, manager => fileIndex >= 0 && fileIndex < manager.Files.Count
            ? manager.SetFilePriorityAsync(manager.Files[fileIndex], wanted ? Priority.Normal : Priority.DoNotDownload)
            : Task.CompletedTask);

    private TorrentManager? Find(string id)
        => engine?.Torrents.FirstOrDefault(manager => IdOf(manager) == id);
}
