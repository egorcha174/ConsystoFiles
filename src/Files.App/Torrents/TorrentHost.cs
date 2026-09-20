// Consysto fork: the BitTorrent engine of the app and the ways into it.

using Consysto.Torrents;
using Files.App.Data.Items;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Files.App.Torrents
{
	/// <summary>
	/// Holds the one <see cref="TorrentService"/>. The engine starts when the first download is added, or at app start when
	/// downloads from the previous run are waiting, so an app that never sees a torrent opens no ports.
	/// </summary>
	public static class TorrentHost
	{
		private static readonly string StateDirectory = SystemIO.Path.Combine(AppStorage.LocalFolderPath, "torrents");

		public static TorrentService Service { get; } = new(StateDirectory);

		/// <summary>Resumes the downloads of the previous run, if there are any.</summary>
		public static async Task ResumeIfAnyAsync()
		{
			if (!SystemIO.File.Exists(SystemIO.Path.Combine(StateDirectory, "engine.state")))
				return;

			try
			{
				await Service.StartAsync();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Torrent downloads could not be resumed");
			}
		}

		public static async Task StopAsync()
		{
			try
			{
				await Service.DisposeAsync();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The torrent engine did not stop cleanly");
			}
		}

		public static bool IsTorrentFile(string? path)
			=> path?.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) == true;

		/// <summary>Every download asks where to go, like downloads from a catalog; the last folder is offered again.</summary>
		public static async Task<string?> PickFolderAsync()
		{
			var picker = new FolderPicker
			{
				SuggestedStartLocation = PickerLocationId.Downloads,
				SettingsIdentifier = "ConsystoTorrentDownloads",
			};
			picker.FileTypeFilter.Add("*");
			WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
			return (await picker.PickSingleFolderAsync())?.Path;
		}

		/// <summary>Adds a .torrent file or a magnet link after asking for the folder, then shows the downloads tab.</summary>
		public static async Task AddAsync(string torrentOrMagnet, IShellPage? shellPage, bool openPage = true)
		{
			var folder = await PickFolderAsync();
			if (folder is null)
				return;

			try
			{
				if (TorrentService.IsMagnetLink(torrentOrMagnet))
					await Service.AddMagnetAsync(torrentOrMagnet.Trim(), folder);
				else
					await Service.AddTorrentFileAsync(torrentOrMagnet, folder);
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The torrent could not be added");
				await DialogDisplayHelper.ShowDialogAsync(Strings.ConsystoTorrents.GetLocalizedResource(), string.Format(Strings.ConsystoTorrentAddFailed.GetLocalizedResource(), ex.Message));
				return;
			}

			if (openPage)
				await OpenPageAsync();
		}

		/// <summary>The downloads live in one tab; an open one is reused.</summary>
		public static Task OpenPageAsync()
			=> NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), TorrentPaths.Path, true);
	}

	/// <summary>Which downloads the page shows, chosen in the sidebar's Downloads section.</summary>
	public enum TorrentFilter
	{
		All,
		Downloading,
		Seeding,
		Completed,
		Paused,
		Active,
		Error,
	}

	/// <summary>
	/// The downloads page opens in a tab under the path "Torrents:", like a terminal or an OPDS catalog; a filter from the
	/// sidebar follows the colon, e.g. "Torrents:Seeding".
	/// </summary>
	public static class TorrentPaths
	{
		public const string Path = "Torrents:";

		/// <summary>Download glyph.</summary>
		public const string Glyph = "\uE896";

		public static bool IsTorrentsPath(string? path)
			=> path?.StartsWith(Path, StringComparison.Ordinal) == true;

		public static string ForFilter(TorrentFilter filter)
			=> filter is TorrentFilter.All ? Path : Path + filter;

		public static TorrentFilter FilterOf(string? path)
			=> IsTorrentsPath(path) && Enum.TryParse<TorrentFilter>(path![Path.Length..], out var filter) ? filter : TorrentFilter.All;

		public static string TitleOf(string? path)
			=> FilterOf(path) is TorrentFilter.All
				? Strings.ConsystoTorrents.GetLocalizedResource()
				: $"{Strings.ConsystoTorrents.GetLocalizedResource()} — {NameOf(FilterOf(path))}";

		public static string NameOf(TorrentFilter filter)
			=> (filter switch
			{
				TorrentFilter.Downloading => Strings.ConsystoTorrentFilterDownloading,
				TorrentFilter.Seeding => Strings.ConsystoTorrentFilterSeeding,
				TorrentFilter.Completed => Strings.ConsystoTorrentFilterCompleted,
				TorrentFilter.Paused => Strings.ConsystoTorrentFilterPaused,
				TorrentFilter.Active => Strings.ConsystoTorrentFilterActive,
				TorrentFilter.Error => Strings.ConsystoTorrentFilterError,
				_ => Strings.ConsystoTorrentFilterAll,
			}).GetLocalizedResource();

		public static bool Matches(TorrentFilter filter, TorrentStatus status)
			=> filter switch
			{
				TorrentFilter.Downloading => status.Activity is TorrentActivity.Downloading or TorrentActivity.Metadata or TorrentActivity.Checking,
				TorrentFilter.Seeding => status.Activity is TorrentActivity.Seeding,
				TorrentFilter.Completed => status.IsComplete,
				TorrentFilter.Paused => status.Activity is TorrentActivity.Paused or TorrentActivity.Stopped,
				TorrentFilter.Active => status.DownloadRate > 0 || status.UploadRate > 0,
				TorrentFilter.Error => status.Activity is TorrentActivity.Error,
				_ => true,
			};
	}

	/// <summary>
	/// The Downloads section of the sidebar: one item per filter with the number of downloads in it, like the left panel of
	/// qBittorrent. Counts follow the engine once a second.
	/// </summary>
	public static class TorrentSidebar
	{
		private static readonly Dictionary<TorrentFilter, LocationItem> items = [];

		public static IEnumerable<LocationItem> CreateItems()
		{
			foreach (var filter in Enum.GetValues<TorrentFilter>())
			{
				items[filter] = new LocationItem
				{
					Text = NameWithCount(filter, TorrentHost.Service.Torrents),
					Path = TorrentPaths.ForFilter(filter),
					Section = SectionType.Downloads,
					MenuOptions = new ContextMenuOptions(),
					SelectsOnInvoked = true,
					ChildItems = null,
				};
			}

			TorrentHost.Service.Updated -= Service_Updated;
			TorrentHost.Service.Updated += Service_Updated;
			return items.Values;
		}

		public static LocationItem? ItemOf(string? path)
			=> items.GetValueOrDefault(TorrentPaths.FilterOf(path));

		private static void Service_Updated(object? sender, EventArgs e)
			=> MainWindow.Instance.DispatcherQueue.TryEnqueue(() =>
			{
				var torrents = TorrentHost.Service.Torrents;
				foreach (var (filter, item) in items)
				{
					var text = NameWithCount(filter, torrents);
					if (item.Text != text)
						item.Text = text;
				}
			});

		private static string NameWithCount(TorrentFilter filter, IReadOnlyList<TorrentStatus> torrents)
			=> $"{TorrentPaths.NameOf(filter)} ({torrents.Count(torrent => TorrentPaths.Matches(filter, torrent))})";
	}
}
