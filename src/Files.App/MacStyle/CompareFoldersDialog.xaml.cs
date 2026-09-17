using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.IO;

namespace Files.App.MacStyle
{
	/// <summary>
	/// Consysto fork: compares the folders open in both panes and lets the user pick a copy direction per item. The dialog
	/// only collects the choice; the caller copies after it closes, since only one content dialog can be open at a time.
	/// </summary>
	public sealed partial class CompareFoldersDialog : ContentDialog
	{
		private readonly IFoldersSettingsService _foldersSettings = Ioc.Default.GetRequiredService<IFoldersSettingsService>();
		private readonly string _leftPath;
		private readonly string _rightPath;

		private List<FolderCompareEntry> _allEntries = [];
		private CancellationTokenSource? _scanCancellation;

		public ObservableCollection<FolderCompareEntry> Entries { get; } = [];

		public CompareFoldersDialog(string leftPath, string rightPath)
		{
			_leftPath = leftPath;
			_rightPath = rightPath;

			InitializeComponent();

			LeftPathText.Text = leftPath;
			RightPathText.Text = rightPath;
			ToolTipService.SetToolTip(LeftPathText, leftPath);
			ToolTipService.SetToolTip(RightPathText, rightPath);
			PrimaryButtonText = string.Format(Strings.ConsystoCompareSynchronize.GetLocalizedResource(), 0);

			if (MainWindow.Instance.Content is FrameworkElement rootElement)
				RequestedTheme = rootElement.RequestedTheme;

			Opened += CompareFoldersDialog_Opened;
			Closing += CompareFoldersDialog_Closing;
		}

		public IReadOnlyList<FolderCompareEntry> GetEntriesToSynchronize()
			=> _allEntries.Where(entry => entry.Action is not FolderSyncDirection.None).ToList();

		private async void CompareFoldersDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
			=> await ScanAsync();

		private void CompareFoldersDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
			=> _scanCancellation?.Cancel();

		private async void Subfolders_Click(object sender, RoutedEventArgs e)
			=> await ScanAsync();

		private void ShowIdentical_Click(object sender, RoutedEventArgs e)
			=> ApplyFilter();

		private void ActionButton_Click(object sender, RoutedEventArgs e)
		{
			if ((sender as FrameworkElement)?.DataContext is FolderCompareEntry entry)
				entry.CycleAction();
		}

		private async Task ScanAsync()
		{
			_scanCancellation?.Cancel();
			var cancellation = new CancellationTokenSource();
			_scanCancellation = cancellation;

			var recursive = SubfoldersCheckBox.IsChecked == true;
			var includeHidden = _foldersSettings.ShowHiddenItems;

			ScanProgress.IsActive = true;
			ScanProgress.Visibility = Visibility.Visible;
			StatusText.Text = Strings.ConsystoCompareScanning.GetLocalizedResource();
			IsPrimaryButtonEnabled = false;

			try
			{
				var entries = await Task.Run(() => FolderComparer.Compare(_leftPath, _rightPath, recursive, includeHidden, cancellation.Token), cancellation.Token);
				if (cancellation.IsCancellationRequested)
					return;

				foreach (var entry in _allEntries)
					entry.PropertyChanged -= Entry_PropertyChanged;

				_allEntries = entries;

				foreach (var entry in _allEntries)
					entry.PropertyChanged += Entry_PropertyChanged;

				StatusText.Text = _allEntries.All(entry => entry.State is FolderCompareState.Identical)
					? Strings.ConsystoCompareNoDifferences.GetLocalizedResource()
					: string.Empty;
				ApplyFilter();
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				StatusText.Text = ex.Message;
			}
			finally
			{
				if (_scanCancellation == cancellation)
				{
					ScanProgress.IsActive = false;
					ScanProgress.Visibility = Visibility.Collapsed;
				}
			}
		}

		private void ApplyFilter()
		{
			var showIdentical = ShowIdenticalCheckBox.IsChecked == true;

			Entries.Clear();
			foreach (var entry in _allEntries)
			{
				if (showIdentical || entry.State is not FolderCompareState.Identical)
					Entries.Add(entry);
			}

			UpdateSummary();
		}

		private void UpdateSummary()
		{
			int CountOf(params FolderCompareState[] states)
				=> _allEntries.Count(entry => states.Contains(entry.State));

			SummaryText.Text = string.Format(
				Strings.ConsystoCompareSummary.GetLocalizedResource(),
				CountOf(FolderCompareState.OnlyLeft),
				CountOf(FolderCompareState.OnlyRight),
				CountOf(FolderCompareState.LeftNewer, FolderCompareState.RightNewer, FolderCompareState.Different, FolderCompareState.TypeMismatch),
				CountOf(FolderCompareState.Identical));

			var selectedCount = _allEntries.Count(entry => entry.Action is not FolderSyncDirection.None);
			PrimaryButtonText = string.Format(Strings.ConsystoCompareSynchronize.GetLocalizedResource(), selectedCount);
			IsPrimaryButtonEnabled = selectedCount > 0;
		}

		private void Entry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(FolderCompareEntry.Action))
				UpdateSummary();
		}
	}
}
