using Files.App.Views.Shells;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Specialized;

namespace Files.App.MacStyle
{
	/// <summary>
	/// Consysto fork: Commander One-like header over a shell pane in dual-pane mode, with a drive bar (the current drive
	/// highlighted and its free space shown) and the pane's own breadcrumb path. Both navigate this pane, not the active
	/// one. Hidden while the tab shows a single pane, where the toolbar already shows the path.
	/// </summary>
	public sealed partial class PaneHeader : UserControl
	{
		private readonly DrivesViewModel _drivesViewModel = Ioc.Default.GetRequiredService<DrivesViewModel>();
		private readonly List<DriveItem> _trackedDrives = [];

		private BaseShellPage? _shell;
		private IShellPanesPage? _paneHolder;
		private bool _isSubscribed;

		public PaneHeader()
		{
			InitializeComponent();

			Loaded += PaneHeader_Loaded;
			Unloaded += PaneHeader_Unloaded;
		}

		public void Attach(BaseShellPage shell)
		{
			_shell = shell;
			PathBar.ItemsSource = shell.ToolbarViewModel.PathComponents;
		}

		private void PaneHeader_Loaded(object sender, RoutedEventArgs e)
		{
			if (_shell is null || _isSubscribed)
				return;

			_isSubscribed = true;
			_shell.PropertyChanged += Shell_PropertyChanged;
			_shell.ToolbarViewModel.PathComponents.CollectionChanged += PathComponents_CollectionChanged;
			_drivesViewModel.Drives.CollectionChanged += Drives_CollectionChanged;
			SetPaneHolder(_shell.PaneHolder);

			RebuildDrives();
			UpdateState();
		}

		private void PaneHeader_Unloaded(object sender, RoutedEventArgs e)
		{
			if (_shell is null || !_isSubscribed)
				return;

			_isSubscribed = false;
			_shell.PropertyChanged -= Shell_PropertyChanged;
			_shell.ToolbarViewModel.PathComponents.CollectionChanged -= PathComponents_CollectionChanged;
			_drivesViewModel.Drives.CollectionChanged -= Drives_CollectionChanged;
			SetPaneHolder(null);
			UntrackDrives();
		}

		private void SetPaneHolder(IShellPanesPage? paneHolder)
		{
			if (_paneHolder is not null)
				_paneHolder.PropertyChanged -= PaneHolder_PropertyChanged;

			_paneHolder = paneHolder;

			if (_paneHolder is not null)
				_paneHolder.PropertyChanged += PaneHolder_PropertyChanged;
		}

		private void Shell_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(BaseShellPage.PaneHolder))
				SetPaneHolder(_shell?.PaneHolder);

			if (e.PropertyName is nameof(BaseShellPage.PaneHolder) or nameof(BaseShellPage.IsCurrentInstance))
				UpdateState();
		}

		private void PaneHolder_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IShellPanesPage.IsMultiPaneActive) or nameof(IShellPanesPage.ActivePane) or nameof(IShellPanesPage.IsSyncNavigationEnabled))
				UpdateState();
		}

		// The working directory is updated around the path components, so read it once the navigation has settled.
		private void PathComponents_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
			=> DispatcherQueue.TryEnqueue(UpdateCurrentDrive);

		// Drives are added and removed by a device watcher, possibly off the UI thread.
		private void Drives_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
			=> DispatcherQueue.TryEnqueue(RebuildDrives);

		private void Drive_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(DriveItem.Icon) or nameof(DriveItem.SpaceText) or nameof(DriveItem.Text))
				DispatcherQueue.TryEnqueue(RebuildDrives);
		}

		private void UpdateState()
		{
			Visibility = _paneHolder?.IsMultiPaneActive == true ? Visibility.Visible : Visibility.Collapsed;
			ActiveTint.Opacity = _shell?.IsCurrentInstance == true ? 0.08 : 0;
			SyncNavigationButton.IsChecked = _paneHolder?.IsSyncNavigationEnabled == true;
		}

		private async void CompareFoldersButton_Click(object sender, RoutedEventArgs e)
			=> await Ioc.Default.GetRequiredService<ICommandManager>().CompareFolders.ExecuteAsync();

		private void ClosePaneButton_Click(object sender, RoutedEventArgs e)
		{
			if (_paneHolder is null || _shell is null)
				return;

			if (ReferenceEquals(_paneHolder.ActivePane, _shell))
				_paneHolder.CloseActivePane();
			else
				_paneHolder.CloseOtherPane();
		}

		private void SyncNavigationButton_Click(object sender, RoutedEventArgs e)
		{
			if (_paneHolder is not null)
				_paneHolder.IsSyncNavigationEnabled = SyncNavigationButton.IsChecked == true;

			UpdateState();
		}

		private void UntrackDrives()
		{
			foreach (var drive in _trackedDrives)
				drive.PropertyChanged -= Drive_PropertyChanged;
			_trackedDrives.Clear();
		}

		private void RebuildDrives()
		{
			UntrackDrives();
			DrivesPanel.Children.Clear();

			foreach (var drive in _drivesViewModel.Drives.OfType<DriveItem>().ToList())
			{
				if (string.IsNullOrEmpty(drive.Path))
					continue;

				drive.PropertyChanged += Drive_PropertyChanged;
				_trackedDrives.Add(drive);

				var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
				content.Children.Add(new Image { Width = 16, Height = 16, Source = drive.Icon });
				content.Children.Add(new TextBlock { Text = GetDriveLabel(drive), VerticalAlignment = VerticalAlignment.Center });

				var button = new Button { Content = content, Tag = drive };
				ToolTipService.SetToolTip(button, drive.Text);
				button.Click += DriveButton_Click;
				DrivesPanel.Children.Add(button);
			}

			UpdateCurrentDrive();
		}

		private void UpdateCurrentDrive()
		{
			var workingDirectory = _shell?.ShellViewModel?.WorkingDirectory;
			var currentDrive = string.IsNullOrEmpty(workingDirectory)
				? null
				: _trackedDrives
					.Where(drive => workingDirectory.StartsWith(drive.Path!, StringComparison.OrdinalIgnoreCase) ||
						workingDirectory.Equals(drive.Path!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
					.OrderByDescending(drive => drive.Path!.Length)
					.FirstOrDefault();

			var normalStyle = (Style)Resources["DriveButtonStyle"];
			var currentStyle = (Style)Resources["CurrentDriveButtonStyle"];
			foreach (var child in DrivesPanel.Children)
			{
				if (child is Button button)
					button.Style = ReferenceEquals(button.Tag, currentDrive) ? currentStyle : normalStyle;
			}

			FreeSpaceText.Text = currentDrive?.SpaceText ?? string.Empty;
		}

		// Letter drives show as "C:", everything else (network, cloud) by its display name.
		private static string GetDriveLabel(DriveItem drive)
			=> drive.Path is { Length: <= 3 } path && path.Length >= 2 && path[1] == ':'
				? path[..2]
				: drive.Text ?? drive.Path ?? string.Empty;

		private void DriveButton_Click(object sender, RoutedEventArgs e)
		{
			if (sender is Button { Tag: DriveItem { Path: { } path } })
				_shell?.NavigateToPath(path);
		}

		private void PathBar_ItemClicked(Files.App.Controls.BreadcrumbBar sender, Files.App.Controls.BreadcrumbBarItemClickedEventArgs args)
		{
			if (_shell is null)
				return;

			if (args.IsRootItem)
			{
				_shell.NavigateHome();
				return;
			}

			// The last component is the current folder: nothing to navigate to.
			var components = _shell.ToolbarViewModel.PathComponents;
			if (args.Index < 0 || args.Index >= components.Count - 1 || components[args.Index].Path is not { } path)
				return;

			_shell.NavigateToPath(path);
		}
	}
}
