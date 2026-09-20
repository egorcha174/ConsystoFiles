// Consysto fork: torrent downloads in a tab, laid out like qBittorrent.

using System.Collections.ObjectModel;
using System.Globalization;
using Consysto.Torrents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Windows.System;

namespace Files.App.Torrents
{
	public sealed partial class TorrentsPage : Page
	{
		/// <summary>Columns of the table: title resource, width (star for the name), how to sort.</summary>
		private static readonly (string Title, GridLength Width, Func<TorrentRow, IComparable?> Key)[] Columns =
		[
			("ConsystoTorrentColumnName", new(1, GridUnitType.Star), row => row.Name),
			("ConsystoTorrentColumnSize", new(90), row => row.Size),
			("ConsystoTorrentColumnProgress", new(130), row => row.Progress),
			("ConsystoTorrentColumnState", new(120), row => row.StateText),
			("ConsystoTorrentColumnSeeds", new(60), row => row.Seeds),
			("ConsystoTorrentColumnPeers", new(60), row => row.Leechers),
			("ConsystoTorrentColumnDownload", new(90), row => row.DownloadRate),
			("ConsystoTorrentColumnUpload", new(90), row => row.UploadRate),
			("ConsystoTorrentColumnRemaining", new(90), row => row.RemainingSeconds),
			("ConsystoTorrentColumnRatio", new(70), row => row.Ratio),
		];

		private readonly List<TorrentRow> allRows = [];
		private readonly ObservableCollection<TorrentRow> shownRows = [];
		private readonly ObservableCollection<TorrentFileRow> fileRows = [];
		private readonly ObservableCollection<TorrentPeerRow> peerRows = [];
		private readonly ObservableCollection<TorrentTrackerRow> trackerRows = [];
		private readonly List<Button> headerButtons = [];
		private IShellPage? appInstance;
		private string torrentsPath = TorrentPaths.Path;
		private int sortColumn;
		private bool sortDescending;
		private bool isUpdatingTrackers;
		private bool isLoadingDetails;

		public TorrentsPage()
		{
			InitializeComponent();
			TorrentList.ItemsSource = shownRows;
			FileList.ItemsSource = fileRows;
			PeerList.ItemsSource = peerRows;
			TrackerList.ItemsSource = trackerRows;
			BuildHeader();
			UpdateButtons();
		}

		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			base.OnNavigatedTo(e);
			if (e.Parameter is NavigationArguments arguments)
			{
				appInstance = arguments.AssociatedTabInstance;
				torrentsPath = arguments.NavPathParam ?? TorrentPaths.Path;
				await UpdateShellAsync();
			}

			TorrentHost.Service.Updated += Service_Updated;
			ShowRows();
			if (!TorrentHost.Service.IsStarted)
				await TorrentHost.ResumeIfAnyAsync();
		}

		protected override void OnNavigatedFrom(NavigationEventArgs e)
		{
			TorrentHost.Service.Updated -= Service_Updated;
			base.OnNavigatedFrom(e);
		}

		private void Service_Updated(object? sender, EventArgs e)
			=> DispatcherQueue.TryEnqueue(async () =>
			{
				ShowRows();
				await LoadDetailsAsync();
			});

		private void BuildHeader()
		{
			for (var index = 0; index < Columns.Length; index++)
			{
				HeaderRow.ColumnDefinitions.Add(NewColumn(index));
				var button = new Button
				{
					Content = Columns[index].Title.GetLocalizedResource(),
					Style = (Style)Resources["TorrentHeaderButtonStyle"],
					Tag = index,
				};
				if (index > 0)
					button.HorizontalContentAlignment = index is 2 or 3 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
				button.Click += Header_Click;
				Grid.SetColumn(button, index);
				HeaderRow.Children.Add(button);
				headerButtons.Add(button);
			}

			UpdateSortIndicator();
		}

		/// <summary>Every row lays out its cells on the same columns as the header.</summary>
		private void Row_Loaded(object sender, RoutedEventArgs e)
		{
			if (sender is not Grid grid || grid.ColumnDefinitions.Count > 0)
				return;

			for (var index = 0; index < Columns.Length; index++)
				grid.ColumnDefinitions.Add(NewColumn(index));
		}

		/// <summary>
		/// The name takes what the fixed columns leave. In a narrow pane that was nothing at all and the name disappeared,
		/// so it keeps a floor: the rest of the table is cut off instead.
		/// </summary>
		private static ColumnDefinition NewColumn(int index)
			=> new()
			{
				Width = Columns[index].Width,
				MinWidth = index is 0 ? 180 : 0,
			};

		private void Header_Click(object sender, RoutedEventArgs e)
		{
			if ((sender as Button)?.Tag is not int column)
				return;

			sortDescending = column == sortColumn && !sortDescending;
			sortColumn = column;
			UpdateSortIndicator();
			ShowRows();
		}

		private void UpdateSortIndicator()
		{
			for (var index = 0; index < headerButtons.Count; index++)
			{
				var title = Columns[index].Title.GetLocalizedResource();
				headerButtons[index].Content = index == sortColumn ? $"{title} {(sortDescending ? "▾" : "▴")}" : title;
			}
		}

		/// <summary>The sidebar filter, the tracker and the search narrow the table; rows are updated in place.</summary>
		private void ShowRows()
		{
			var torrents = TorrentHost.Service.Torrents;
			allRows.RemoveAll(row => !torrents.Any(torrent => torrent.Id == row.Id));
			foreach (var torrent in torrents)
			{
				if (allRows.FirstOrDefault(row => row.Id == torrent.Id) is { } row)
					row.Update(torrent);
				else
					allRows.Add(new TorrentRow(torrent));
			}

			UpdateTrackerFilter(torrents);

			var filter = TorrentPaths.FilterOf(torrentsPath);
			var tracker = TrackerFilter.SelectedIndex > 0 ? TrackerFilter.SelectedItem as string : null;
			var search = SearchBox.Text?.Trim();
			var key = Columns[sortColumn].Key;
			IEnumerable<TorrentRow> visible = allRows
				.Where(row => TorrentPaths.Matches(filter, row.Status))
				.Where(row => tracker is null || row.Status.TrackerHosts.Contains(tracker, StringComparer.OrdinalIgnoreCase))
				.Where(row => string.IsNullOrEmpty(search) || row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase));
			visible = sortDescending ? visible.OrderByDescending(key) : visible.OrderBy(key);
			var ordered = visible.ToList();

			// Move rows into place instead of rebuilding the list, so the selection and scroll position survive the refresh
			for (var index = shownRows.Count - 1; index >= 0; index--)
			{
				if (!ordered.Contains(shownRows[index]))
					shownRows.RemoveAt(index);
			}
			for (var index = 0; index < ordered.Count; index++)
			{
				var current = shownRows.IndexOf(ordered[index]);
				if (current < 0)
					shownRows.Insert(index, ordered[index]);
				else if (current != index)
					shownRows.Move(current, index);
			}

			EmptyText.Visibility = shownRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
			UpdateButtons();
		}

		private void UpdateTrackerFilter(IReadOnlyList<TorrentStatus> torrents)
		{
			var hosts = torrents.SelectMany(torrent => torrent.TrackerHosts).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
			var current = TrackerFilter.Items.Skip(1).OfType<string>().ToList();
			if (TrackerFilter.Items.Count > 0 && hosts.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))
				return;

			isUpdatingTrackers = true;
			var selected = TrackerFilter.SelectedItem as string;
			TrackerFilter.Items.Clear();
			TrackerFilter.Items.Add(Strings.ConsystoTorrentAllTrackers.GetLocalizedResource());
			foreach (var host in hosts)
				TrackerFilter.Items.Add(host);
			TrackerFilter.SelectedIndex = selected is not null && hosts.Contains(selected) ? hosts.IndexOf(selected) + 1 : 0;
			isUpdatingTrackers = false;
		}

		private void Filter_Changed(object sender, SelectionChangedEventArgs e)
		{
			if (!isUpdatingTrackers)
				ShowRows();
		}

		private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
			=> ShowRows();

		private TorrentRow? Selected
			=> TorrentList.SelectedItem as TorrentRow;

		private void UpdateButtons()
		{
			var selected = Selected;
			ResumeButton.IsEnabled = selected is not null && !selected.IsActive;
			PauseButton.IsEnabled = selected is not null && selected.IsActive;
			RemoveButton.IsEnabled = selected is not null;
			OpenFolderButton.IsEnabled = selected is not null;
		}

		private async void TorrentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			UpdateButtons();
			fileRows.Clear();
			peerRows.Clear();
			trackerRows.Clear();
			await LoadDetailsAsync();
		}

		private void DetailsTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
		{
			FileList.Visibility = DetailsTabs.SelectedItem == FilesTab ? Visibility.Visible : Visibility.Collapsed;
			PeerList.Visibility = DetailsTabs.SelectedItem == PeersTab ? Visibility.Visible : Visibility.Collapsed;
			TrackerList.Visibility = DetailsTabs.SelectedItem == TrackersTab ? Visibility.Visible : Visibility.Collapsed;
			_ = LoadDetailsAsync();
		}

		/// <summary>Files, peers and trackers of the selected download, refreshed with the table.</summary>
		private async Task LoadDetailsAsync()
		{
			DetailsEmptyText.Visibility = Selected is null ? Visibility.Visible : Visibility.Collapsed;
			if (Selected is not { } selected || isLoadingDetails)
				return;

			isLoadingDetails = true;
			try
			{
				if (await TorrentHost.Service.GetDetailsAsync(selected.Id) is not { } details || Selected?.Id != details.Id)
					return;

				Sync(fileRows, details.Files.Select(file => new TorrentFileRow(file)).ToList(), (a, b) => a.Index == b.Index);
				Sync(peerRows, details.Peers.Select(peer => new TorrentPeerRow(peer)).ToList(), (a, b) => a.Address == b.Address);
				Sync(trackerRows, details.Trackers.Select(tracker => new TorrentTrackerRow(tracker)).ToList(), (a, b) => a.Address == b.Address);
			}
			finally
			{
				isLoadingDetails = false;
			}
		}

		/// <summary>Replaces changed rows only, so the lists do not jump every second.</summary>
		private static void Sync<T>(ObservableCollection<T> target, List<T> source, Func<T, T, bool> same)
			where T : IEquatable<T>
		{
			for (var index = target.Count - 1; index >= source.Count; index--)
				target.RemoveAt(index);
			for (var index = 0; index < source.Count; index++)
			{
				if (index >= target.Count)
					target.Add(source[index]);
				else if (!target[index].Equals(source[index]))
					target[index] = source[index];
			}
		}

		private async void FileWanted_Click(object sender, RoutedEventArgs e)
		{
			if (Selected is { } selected && sender is CheckBox { Tag: int index } box)
				await TorrentHost.Service.SetFileWantedAsync(selected.Id, index, box.IsChecked == true);
		}

		private async void AddMagnet_Click(object sender, RoutedEventArgs e)
			=> await AddMagnetAsync();

		private async void MagnetBox_KeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key == VirtualKey.Enter)
			{
				e.Handled = true;
				await AddMagnetAsync();
			}
		}

		private async Task AddMagnetAsync()
		{
			var text = MagnetBox.Text.Trim();
			MagnetFlyout.Hide();
			if (!TorrentService.IsMagnetLink(text))
			{
				await DialogDisplayHelper.ShowDialogAsync(Strings.ConsystoTorrents.GetLocalizedResource(), Strings.ConsystoTorrentNotMagnet.GetLocalizedResource());
				return;
			}

			MagnetBox.Text = string.Empty;
			await TorrentHost.AddAsync(text, appInstance, openPage: false);
			ShowRows();
		}

		private async void AddFile_Click(object sender, RoutedEventArgs e)
		{
			var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
			picker.FileTypeFilter.Add(".torrent");
			WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
			if (await picker.PickSingleFileAsync() is { } file)
			{
				await TorrentHost.AddAsync(file.Path, appInstance, openPage: false);
				ShowRows();
			}
		}

		private async void Resume_Click(object sender, RoutedEventArgs e)
		{
			if (Selected is { } selected)
				await TorrentHost.Service.ResumeAsync(selected.Id);
		}

		private async void Pause_Click(object sender, RoutedEventArgs e)
		{
			if (Selected is { } selected)
				await TorrentHost.Service.PauseAsync(selected.Id);
		}

		private async void OpenFolder_Click(object sender, RoutedEventArgs e)
		{
			if (Selected is { } selected && SystemIO.Directory.Exists(selected.SavePath))
				await NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), selected.SavePath, true);
		}

		private void TorrentList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
			=> OpenFolder_Click(sender, e);

		private async void Remove_Click(object sender, RoutedEventArgs e)
		{
			if (Selected is not { } row)
				return;

			// Two ways out: keep the downloaded files, or delete them too
			var dialog = new ContentDialog
			{
				Title = Strings.ConsystoTorrentRemove.GetLocalizedResource(),
				Content = string.Format(Strings.ConsystoTorrentRemoveQuestion.GetLocalizedResource(), row.Name),
				PrimaryButtonText = Strings.ConsystoTorrentRemoveKeepFiles.GetLocalizedResource(),
				SecondaryButtonText = Strings.ConsystoTorrentRemoveDeleteFiles.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DefaultButton = ContentDialogButton.Primary,
				XamlRoot = XamlRoot,
			};

			var result = await dialog.TryShowAsync();
			if (result == ContentDialogResult.Primary)
				await TorrentHost.Service.RemoveAsync(row.Id, deleteFiles: false);
			else if (result == ContentDialogResult.Secondary)
				await TorrentHost.Service.RemoveAsync(row.Id, deleteFiles: true);
		}

		private async Task UpdateShellAsync()
		{
			if (appInstance is not { } shell)
				return;

			// Like Home, the page has no files: the toolbar hides its folder commands and the preview pane stays closed.
			shell.InstanceViewModel.IsPageTypeNotHome = false;
			shell.InstanceViewModel.IsPageTypeSearchResults = false;
			shell.InstanceViewModel.IsPageTypeMtpDevice = false;
			shell.InstanceViewModel.IsPageTypeRecycleBin = false;
			shell.InstanceViewModel.IsPageTypeCloudDrive = false;
			shell.InstanceViewModel.IsPageTypeFtp = false;
			shell.InstanceViewModel.IsPageTypeZipFolder = false;
			shell.InstanceViewModel.IsPageTypeLibrary = false;
			shell.InstanceViewModel.GitRepositoryPath = null;
			shell.InstanceViewModel.IsGitRepository = false;
			shell.InstanceViewModel.IsPageTypeReleaseNotes = false;
			shell.InstanceViewModel.IsPageTypeSettings = false;
			shell.ToolbarViewModel.CanRefresh = false;
			shell.ToolbarViewModel.CanGoBack = shell.CanNavigateBackward;
			shell.ToolbarViewModel.CanGoForward = shell.CanNavigateForward;
			shell.ToolbarViewModel.CanNavigateToParent = false;

			var shellViewModel = shell.GetRequiredShellViewModel();
			await shellViewModel.SetWorkingDirectoryAsync(torrentsPath);
			shell.SlimContentPage?.StatusBarViewModel.UpdateGitInfo(false, string.Empty, null);

			var title = TorrentPaths.TitleOf(torrentsPath);
			shell.ToolbarViewModel.PathComponents.Clear();
			shell.ToolbarViewModel.PathComponents.Add(new PathBoxItem()
			{
				Title = title,
				Path = torrentsPath,
				ChevronToolTip = string.Format(Strings.BreadcrumbBarChevronButtonToolTip.GetLocalizedResource(), title),
			});
		}
	}

	/// <summary>One download as the table shows it; updated in place every second.</summary>
	public sealed partial class TorrentRow : ObservableObject
	{
		public TorrentRow(TorrentStatus status)
		{
			Id = status.Id;
			Status = status;
			Update(status);
		}

		public string Id { get; }

		public TorrentStatus Status { get; private set; }

		[ObservableProperty]
		public partial string Name { get; set; } = string.Empty;

		[ObservableProperty]
		public partial string SavePath { get; set; } = string.Empty;

		[ObservableProperty]
		public partial double Progress { get; set; }

		[ObservableProperty]
		public partial string ProgressText { get; set; } = string.Empty;

		[ObservableProperty]
		public partial long Size { get; set; }

		[ObservableProperty]
		public partial string SizeText { get; set; } = string.Empty;

		[ObservableProperty]
		public partial string StateText { get; set; } = string.Empty;

		[ObservableProperty]
		public partial Brush? StateBrush { get; set; }

		[ObservableProperty]
		public partial int Seeds { get; set; }

		[ObservableProperty]
		public partial int Leechers { get; set; }

		[ObservableProperty]
		public partial long DownloadRate { get; set; }

		[ObservableProperty]
		public partial string DownloadRateText { get; set; } = string.Empty;

		[ObservableProperty]
		public partial long UploadRate { get; set; }

		[ObservableProperty]
		public partial string UploadRateText { get; set; } = string.Empty;

		[ObservableProperty]
		public partial string RemainingText { get; set; } = string.Empty;

		[ObservableProperty]
		public partial double Ratio { get; set; }

		[ObservableProperty]
		public partial string RatioText { get; set; } = string.Empty;

		[ObservableProperty]
		public partial bool IsPaused { get; set; }

		[ObservableProperty]
		public partial bool IsError { get; set; }

		[ObservableProperty]
		public partial bool IsActive { get; set; }

		/// <summary>Sort key of the remaining time: finished and stalled downloads go last.</summary>
		public double RemainingSeconds
			=> Status.Remaining?.TotalSeconds ?? double.MaxValue;

		public void Update(TorrentStatus status)
		{
			Status = status;
			Name = status.Name;
			SavePath = status.SavePath;
			Progress = status.Progress;
			ProgressText = string.Format(CultureInfo.CurrentCulture, "{0:0.#} %", status.Progress * 100);
			Size = status.Size;
			SizeText = status.Size > 0 ? status.Size.ToSizeString() : string.Empty;
			IsPaused = status.Activity is TorrentActivity.Paused or TorrentActivity.Stopped;
			IsError = status.Activity is TorrentActivity.Error;
			IsActive = status.Activity is TorrentActivity.Downloading or TorrentActivity.Seeding or TorrentActivity.Metadata or TorrentActivity.Checking;
			StateText = (status.Activity switch
			{
				TorrentActivity.Metadata => Strings.ConsystoTorrentStateMetadata,
				TorrentActivity.Checking => Strings.ConsystoTorrentStateChecking,
				TorrentActivity.Downloading => Strings.ConsystoTorrentStateDownloading,
				TorrentActivity.Seeding => Strings.ConsystoTorrentStateSeeding,
				TorrentActivity.Paused => Strings.ConsystoTorrentStatePaused,
				TorrentActivity.Error => Strings.ConsystoTorrentStateError,
				_ => Strings.ConsystoTorrentStateStopped,
			}).GetLocalizedResource();
			StateBrush = BrushFor(status.Activity);
			Seeds = status.Seeds;
			Leechers = status.Leechers;
			DownloadRate = status.DownloadRate;
			DownloadRateText = FormatRate(status.DownloadRate);
			UploadRate = status.UploadRate;
			UploadRateText = FormatRate(status.UploadRate);
			RemainingText = status.Remaining is { } remaining ? FormatRemaining(remaining) : "∞";
			Ratio = status.Ratio;
			RatioText = status.Ratio.ToString("0.00", CultureInfo.CurrentCulture);
		}

		private static string FormatRemaining(TimeSpan remaining)
			=> remaining.TotalDays >= 1 ? string.Format(Strings.ConsystoTorrentDaysHours.GetLocalizedResource(), (int)remaining.TotalDays, remaining.Hours)
				: remaining.TotalHours >= 1 ? string.Format(Strings.ConsystoTorrentHoursMinutes.GetLocalizedResource(), (int)remaining.TotalHours, remaining.Minutes)
				: string.Format(Strings.ConsystoTorrentMinutesSeconds.GetLocalizedResource(), remaining.Minutes, remaining.Seconds);

		/// <summary>"2,57 МБ/с"; zero reads as "0 Б/с" like qBittorrent.</summary>
		internal static string FormatRate(long bytesPerSecond)
			=> string.Format(Strings.ConsystoTorrentRate.GetLocalizedResource(), bytesPerSecond.ToSizeString());

		/// <summary>Seeding in blue and downloading in green, as in qBittorrent; errors in red.</summary>
		private static Brush? BrushFor(TorrentActivity activity)
		{
			var key = activity switch
			{
				TorrentActivity.Downloading or TorrentActivity.Metadata => "SystemFillColorSuccessBrush",
				TorrentActivity.Seeding => "AccentTextFillColorPrimaryBrush",
				TorrentActivity.Error => "SystemFillColorCriticalBrush",
				_ => "TextFillColorSecondaryBrush",
			};
			return Application.Current.Resources.TryGetValue(key, out var brush) ? brush as Brush : null;
		}
	}

	public sealed partial class TorrentFileRow(TorrentFileStatus file) : IEquatable<TorrentFileRow>
	{
		public int Index { get; } = file.Index;
		public string Path { get; } = file.Path;
		public string SizeText { get; } = file.Size.ToSizeString();
		public double Progress { get; } = file.Progress;
		public bool IsWanted { get; } = file.IsWanted;

		public bool Equals(TorrentFileRow? other)
			=> other is not null && Index == other.Index && Progress == other.Progress && IsWanted == other.IsWanted && Path == other.Path;

		public override bool Equals(object? obj) => Equals(obj as TorrentFileRow);

		public override int GetHashCode() => Index;
	}

	public sealed partial class TorrentPeerRow(TorrentPeerStatus peer) : IEquatable<TorrentPeerRow>
	{
		public string Address { get; } = peer.Address;
		public string Client { get; } = peer.Client;
		public string ProgressText { get; } = string.Format(CultureInfo.CurrentCulture, "{0:0.#} %", peer.Progress * 100);
		public string DownloadRateText { get; } = TorrentRow.FormatRate(peer.DownloadRate);
		public string UploadRateText { get; } = TorrentRow.FormatRate(peer.UploadRate);

		public bool Equals(TorrentPeerRow? other)
			=> other is not null && Address == other.Address && ProgressText == other.ProgressText && DownloadRateText == other.DownloadRateText && UploadRateText == other.UploadRateText;

		public override bool Equals(object? obj) => Equals(obj as TorrentPeerRow);

		public override int GetHashCode() => Address.GetHashCode(StringComparison.Ordinal);
	}

	public sealed partial class TorrentTrackerRow(TorrentTrackerStatus tracker) : IEquatable<TorrentTrackerRow>
	{
		public string Address { get; } = tracker.Address;
		public string Status { get; } = tracker.Status;
		public string Message { get; } = tracker.Message ?? string.Empty;

		public bool Equals(TorrentTrackerRow? other)
			=> other is not null && Address == other.Address && Status == other.Status && Message == other.Message;

		public override bool Equals(object? obj) => Equals(obj as TorrentTrackerRow);

		public override int GetHashCode() => Address.GetHashCode(StringComparison.Ordinal);
	}
}
