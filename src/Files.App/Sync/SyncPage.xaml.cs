// Consysto fork: one backup pair in a tab — a check lists what would be copied, a run copies it.

using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Files.App.Sync
{
	public sealed partial class SyncPage : Page
	{
		private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

		private readonly ObservableCollection<BackupRow> rows = [];
		private IShellPage? appInstance;
		private string pairPath = string.Empty;
		private SyncPair? pair;
		private BackupPlan? plan;
		private CancellationTokenSource? work;
		private DateTime lastProgress;

		public SyncPage()
		{
			InitializeComponent();
			ItemList.ItemsSource = rows;
		}

		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			base.OnNavigatedTo(e);
			if (e.Parameter is not NavigationArguments arguments)
				return;

			appInstance = arguments.AssociatedTabInstance;
			pairPath = arguments.NavPathParam ?? string.Empty;
			pair = SyncManager.Instance.Find(pairPath);
			await UpdateShellAsync();
			ShowPair();
		}

		protected override void OnNavigatedFrom(NavigationEventArgs e)
		{
			// Leaving the page does not stop a copy that is under way: it finishes, and the page shows the result next time
			base.OnNavigatedFrom(e);
		}

		private void ShowPair()
		{
			if (pair is null)
			{
				PageTitle.Text = Strings.ConsystoSyncNotFound.GetLocalizedResource();
				ScanButton.IsEnabled = false;
				return;
			}

			PageTitle.Text = pair.Name;
			SourceLink.Content = pair.Source;
			TargetLink.Content = pair.Target;
			LastRunText.Text = pair.LastRun is { } lastRun
				? string.Format(Strings.ConsystoSyncLastRun.GetLocalizedResource(), lastRun.ToString("g", CultureInfo.CurrentCulture), pair.LastResult)
				: Strings.ConsystoSyncNeverRun.GetLocalizedResource();
			UpdateTabs();
		}

		private async void Scan_Click(object sender, RoutedEventArgs e)
			=> await ScanAsync();

		private async Task ScanAsync()
		{
			if (pair is null)
				return;

			if (!SystemIO.Directory.Exists(pair.Source))
			{
				ShowResult(InfoBarSeverity.Error, Strings.ConsystoSyncSourceMissing.GetLocalizedResource());
				return;
			}

			var cancellation = BeginWork();
			ProgressBar.IsIndeterminate = true;
			ProgressText.Text = Strings.ConsystoSyncScanning.GetLocalizedResource();
			var progress = new Progress<BackupProgress>(value =>
			{
				ProgressText.Text = string.Format(CultureInfo.CurrentCulture, Strings.ConsystoSyncScanningCount.GetLocalizedResource(), value.Done);
				ProgressPath.Text = value.Path ?? string.Empty;
			});

			try
			{
				var source = pair.Source;
				var target = pair.Target;
				var exclusions = pair.Exclusions.ToArray();
				plan = await Task.Run(() => BackupPlanner.Scan(source, target, exclusions, progress, cancellation.Token), cancellation.Token);
				ShowPlan();
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The backup check failed");
				ShowResult(InfoBarSeverity.Error, ex.Message);
			}
			finally
			{
				EndWork(cancellation);
			}
		}

		private void ShowPlan()
		{
			if (plan is null)
				return;

			var copy = plan.ToCopy(IncludeTargetNewerBox.IsChecked == true).ToList();
			var newer = plan.Items.Count(item => item.Kind is BackupItemKind.TargetNewer);
			ShowResult(
				copy.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational,
				copy.Count == 0
					? string.Format(Strings.ConsystoSyncUpToDate.GetLocalizedResource(), plan.FilesChecked)
					: string.Format(Strings.ConsystoSyncPlanSummary.GetLocalizedResource(), copy.Count, copy.Sum(item => item.Size).ToSizeString(), plan.FilesChecked)
						+ (newer > 0 && IncludeTargetNewerBox.IsChecked != true ? " " + string.Format(Strings.ConsystoSyncTargetNewerSkipped.GetLocalizedResource(), newer) : string.Empty));
			RunButton.IsEnabled = copy.Count > 0;
			UpdateTabs();
			ShowRows();
		}

		private void UpdateTabs()
		{
			int CountOf(BackupItemKind? kind) => plan is null ? 0 : plan.Items.Count(item => kind is null || item.Kind == kind);
			AllTab.Text = $"{Strings.ConsystoSyncAll.GetLocalizedResource()} ({CountOf(null)})";
			NewTab.Text = $"{Strings.ConsystoSyncNew.GetLocalizedResource()} ({CountOf(BackupItemKind.New)})";
			ChangedTab.Text = $"{Strings.ConsystoSyncChanged.GetLocalizedResource()} ({CountOf(BackupItemKind.Changed)})";
			TargetNewerTab.Text = $"{Strings.ConsystoSyncTargetNewer.GetLocalizedResource()} ({CountOf(BackupItemKind.TargetNewer)})";
		}

		private void KindTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
			=> ShowRows();

		/// <summary>Up to a few thousand rows; a first backup of a whole drive is summed up above rather than listed file by file.</summary>
		private void ShowRows()
		{
			rows.Clear();
			if (plan is null)
			{
				HintText.Visibility = Visibility.Visible;
				return;
			}

			BackupItemKind? kind = (KindTabs.SelectedItem?.Tag as string) switch
			{
				"New" => BackupItemKind.New,
				"Changed" => BackupItemKind.Changed,
				"TargetNewer" => BackupItemKind.TargetNewer,
				_ => null,
			};
			foreach (var item in plan.Items.Where(item => kind is null || item.Kind == kind).Take(5000))
				rows.Add(new BackupRow(item));

			HintText.Visibility = Visibility.Collapsed;
		}

		private void IncludeTargetNewer_Click(object sender, RoutedEventArgs e)
			=> ShowPlan();

		private async void Run_Click(object sender, RoutedEventArgs e)
		{
			if (pair is null || plan is null)
				return;

			var includeTargetNewer = IncludeTargetNewerBox.IsChecked == true;
			var cancellation = BeginWork();
			ProgressBar.IsIndeterminate = false;
			var progress = new Progress<BackupProgress>(value =>
			{
				// Copy progress arrives every megabyte; the screen needs a few updates a second
				if (DateTime.UtcNow - lastProgress < ProgressInterval && value.Path is not null)
					return;

				lastProgress = DateTime.UtcNow;
				ProgressBar.Maximum = Math.Max(1, value.TotalBytes);
				ProgressBar.Value = value.Bytes;
				ProgressText.Text = string.Format(CultureInfo.CurrentCulture, Strings.ConsystoSyncCopying.GetLocalizedResource(),
					value.Done, value.Total, value.Bytes.ToSizeString(), value.TotalBytes.ToSizeString());
				ProgressPath.Text = value.Path ?? string.Empty;
			});

			try
			{
				var current = plan;
				var (copied, failed) = await Task.Run(() => BackupPlanner.Execute(current, includeTargetNewer, progress, cancellation.Token), cancellation.Token);
				var result = failed.Count == 0
					? string.Format(Strings.ConsystoSyncDone.GetLocalizedResource(), copied)
					: string.Format(Strings.ConsystoSyncDoneWithErrors.GetLocalizedResource(), copied, failed.Count, string.Join("; ", failed.Take(3).Select(failure => $"{failure.Path}: {failure.Error}")));
				SyncManager.Instance.RecordRun(pair, result);
				ShowPair();
				ShowResult(failed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning, result);
				plan = null;
				RunButton.IsEnabled = false;
				rows.Clear();
				UpdateTabs();
			}
			catch (OperationCanceledException)
			{
				ShowResult(InfoBarSeverity.Warning, Strings.ConsystoSyncCancelled.GetLocalizedResource());
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The backup failed");
				ShowResult(InfoBarSeverity.Error, ex.Message);
			}
			finally
			{
				EndWork(cancellation);
			}
		}

		private void Cancel_Click(object sender, RoutedEventArgs e)
			=> work?.Cancel();

		private CancellationTokenSource BeginWork()
		{
			work?.Cancel();
			var cancellation = new CancellationTokenSource();
			work = cancellation;
			ResultBar.IsOpen = false;
			ProgressPanel.Visibility = Visibility.Visible;
			ProgressPath.Text = string.Empty;
			ScanButton.IsEnabled = false;
			RunButton.IsEnabled = false;
			CancelButton.Visibility = Visibility.Visible;
			return cancellation;
		}

		private void EndWork(CancellationTokenSource cancellation)
		{
			if (work != cancellation)
				return;

			work = null;
			ProgressPanel.Visibility = Visibility.Collapsed;
			ScanButton.IsEnabled = pair is not null;
			RunButton.IsEnabled = plan?.ToCopy(IncludeTargetNewerBox.IsChecked == true).Any() == true;
			CancelButton.Visibility = Visibility.Collapsed;
		}

		private void ShowResult(InfoBarSeverity severity, string message)
		{
			ResultBar.Severity = severity;
			ResultBar.Message = message;
			ResultBar.IsOpen = true;
		}

		private async void Edit_Click(object sender, RoutedEventArgs e)
		{
			if (pair is not null && await SyncDialogs.EditAsync(pair) is not null)
			{
				plan = null;
				rows.Clear();
				RunButton.IsEnabled = false;
				ShowPair();
				await UpdateShellAsync();
			}
		}

		private async void RemovePair_Click(object sender, RoutedEventArgs e)
		{
			if (pair is null)
				return;

			var dialog = new ContentDialog
			{
				Title = Strings.ConsystoSyncRemovePair.GetLocalizedResource(),
				Content = string.Format(Strings.ConsystoSyncRemoveQuestion.GetLocalizedResource(), pair.Name),
				PrimaryButtonText = Strings.ConsystoSyncRemovePair.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DefaultButton = ContentDialogButton.Close,
				XamlRoot = XamlRoot,
			};
			if (await dialog.TryShowAsync() != ContentDialogResult.Primary)
				return;

			SyncManager.Instance.Remove(pair);
			appInstance?.NavigateHome();
		}

		private async void SourceLink_Click(object sender, RoutedEventArgs e)
		{
			if (pair is not null)
				await NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), pair.Source, true);
		}

		private async void TargetLink_Click(object sender, RoutedEventArgs e)
		{
			if (pair is not null && SystemIO.Directory.Exists(pair.Target))
				await NavigationHelpers.AddNewTabByPathAsync(typeof(ShellPanesPage), pair.Target, true);
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
			await shellViewModel.SetWorkingDirectoryAsync(pairPath);
			shell.SlimContentPage?.StatusBarViewModel.UpdateGitInfo(false, string.Empty, null);

			var title = SyncManager.Instance.TitleOf(pairPath);
			shell.ToolbarViewModel.PathComponents.Clear();
			shell.ToolbarViewModel.PathComponents.Add(new PathBoxItem()
			{
				Title = title,
				Path = pairPath,
				ChevronToolTip = string.Format(Strings.BreadcrumbBarChevronButtonToolTip.GetLocalizedResource(), title),
			});
		}
	}

	/// <summary>One file of the check, as the list shows it.</summary>
	public sealed partial class BackupRow(BackupItem item)
	{
		public string Path { get; } = item.RelativePath;

		public string SizeText { get; } = item.Size.ToSizeString();

		public string SourceTimeText { get; } = item.SourceModified.ToString("g", CultureInfo.CurrentCulture);

		public string TargetTimeText { get; } = item.TargetModified?.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;

		public string KindText { get; } = (item.Kind switch
		{
			BackupItemKind.New => Strings.ConsystoSyncNew,
			BackupItemKind.Changed => Strings.ConsystoSyncChanged,
			_ => Strings.ConsystoSyncTargetNewer,
		}).GetLocalizedResource();

		public Brush? KindBrush { get; } = Application.Current.Resources.TryGetValue(item.Kind switch
		{
			BackupItemKind.New => "SystemFillColorSuccessBrush",
			BackupItemKind.Changed => "AccentTextFillColorPrimaryBrush",
			_ => "SystemFillColorCautionBrush",
		}, out var brush) ? brush as Brush : null;
	}
}
