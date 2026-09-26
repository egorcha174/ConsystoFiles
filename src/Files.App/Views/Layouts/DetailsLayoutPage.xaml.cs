// Copyright (c) Files Community
// Licensed under the MIT License.

using CommunityToolkit.WinUI;
using Files.App.Controls;
using Files.App.UserControls.Selection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using WinRT;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using SortDirection = Files.App.Data.Enums.SortDirection;

namespace Files.App.Views.Layouts
{
	/// <summary>
	/// Represents the browser page of Details View
	/// </summary>
	[WinRT.GeneratedBindableCustomProperty([nameof(RowHeight), nameof(ColumnsViewModel), nameof(MaxWidthForRenameTextbox)], [])]
	public sealed partial class DetailsLayoutPage : BaseGroupableLayoutPage
	{
		// Constants

		private const int TAG_TEXT_BLOCK = 1;

		// Fields

		private ListedItem? _nextItemToSelect;

		/// <summary>
		/// This reference is used to prevent unnecessary icon reloading by only reloading icons when their
		/// size changes, even if the layout size changes (since some layout sizes share the same icon size).
		/// </summary>
		private uint currentIconSize;
		private DetailsViewSizeKind? itemContainerSize;

		private DispatcherQueueTimer? _autoFitColumnsTimer;

		// Properties

		protected override ListViewBase ListViewBase => FileList;
		protected override SemanticZoom RootZoom => RootGridZoom;

		[DynamicWindowsRuntimeCast(typeof(ItemsStackPanel))]
		protected override (int First, int Last) GetVisibleIndexRange()
			=> FileList.ItemsPanelRoot is ItemsStackPanel panel ? (panel.FirstVisibleIndex, panel.LastVisibleIndex) : (-1, -1);

		public ColumnsViewModel ColumnsViewModel { get; } = new();

		private RelayCommand<string>? UpdateSortOptionsCommand { get; set; }

		public ScrollViewer? ContentScroller { get; private set; }

		private double maxWidthForRenameTextbox;
		public double MaxWidthForRenameTextbox
		{
			get => maxWidthForRenameTextbox;
			set
			{
				if (value != maxWidthForRenameTextbox)
				{
					maxWidthForRenameTextbox = value;
					NotifyPropertyChanged(nameof(MaxWidthForRenameTextbox));
				}
			}
		}

		/// <summary>
		/// Row height for items in the Details View
		/// </summary>
		public int RowHeight
		{
			get => LayoutSizeKindHelper.GetDetailsViewRowHeight((DetailsViewSizeKind)UserSettingsService.LayoutSettingsService.DetailsViewSize);
		}


		// Constructor

		public DetailsLayoutPage() : base()
		{
			InitializeComponent();
			DataContext = this;
			var selectionRectangle = RectangleSelection.Create(FileList, SelectionRectangle, FileList_SelectionChanged);
			selectionRectangle.SelectionStarted += SelectionRectangle_SelectionStarted;
			selectionRectangle.SelectionEnded += SelectionRectangle_SelectionEnded;

			UpdateSortOptionsCommand = new RelayCommand<string>(x =>
			{
				// Consysto fork: releasing a dragged column header must not sort by it
				if (isDraggingColumn || suppressSortAfterColumnDrag)
					return;
				if (!Enum.TryParse<SortOption>(x, out var val))
					return;
				var folderSettings = FolderSettings
					?? throw new InvalidOperationException("The details layout does not have folder settings.");
				if (folderSettings.DirectorySortOption == val)
				{
					folderSettings.DirectorySortDirection = (SortDirection)(((int)folderSettings.DirectorySortDirection + 1) % 2);
				}
				else
				{
					folderSettings.DirectorySortOption = val;
					folderSettings.DirectorySortDirection = SortDirection.Ascending;
				}
			});

			InitializeColumnReordering();
		}

		// Methods

		protected override void ItemManipulationModel_ScrollIntoViewInvoked(object? sender, ListedItem e)
		{
			FileList.ScrollIntoView(e);
			ContentScroller?.ChangeView(null, FileList.Items.IndexOf(e) * RowHeight, null, true); // Scroll to index * item height
		}

		protected override void ItemManipulationModel_ScrollToTopInvoked(object? sender, EventArgs e)
		{
			ContentScroller?.ChangeView(null, 0, null, true);
		}

		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		protected override void ItemManipulationModel_FocusSelectedItemsInvoked(object? sender, EventArgs e)
		{
			if (SelectedItems?.Any() ?? false)
			{
				FileList.ScrollIntoView(SelectedItems.Last());
				ContentScroller?.ChangeView(null, FileList.Items.IndexOf(SelectedItems.Last()) * RowHeight, null, false);
				(FileList.ContainerFromItem(SelectedItems.Last()) as ListViewItem)?.Focus(FocusState.Keyboard);
			}
		}

		protected override void ItemManipulationModel_AddSelectedItemInvoked(object? sender, ListedItem e)
		{
			if (NextRenameIndex != 0)
			{
				_nextItemToSelect = e;
				FileList.LayoutUpdated += FileList_LayoutUpdated;
			}
			else if (FileList?.Items.Contains(e) ?? false)
				FileList!.SelectedItems.Add(e);
		}

		protected override void ItemManipulationModel_RemoveSelectedItemInvoked(object? sender, ListedItem e)
		{
			if (FileList?.Items.Contains(e) ?? false)
				FileList.SelectedItems.Remove(e);
		}

		protected override void OnNavigatedTo(NavigationEventArgs eventArgs)
		{
			if (eventArgs.Parameter is NavigationArguments navArgs)
				navArgs.FocusOnNavigation = true;

			base.OnNavigatedTo(eventArgs);

			var parentShellPage = ParentShellPageInstance
				?? throw new InvalidOperationException("The details layout must be associated with a shell page.");
			var shellViewModel = parentShellPage.GetRequiredShellViewModel();
			var folderSettings = FolderSettings
				?? throw new InvalidOperationException("The details layout requires folder settings.");

			currentIconSize = LayoutSizeKindHelper.GetIconSize(FolderLayoutModes.DetailsView);

			if (FolderSettings?.ColumnsViewModel is not null)
			{
				// Don't assign the columns view model directly, instead update each property individually using the Update method.
				// This is done to workaround a bug where CsWinRT doesn't properly track the memory of the object so that
				// an invalid memory access can occur when the object is moved.
				// See https://github.com/microsoft/CsWinRT/issues/1834.
				ColumnsViewModel.DateCreatedColumn.Update(FolderSettings.ColumnsViewModel.DateCreatedColumn);
				ColumnsViewModel.DateDeletedColumn.Update(FolderSettings.ColumnsViewModel.DateDeletedColumn);
				ColumnsViewModel.DateModifiedColumn.Update(FolderSettings.ColumnsViewModel.DateModifiedColumn);
				ColumnsViewModel.IconColumn.Update(FolderSettings.ColumnsViewModel.IconColumn);
				ColumnsViewModel.ItemTypeColumn.Update(FolderSettings.ColumnsViewModel.ItemTypeColumn);
				ColumnsViewModel.NameColumn.Update(FolderSettings.ColumnsViewModel.NameColumn);
				ColumnsViewModel.ExtensionColumn.Update(FolderSettings.ColumnsViewModel.ExtensionColumn);
			ColumnsViewModel.BookAuthorColumn.Update(FolderSettings.ColumnsViewModel.BookAuthorColumn);
			ColumnsViewModel.BookSeriesColumn.Update(FolderSettings.ColumnsViewModel.BookSeriesColumn);
			ColumnsViewModel.CadPartNumberColumn.Update(FolderSettings.ColumnsViewModel.CadPartNumberColumn);
			ColumnsViewModel.CadMaterialColumn.Update(FolderSettings.ColumnsViewModel.CadMaterialColumn);
			ColumnsViewModel.CadMassColumn.Update(FolderSettings.ColumnsViewModel.CadMassColumn);
			ColumnsViewModel.CadVersionColumn.Update(FolderSettings.ColumnsViewModel.CadVersionColumn);
			ColumnsViewModel.CadPrintTimeColumn.Update(FolderSettings.ColumnsViewModel.CadPrintTimeColumn);
			ColumnsViewModel.ColumnOrder = FolderSettings.ColumnsViewModel.ColumnOrder;
				ColumnsViewModel.PathColumn.Update(FolderSettings.ColumnsViewModel.PathColumn);
				ColumnsViewModel.OriginalPathColumn.Update(FolderSettings.ColumnsViewModel.OriginalPathColumn);
				ColumnsViewModel.SizeColumn.Update(FolderSettings.ColumnsViewModel.SizeColumn);
				ColumnsViewModel.StatusColumn.Update(FolderSettings.ColumnsViewModel.StatusColumn);
				ColumnsViewModel.TagColumn.Update(FolderSettings.ColumnsViewModel.TagColumn);
				ColumnsViewModel.GitStatusColumn.Update(FolderSettings.ColumnsViewModel.GitStatusColumn);
				ColumnsViewModel.GitLastCommitDateColumn.Update(FolderSettings.ColumnsViewModel.GitLastCommitDateColumn);
				ColumnsViewModel.GitLastCommitMessageColumn.Update(FolderSettings.ColumnsViewModel.GitLastCommitMessageColumn);
				ColumnsViewModel.GitCommitAuthorColumn.Update(FolderSettings.ColumnsViewModel.GitCommitAuthorColumn);
				ColumnsViewModel.GitLastCommitShaColumn.Update(FolderSettings.ColumnsViewModel.GitLastCommitShaColumn);
			}

			shellViewModel.EnabledGitProperties = GetEnabledGitProperties(ColumnsViewModel);

			folderSettings.LayoutModeChangeRequested += FolderSettings_LayoutModeChangeRequested;
			folderSettings.SortDirectionPreferenceUpdated += FolderSettings_SortDirectionPreferenceUpdated;
			folderSettings.SortOptionPreferenceUpdated += FolderSettings_SortOptionPreferenceUpdated;
			shellViewModel.PageTypeUpdated += FilesystemViewModel_PageTypeUpdated;
			shellViewModel.ItemLoadStatusChanged += ShellViewModel_ItemLoadStatusChanged;
			shellViewModel.HasBookItemsChanged += ShellViewModel_HasBookItemsChanged;
			shellViewModel.HasCadItemsChanged += ShellViewModel_HasCadItemsChanged;
			UserSettingsService.LayoutSettingsService.PropertyChanged += LayoutSettingsService_PropertyChanged;
			FileList.Items.VectorChanged += FileListItems_VectorChanged;
			ActualThemeChanged += DetailsLayoutPage_ActualThemeChanged;

			var parameters = (NavigationArguments)eventArgs.Parameter;
			if (parameters.IsLayoutSwitch)
				_ = ReloadItemIconsAsync();

			FilesystemViewModel_PageTypeUpdated(null, new PageTypeUpdatedEventArgs()
			{
				IsTypeCloudDrive = InstanceViewModel?.IsPageTypeCloudDrive ?? false,
				IsTypeRecycleBin = InstanceViewModel?.IsPageTypeRecycleBin ?? false,
				IsTypeGitRepository = InstanceViewModel?.IsGitRepository ?? false,
				IsTypeSearchResults = InstanceViewModel?.IsPageTypeSearchResults ?? false
			});
			UpdateBookColumns();
			UpdateCadColumns();
			UpdateColumnOffsets();

			RootGrid_SizeChanged(null, null);

			SetItemContainerStyle();
		}

		protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
		{
			base.OnNavigatingFrom(e);
			var folderSettings = FolderSettings
				?? throw new InvalidOperationException("The details layout does not have folder settings.");
			folderSettings.LayoutModeChangeRequested -= FolderSettings_LayoutModeChangeRequested;
			folderSettings.SortDirectionPreferenceUpdated -= FolderSettings_SortDirectionPreferenceUpdated;
			folderSettings.SortOptionPreferenceUpdated -= FolderSettings_SortOptionPreferenceUpdated;
			var shellViewModel = ParentShellPageInstance.GetRequiredShellViewModel();
			shellViewModel.PageTypeUpdated -= FilesystemViewModel_PageTypeUpdated;
			shellViewModel.ItemLoadStatusChanged -= ShellViewModel_ItemLoadStatusChanged;
			shellViewModel.HasBookItemsChanged -= ShellViewModel_HasBookItemsChanged;
			shellViewModel.HasCadItemsChanged -= ShellViewModel_HasCadItemsChanged;
			UserSettingsService.LayoutSettingsService.PropertyChanged -= LayoutSettingsService_PropertyChanged;
			FileList.Items.VectorChanged -= FileListItems_VectorChanged;
			ActualThemeChanged -= DetailsLayoutPage_ActualThemeChanged;
			_autoFitColumnsTimer?.Stop();
		}

		public override void Dispose()
		{
			Bindings.StopTracking();
			if (FolderSettings is { } folderSettings)
			{
				folderSettings.LayoutModeChangeRequested -= FolderSettings_LayoutModeChangeRequested;
				folderSettings.SortDirectionPreferenceUpdated -= FolderSettings_SortDirectionPreferenceUpdated;
				folderSettings.SortOptionPreferenceUpdated -= FolderSettings_SortOptionPreferenceUpdated;
			}
			if (ParentShellPageInstance?.ShellViewModel is { } shellViewModel)
			{
				shellViewModel.PageTypeUpdated -= FilesystemViewModel_PageTypeUpdated;
				shellViewModel.ItemLoadStatusChanged -= ShellViewModel_ItemLoadStatusChanged;
				shellViewModel.HasBookItemsChanged -= ShellViewModel_HasBookItemsChanged;
				shellViewModel.HasCadItemsChanged -= ShellViewModel_HasCadItemsChanged;
			}
			UserSettingsService.LayoutSettingsService.PropertyChanged -= LayoutSettingsService_PropertyChanged;
			FileList.Items.VectorChanged -= FileListItems_VectorChanged;
			ActualThemeChanged -= DetailsLayoutPage_ActualThemeChanged;
			_autoFitColumnsTimer?.Stop();
			base.Dispose();
		}

		private void LayoutSettingsService_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(ILayoutSettingsService.DetailsViewSize))
			{
				// Get current scroll position
				var previousOffset = ContentScroller?.VerticalOffset;

				NotifyPropertyChanged(nameof(RowHeight));
				QueueEmptyRowStripesUpdate();

				// Update the container style to match the item size
				SetItemContainerStyle();

				// Restore correct scroll position
				ContentScroller?.ChangeView(null, previousOffset, null);

				// Check if icons need to be reloaded
				var newIconSize = LayoutSizeKindHelper.GetIconSize(FolderLayoutModes.DetailsView);
				if (newIconSize != currentIconSize)
				{
					currentIconSize = newIconSize;
					_ = ReloadItemIconsAsync();
				}
			}
			else
			{
				var settings = sender as ILayoutSettingsService;
				var isDefaultPath = FolderSettings?.IsPathUsingDefaultLayout(ParentShellPageInstance?.ShellViewModel?.CurrentFolder?.ItemPath);
				if (settings is not null && (isDefaultPath ?? true))
				{
					switch (e.PropertyName)
					{
						case nameof(ILayoutSettingsService.ShowFileTagColumn):
							ColumnsViewModel.TagColumn.UserCollapsed = !settings.ShowFileTagColumn;
							break;
						case nameof(ILayoutSettingsService.ShowSizeColumn):
							ColumnsViewModel.SizeColumn.UserCollapsed = !settings.ShowSizeColumn;
							break;
						case nameof(ILayoutSettingsService.ShowTypeColumn):
							ColumnsViewModel.ItemTypeColumn.UserCollapsed = !settings.ShowTypeColumn;
							break;
						case nameof(ILayoutSettingsService.ShowExtensionColumn):
							ColumnsViewModel.ExtensionColumn.UserCollapsed = !settings.ShowExtensionColumn;
							break;
						case nameof(ILayoutSettingsService.ShowBookAuthorColumn):
							ColumnsViewModel.BookAuthorColumn.UserCollapsed = !settings.ShowBookAuthorColumn;
							break;
						case nameof(ILayoutSettingsService.ShowBookSeriesColumn):
							ColumnsViewModel.BookSeriesColumn.UserCollapsed = !settings.ShowBookSeriesColumn;
							break;
						case nameof(ILayoutSettingsService.ShowCadPartNumberColumn):
							ColumnsViewModel.CadPartNumberColumn.UserCollapsed = !settings.ShowCadPartNumberColumn;
							break;
						case nameof(ILayoutSettingsService.ShowCadMaterialColumn):
							ColumnsViewModel.CadMaterialColumn.UserCollapsed = !settings.ShowCadMaterialColumn;
							break;
						case nameof(ILayoutSettingsService.ShowCadMassColumn):
							ColumnsViewModel.CadMassColumn.UserCollapsed = !settings.ShowCadMassColumn;
							break;
						case nameof(ILayoutSettingsService.ShowCadVersionColumn):
							ColumnsViewModel.CadVersionColumn.UserCollapsed = !settings.ShowCadVersionColumn;
							break;
						case nameof(ILayoutSettingsService.ShowCadPrintTimeColumn):
							ColumnsViewModel.CadPrintTimeColumn.UserCollapsed = !settings.ShowCadPrintTimeColumn;
							break;
						case nameof(ILayoutSettingsService.ShowDateCreatedColumn):
							ColumnsViewModel.DateCreatedColumn.UserCollapsed = !settings.ShowDateCreatedColumn;
							break;
						case nameof(ILayoutSettingsService.ShowDateColumn):
							ColumnsViewModel.DateModifiedColumn.UserCollapsed = !settings.ShowDateColumn;
							break;
					}
				}
			}
		}

		/// <summary>
		/// Sets the item size and spacing
		/// </summary>
		private void SetItemContainerStyle()
		{
			var size = UserSettingsService.LayoutSettingsService.DetailsViewSize;
			if (itemContainerSize != size)
			{
				// Changing size still requires a style refresh, even when both sizes use the same style.
				FileList.ItemContainerStyle = size == DetailsViewSizeKind.Compact ? RegularItemContainerStyle : CompactItemContainerStyle;
				FileList.ItemContainerStyle = size == DetailsViewSizeKind.Compact ? CompactItemContainerStyle : RegularItemContainerStyle;
				itemContainerSize = size;
			}

			// Set the width of the icon column. The value is increased by 4px to account for icon overlays.
			ColumnsViewModel.IconColumn.UserLength = new GridLength(LayoutSizeKindHelper.GetIconSize(FolderLayoutModes.DetailsView) + 4);

			// Compact rows use a -2px ItemContainer margin, so the header checkbox needs an extra
			// left offset to stay aligned with row checkboxes.
			var leftOffset = UserSettingsService.LayoutSettingsService.DetailsViewSize == DetailsViewSizeKind.Compact ? -4 : 0;
			SelectAllCheckbox.Margin = new Thickness(leftOffset, 14, 0, 0);
		}

		private void FileList_LayoutUpdated(object? sender, object e)
		{
			FileList.LayoutUpdated -= FileList_LayoutUpdated;
			TryStartRenameNextItem(_nextItemToSelect!);
			_nextItemToSelect = null;
		}

		private void FolderSettings_SortOptionPreferenceUpdated(object? sender, SortOption e)
		{
			UpdateSortIndicator();
		}

		private void FolderSettings_SortDirectionPreferenceUpdated(object? sender, SortDirection e)
		{
			UpdateSortIndicator();
		}

		private void UpdateSortIndicator()
		{
			var folderSettings = FolderSettings
				?? throw new InvalidOperationException("The details layout does not have folder settings.");

			NameHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.Name ? folderSettings.DirectorySortDirection : null;
			ExtensionHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.FileExtension ? folderSettings.DirectorySortDirection : null;
			BookAuthorHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.BookAuthor ? folderSettings.DirectorySortDirection : null;
			BookSeriesHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.BookSeries ? folderSettings.DirectorySortDirection : null;
			CadPartNumberHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.CadPartNumber ? folderSettings.DirectorySortDirection : null;
			CadMaterialHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.CadMaterial ? folderSettings.DirectorySortDirection : null;
			CadMassHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.CadMass ? folderSettings.DirectorySortDirection : null;
			CadVersionHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.CadVersion ? folderSettings.DirectorySortDirection : null;
			CadPrintTimeHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.CadPrintTime ? folderSettings.DirectorySortDirection : null;
			TagHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.FileTag ? folderSettings.DirectorySortDirection : null;
			PathHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.Path ? folderSettings.DirectorySortDirection : null;
			OriginalPathHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.OriginalFolder ? folderSettings.DirectorySortDirection : null;
			DateDeletedHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.DateDeleted ? folderSettings.DirectorySortDirection : null;
			DateModifiedHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.DateModified ? folderSettings.DirectorySortDirection : null;
			DateCreatedHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.DateCreated ? folderSettings.DirectorySortDirection : null;
			FileTypeHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.FileType ? folderSettings.DirectorySortDirection : null;
			ItemSizeHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.Size ? folderSettings.DirectorySortDirection : null;
			SyncStatusHeader.ColumnSortOption = folderSettings.DirectorySortOption == SortOption.SyncStatus ? folderSettings.DirectorySortDirection : null;
		}

		private void FilesystemViewModel_PageTypeUpdated(object? sender, PageTypeUpdatedEventArgs e)
		{
			if (e.IsTypeRecycleBin)
			{
				ColumnsViewModel.OriginalPathColumn.Show();
				ColumnsViewModel.DateDeletedColumn.Show();
			}
			else
			{
				ColumnsViewModel.OriginalPathColumn.Hide();
				ColumnsViewModel.DateDeletedColumn.Hide();
			}

			if (e.IsTypeCloudDrive)
				ColumnsViewModel.StatusColumn.Show();
			else
				ColumnsViewModel.StatusColumn.Hide();

			if (e.IsTypeGitRepository && !e.IsTypeSearchResults)
			{
				ColumnsViewModel.GitCommitAuthorColumn.Show();
				ColumnsViewModel.GitLastCommitDateColumn.Show();
				ColumnsViewModel.GitLastCommitMessageColumn.Show();
				ColumnsViewModel.GitLastCommitShaColumn.Show();
				ColumnsViewModel.GitStatusColumn.Show();
			}
			else
			{
				ColumnsViewModel.GitCommitAuthorColumn.Hide();
				ColumnsViewModel.GitLastCommitDateColumn.Hide();
				ColumnsViewModel.GitLastCommitMessageColumn.Hide();
				ColumnsViewModel.GitLastCommitShaColumn.Hide();
				ColumnsViewModel.GitStatusColumn.Hide();
			}

			if (e.IsTypeSearchResults)
				ColumnsViewModel.PathColumn.Show();
			else
				ColumnsViewModel.PathColumn.Hide();

			UpdateSortIndicator();
		}

		// Consysto fork: like the Git columns outside a repository, the book columns stay hidden in folders without books.
		private void ShellViewModel_HasBookItemsChanged(object? sender, EventArgs e)
			=> UpdateBookColumns();

		private void UpdateBookColumns()
		{
			if (ParentShellPageInstance?.ShellViewModel?.HasBookItems == true)
			{
				ColumnsViewModel.BookAuthorColumn.Show();
				ColumnsViewModel.BookSeriesColumn.Show();
			}
			else
			{
				ColumnsViewModel.BookAuthorColumn.Hide();
				ColumnsViewModel.BookSeriesColumn.Hide();
			}
		}

		// Consysto fork: the Inventor columns stay hidden in folders without Inventor documents, like the book columns.
		private void ShellViewModel_HasCadItemsChanged(object? sender, EventArgs e)
			=> UpdateCadColumns();

		private void UpdateCadColumns()
		{
			// Part number belongs to drawings and models, print time to print jobs; material, mass and version to both
			var shell = ParentShellPageInstance?.ShellViewModel;
			var hasDocuments = shell?.HasCadItems == true;
			void Toggle(DetailsLayoutColumnItem column, bool visible)
			{
				if (visible)
					column.Show();
				else
					column.Hide();
			}

			Toggle(ColumnsViewModel.CadPartNumberColumn, hasDocuments && shell!.HasDrawingItems);
			Toggle(ColumnsViewModel.CadMaterialColumn, hasDocuments);
			Toggle(ColumnsViewModel.CadMassColumn, hasDocuments);
			Toggle(ColumnsViewModel.CadVersionColumn, hasDocuments);
			Toggle(ColumnsViewModel.CadPrintTimeColumn, hasDocuments && shell!.HasPrintItems);
		}

		private void FolderSettings_LayoutModeChangeRequested(object? sender, LayoutModeEventArgs e)
		{

		}

		protected override void OnSelectionChanged(SelectionChangedEventArgs e)
		{
			foreach (var item in e.AddedItems)
				SetCheckboxSelectionState(item);

			foreach (var item in e.RemovedItems)
				SetCheckboxSelectionState(item);

			UpdateSelectAllCheckboxState();
		}

		private bool _suppressSelectAllCheckboxEvents;

		private void SelectAllCheckbox_Checked(object sender, RoutedEventArgs e)
		{
			if (_suppressSelectAllCheckboxEvents)
				return;

			ItemManipulationModel.SelectAllItems();
		}

		private void SelectAllCheckbox_Unchecked(object sender, RoutedEventArgs e)
		{
			if (_suppressSelectAllCheckboxEvents)
				return;

			ItemManipulationModel.ClearSelection();
		}

		private void UpdateSelectAllCheckboxState()
		{
			var selectedCount = FileList.SelectedItems.Count;
			bool? newState = selectedCount == 0
				? false
				: selectedCount == FileList.Items.Count ? true : null;

			if (SelectAllCheckbox.IsChecked == newState)
				return;

			_suppressSelectAllCheckboxEvents = true;
			try
			{
				SelectAllCheckbox.IsChecked = newState;
			}
			finally
			{
				_suppressSelectAllCheckboxEvents = false;
			}
		}

		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		override public void StartRenameItem()
		{
			StartRenameItem("ItemNameTextBox");

			if (FileList.ContainerFromItem(RenamingItem) is not ListViewItem listViewItem)
				return;

			var textBox = listViewItem.FindDescendant("ItemNameTextBox") as TextBox;
			if (textBox is null || textBox.FindParent<Grid>() is null)
				return;

			Grid.SetColumnSpan(textBox.FindParent<Grid>(), 8);
		}

		private void ItemNameTextBox_BeforeTextChanging(TextBox textBox, TextBoxBeforeTextChangingEventArgs args)
		{
			if (IsRenamingItem)
			{
				_ = ValidateItemNameInputTextAsync(textBox, args, (showError) =>
				{
					FileNameTeachingTip.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
					FileNameTeachingTip.IsOpen = showError;
				});
			}
		}

		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		protected override void EndRename(TextBox textBox)
		{
			if (textBox is not null && textBox.FindParent<Grid>() is FrameworkElement parent)
				Grid.SetColumnSpan(parent, 1);

			ListViewItem? listViewItem = FileList.ContainerFromItem(RenamingItem) as ListViewItem;

			if (textBox is null || listViewItem is null)
			{
				// Navigating away, do nothing
			}
			else
			{
				TextBlock? textBlock = listViewItem.FindDescendant("ItemName") as TextBlock;
				textBox.Visibility = Visibility.Collapsed;
				textBlock!.Visibility = Visibility.Visible;
			}

			// Unsubscribe from events
			if (textBox is not null)
			{
				textBox!.LostFocus -= RenameTextBox_LostFocus;
				textBox.KeyDown -= RenameTextBox_KeyDown;
			}

			FileNameTeachingTip.IsOpen = false;
			IsRenamingItem = false;

			// Re-focus selected list item
			listViewItem?.Focus(FocusState.Programmatic);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		[DynamicWindowsRuntimeCast(typeof(HyperlinkButton))]
		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		protected override async void FileList_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
		{
			if (ParentShellPageInstance is null || IsRenamingItem)
				return;

			var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
			var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
			var focusedElement = (FrameworkElement)FocusManager.GetFocusedElement(MainWindow.Instance.Content.XamlRoot);
			var isHeaderFocused = DependencyObjectHelpers.FindParent<DataGridHeader>(focusedElement) is not null;
			var isFooterFocused = focusedElement is HyperlinkButton;

			if (ctrlPressed && e.Key is VirtualKey.A)
			{
				e.Handled = true;

				var commands = Ioc.Default.GetRequiredService<ICommandManager>();
				var hotKey = new HotKey(Keys.A, KeyModifiers.Ctrl);

				await commands[hotKey].ExecuteAsync();
			}
			else if (e.Key == VirtualKey.Enter && !e.KeyStatus.IsMenuKeyDown)
			{
				e.Handled = true;

				if (ctrlPressed && !shiftPressed)
				{
					var folders = SelectedItems?.Where(file => file.PrimaryItemAttribute == StorageItemTypes.Folder);
					if (folders?.Any() ?? false)
					{
						foreach (ListedItem folder in folders)
							await NavigationHelpers.OpenPathInNewTab(folder.ItemPath);
					}
				}
				else if (ctrlPressed && shiftPressed)
				{
					var selectedFolders = SelectedItems?.Where(item => item.PrimaryItemAttribute == StorageItemTypes.Folder);
					if (selectedFolders?.Count() == 1)
					{
						NavigationHelpers.OpenInSecondaryPane(ParentShellPageInstance, selectedFolders.First());
					}
				}
				else if (!ctrlPressed && !shiftPressed)
				{
					if (SelectedItems?.Any() ?? false)
					{
						foreach (var selectedItem in SelectedItems)
							await OpenItem(selectedItem);
					}
				}
			}
			else if (e.Key == VirtualKey.Enter && e.KeyStatus.IsMenuKeyDown)
			{
				FilePropertiesHelpers.OpenPropertiesWindow(ParentShellPageInstance);
				e.Handled = true;
			}
			else if (e.Key == VirtualKey.Space)
			{
				e.Handled = true;
			}
			else if (e.KeyStatus.IsMenuKeyDown && (e.Key == VirtualKey.Left || e.Key == VirtualKey.Right || e.Key == VirtualKey.Up))
			{
				// Unfocus the GridView so keyboard shortcut can be handled
				Focus(FocusState.Pointer);
			}
			else if (e.KeyStatus.IsMenuKeyDown && shiftPressed && e.Key == VirtualKey.Add)
			{
				// Unfocus the ListView so keyboard shortcut can be handled (alt + shift + "+")
				Focus(FocusState.Pointer);
			}
			else if (e.Key == VirtualKey.Down)
			{
				// Focus the first item in the file list if Header header has focus,
				// or if there is only one item in the file list (#13774)
				if (isHeaderFocused || FileList.Items.Count == 1)
				{
					var selectIndex = FileList.SelectedIndex < 0 ? 0 : FileList.SelectedIndex;
					if (FileList.ContainerFromIndex(selectIndex) is ListViewItem item)
					{
						// Focus selected list item or first item
						item.Focus(FocusState.Programmatic);
						if (!IsItemSelected)
							FileList.SelectedIndex = 0;
						e.Handled = true;
					}
				}
			}
		}

		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		protected override bool CanGetItemFromElement(object element)
			=> element is ListViewItem;

		private async Task ReloadItemIconsAsync()
		{
			if (ParentShellPageInstance is not { } parentShellPage)
				return;
			var shellViewModel = parentShellPage.GetRequiredShellViewModel();

			shellViewModel.CancelExtendedPropertiesLoading();
			var filesAndFolders = shellViewModel.FilesAndFolders.ToList();

			await Task.WhenAll(filesAndFolders.Select(listedItem =>
			{
				listedItem.ItemPropertiesInitialized = false;
				if (FileList.ContainerFromItem(listedItem) is not null)
					return shellViewModel.LoadExtendedItemPropertiesAsync(listedItem);
				else
					return Task.CompletedTask;
			}));

			if (shellViewModel.EnabledGitProperties is not GitProperties.None)
			{
				await Task.WhenAll(filesAndFolders.Select(item =>
				{
					if (item is IGitItem gitItem)
						return shellViewModel.LoadGitPropertiesAsync(gitItem);

					return Task.CompletedTask;
				}));
			}
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		[DynamicWindowsRuntimeCast(typeof(TextBox))]
		[DynamicWindowsRuntimeCast(typeof(Rectangle))]
		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		private async void FileList_ItemTapped(object sender, TappedRoutedEventArgs e)
		{
			var clickedItem = e.OriginalSource as FrameworkElement;
			var ctrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
			var shiftPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
			var item = clickedItem?.DataContext as ListedItem;
			if (item is null)
			{
				if (IsRenamingItem && RenamingItem is not null)
				{
					ListViewItem? listViewItem = FileList.ContainerFromItem(RenamingItem) as ListViewItem;
					if (listViewItem is not null)
					{
						var textBox = listViewItem.FindDescendant("ItemNameTextBox") as TextBox;
						if (textBox is not null)
							await CommitRenameAsync(textBox);
					}
				}
				else
				{
					// Clear selection when clicking empty area via touch
					// https://github.com/files-community/Files/issues/15051
					if (e.PointerDeviceType == PointerDeviceType.Touch)
						ItemManipulationModel.ClearSelection();
				}
				return;
			}

			// Skip code if the control or shift key is pressed or if the user is using multiselect
			if
			(
				ctrlPressed ||
				shiftPressed ||
				clickedItem is Microsoft.UI.Xaml.Shapes.Rectangle
			)
			{
				e.Handled = true;
				return;
			}

			// Check if the setting to open items with a single click is turned on
			if ((item.PrimaryItemAttribute is StorageItemTypes.File && UserSettingsService.FoldersSettingsService.OpenFilesWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)) ||
				(item.PrimaryItemAttribute is StorageItemTypes.Folder && UserSettingsService.FoldersSettingsService.OpenFoldersWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType)))
			{
				ResetRenameDoubleClick();
				await Commands.OpenItem.ExecuteAsync();
			}
			else
			{
				if (clickedItem is TextBlock && ((TextBlock)clickedItem).Name == "ItemName")
				{
					CheckRenameDoubleClick(clickedItem.DataContext);
				}
				else if (IsRenamingItem && RenamingItem is not null)
				{
					ListViewItem? listViewItem = FileList.ContainerFromItem(RenamingItem) as ListViewItem;
					if (listViewItem is not null)
					{
						var textBox = listViewItem.FindDescendant("ItemNameTextBox") as TextBox;
						if (textBox is not null)
							await CommitRenameAsync(textBox);
					}
				}
			}
		}

		private async Task OpenItem(ListedItem item)
		{
			if (!Commands.OpenItem.IsExecutable)
			{
				// Fallback if the command is not executable. It occurs only when search is performed from the columns view.
				if (ParentShellPageInstance is not { } parentShellPage)
					throw new InvalidOperationException("The details layout does not have a parent shell page.");

				var itemType = item.PrimaryItemAttribute == StorageItemTypes.Folder ? FilesystemItemType.Directory : FilesystemItemType.File;
				await NavigationHelpers.OpenPath(item.ItemPath!, parentShellPage, itemType);
			}
			else
			{
				await Commands.OpenItem.ExecuteAsync();
			}
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		[DynamicWindowsRuntimeCast(typeof(ListView))]
		private async void FileList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			// Skip opening selected items if the double tap doesn't capture an item
			var originalElement = e.OriginalSource as FrameworkElement;
			var dataContext = originalElement?.DataContext;

			// Try to get the item from DataContext or from sender (ListView)
			ListedItem? item = dataContext as ListedItem;
			if (item == null && sender is ListView listView && listView.SelectedItem is ListedItem selectedItem)
				item = selectedItem;

			if (item != null && item.PrimaryItemAttribute == StorageItemTypes.File && !UserSettingsService.FoldersSettingsService.OpenFilesWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType))
				await OpenItem(item);
			else if (item != null && item.PrimaryItemAttribute == StorageItemTypes.Folder && !UserSettingsService.FoldersSettingsService.OpenFoldersWithSingleClick.ShouldOpenWithSingleClick(e.PointerDeviceType))
				await OpenItem(item);
			else if (item == null && UserSettingsService.FoldersSettingsService.DoubleClickToGoUp)
				await Commands.NavigateUp.ExecuteAsync();

			ResetRenameDoubleClick();
		}

		[DynamicWindowsRuntimeCast(typeof(StackPanel))]
		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		private void StackPanel_Loaded(object sender, RoutedEventArgs e)
		{
			// This is the best way I could find to set the context flyout, as doing it in the styles isn't possible
			// because you can't use bindings in the setters
			DependencyObject item = VisualTreeHelper.GetParent(sender as StackPanel);
			while (item is not ListViewItem)
				item = VisualTreeHelper.GetParent(item);
			if (item is ListViewItem itemContainer)
			{
				itemContainer.ContextFlyout = ItemContextMenuFlyout;

				// Consysto fork: the row panel may not exist yet when ContainerContentChanging runs, so stripe it once it loads.
				ApplyRowStripe(itemContainer, FileList.IndexFromContainer(itemContainer));
				ApplyRowOffsets(itemContainer);
				QueueEmptyRowStripesUpdate();
			}
		}

		private void Grid_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			// This prevents the drag selection rectangle from appearing when resizing the columns
			e.Handled = true;
		}

		private void GridSplitter_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
		{
			UpdateColumnLayout();
		}

		private void GridSplitter_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
		{
			if (e.Key == VirtualKey.Left || e.Key == VirtualKey.Right)
			{
				UpdateColumnLayout();
				var folderSettings = FolderSettings
					?? throw new InvalidOperationException("The details layout does not have folder settings.");
				folderSettings.ColumnsViewModel = ColumnsViewModel;
			}
		}

		private void UpdateColumnLayout()
		{
			QueueEmptyRowStripesUpdate();

			ColumnsViewModel.IconColumn.UserLength = Column2.Width;
			ColumnsViewModel.NameColumn.UserLength = Column3.Width;
			ColumnsViewModel.ExtensionColumn.UserLength = ExtensionColumnDefinition.Width;
			ColumnsViewModel.BookAuthorColumn.UserLength = BookAuthorColumnDefinition.Width;
			ColumnsViewModel.BookSeriesColumn.UserLength = BookSeriesColumnDefinition.Width;
			ColumnsViewModel.CadPartNumberColumn.UserLength = CadPartNumberColumnDefinition.Width;
			ColumnsViewModel.CadMaterialColumn.UserLength = CadMaterialColumnDefinition.Width;
			ColumnsViewModel.CadMassColumn.UserLength = CadMassColumnDefinition.Width;
			ColumnsViewModel.CadVersionColumn.UserLength = CadVersionColumnDefinition.Width;
			ColumnsViewModel.CadPrintTimeColumn.UserLength = CadPrintTimeColumnDefinition.Width;

			// Git
			ColumnsViewModel.GitStatusColumn.UserLength = GitStatusColumnDefinition.Width;
			ColumnsViewModel.GitLastCommitDateColumn.UserLength = GitLastCommitDateColumnDefinition.Width;
			ColumnsViewModel.GitLastCommitMessageColumn.UserLength = GitLastCommitMessageColumnDefinition.Width;
			ColumnsViewModel.GitCommitAuthorColumn.UserLength = GitCommitAuthorColumnDefinition.Width;
			ColumnsViewModel.GitLastCommitShaColumn.UserLength = GitLastCommitShaColumnDefinition.Width;

			ColumnsViewModel.TagColumn.UserLength = Column4.Width;
			ColumnsViewModel.PathColumn.UserLength = Column5.Width;
			ColumnsViewModel.OriginalPathColumn.UserLength = Column6.Width;
			ColumnsViewModel.DateDeletedColumn.UserLength = Column7.Width;
			ColumnsViewModel.DateModifiedColumn.UserLength = Column8.Width;
			ColumnsViewModel.DateCreatedColumn.UserLength = Column9.Width;
			ColumnsViewModel.ItemTypeColumn.UserLength = Column10.Width;
			ColumnsViewModel.SizeColumn.UserLength = Column11.Width;
			ColumnsViewModel.StatusColumn.UserLength = Column12.Width;
		}

		private void RootGrid_SizeChanged(object? sender, SizeChangedEventArgs? e)
		{
			MaxWidthForRenameTextbox = Math.Max(0, RootGrid.ActualWidth - 80);
		}

		private void GridSplitter_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
		{
			this.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
		}

		private void GridSplitter_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
		{
			var folderSettings = FolderSettings
				?? throw new InvalidOperationException("The details layout does not have folder settings.");
			folderSettings.ColumnsViewModel = ColumnsViewModel;
			this.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.Arrow));
		}

		[DynamicWindowsRuntimeCast(typeof(UIElement))]
		private void GridSplitter_Loaded(object sender, RoutedEventArgs e)
		{
			(sender as UIElement)?.ChangeCursor(InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
		}

		private void ToggleMenuFlyoutItem_Click(object sender, RoutedEventArgs e)
		{
			var folderSettings = FolderSettings
				?? throw new InvalidOperationException("The details layout does not have folder settings.");
			folderSettings.ColumnsViewModel = ColumnsViewModel;
			var shellViewModel = ParentShellPageInstance.GetRequiredShellViewModel();
			shellViewModel.EnabledGitProperties = GetEnabledGitProperties(ColumnsViewModel);
		}

		private void GridSplitter_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			var columnToResize = Grid.GetColumn(sender as Files.App.Controls.GridSplitter) / 2 + 1;
			ResizeColumnToFit(columnToResize);

			e.Handled = true;
		}

		private void SizeAllColumnsToFit_Click(object sender, RoutedEventArgs e)
		{
			_ = Commands.AutoFitColumns.ExecuteAsync();
		}

		public void AutoFitColumns()
		{
			if (!FileList.Items.Any())
				return;

			// Scale to whatever DetailsLayoutColumnItem properties exist on ColumnsViewModel so new columns don't need a code change here.
			int totalColumnCount = typeof(ColumnsViewModel).GetProperties().Count(prop => prop.PropertyType == typeof(DetailsLayoutColumnItem));
			for (int columnIndex = 1; columnIndex <= totalColumnCount; columnIndex++)
				ResizeColumnToFit(columnIndex);
		}

		private void AutoFitColumnsIfEnabled()
		{
			if (!UserSettingsService.LayoutSettingsService.AutoSizeColumnsInDetailsLayout)
				return;

			// Trailing debounce so bursts of item adds and extended-property updates during a folder load collapse into one resize.
			_autoFitColumnsTimer ??= DispatcherQueue.CreateTimer();
			_autoFitColumnsTimer.Debounce(
				() => _ = Commands.AutoFitColumns.ExecuteAsync(),
				TimeSpan.FromMilliseconds(250));
		}

		private void ShellViewModel_ItemLoadStatusChanged(object? sender, ItemLoadStatusChangedEventArgs e)
		{
			if (e.Status == ItemLoadStatusChangedEventArgs.ItemLoadStatus.Complete)
				AutoFitColumnsIfEnabled();
		}

		private void FileListItems_VectorChanged(IObservableVector<object> sender, IVectorChangedEventArgs e)
		{
			AutoFitColumnsIfEnabled();

			// Consysto fork: inserts, removals and sorting shift row indexes without re-raising ContainerContentChanging
			// for rows that stay realized, so the zebra parity is re-applied once the list settles.
			DispatcherQueue.TryEnqueue(RefreshRowStripes);
		}

		protected override async Task CommitRenameAsync(TextBox textBox)
		{
			await base.CommitRenameAsync(textBox);
			AutoFitColumnsIfEnabled();
		}

		private void ResizeColumnToFit(int columnToResize)
		{
			if (!FileList.Items.Any())
				return;

			var maxItemLength = columnToResize switch
			{
				1 => 40, // Check all items columns
				2 => FileList.Items.Cast<ListedItem>().Select(x => x.Name?.Length ?? 0).Max(), // file name column
				3 => FileList.Items.Cast<ListedItem>().Select(x => x.FileExtensionDisplay?.Length ?? 0).Max(), // Consysto fork: extension column
				4 => FileList.Items.Cast<ListedItem>().Select(x => x.BookAuthor?.Length ?? 0).Max(), // Consysto fork: book author column
				5 => FileList.Items.Cast<ListedItem>().Select(x => x.BookSeries?.Length ?? 0).Max(), // Consysto fork: book series column
				6 => FileList.Items.Cast<ListedItem>().Select(x => x.CadPartNumber?.Length ?? 0).Max(), // Consysto fork: Inventor part number column
				7 => FileList.Items.Cast<ListedItem>().Select(x => x.CadMaterial?.Length ?? 0).Max(), // Consysto fork: Inventor material column
				8 => FileList.Items.Cast<ListedItem>().Select(x => x.CadMass?.Length ?? 0).Max(), // Consysto fork: Inventor mass column (the book and Inventor columns shift later indexes by five)
				9 => FileList.Items.Cast<ListedItem>().Select(x => x.CadVersion?.Length ?? 0).Max(), // Consysto fork: program version column
				10 => FileList.Items.Cast<ListedItem>().Select(x => x.CadPrintTime?.Length ?? 0).Max(), // Consysto fork: print time column
				12 => FileList.Items.Cast<ListedItem>().Select(x => (x as IGitItem)?.GitLastCommitDateHumanized?.Length ?? 0).Max(), // git
				13 => FileList.Items.Cast<ListedItem>().Select(x => (x as IGitItem)?.GitLastCommitMessage?.Length ?? 0).Max(), // git
				14 => FileList.Items.Cast<ListedItem>().Select(x => (x as IGitItem)?.GitLastCommitAuthor?.Length ?? 0).Max(), // git
				15 => FileList.Items.Cast<ListedItem>().Select(x => (x as IGitItem)?.GitLastCommitSha?.Length ?? 0).Max(), // git
				16 => FileList.Items.Cast<ListedItem>().Select(x => x.FileTagsUI?.Sum(x => x?.Name?.Length ?? 0) ?? 0).Max(), // file tag column
				17 => FileList.Items.Cast<ListedItem>().Select(x => x.ItemPath?.Length ?? 0).Max(), // path column
				18 => FileList.Items.Cast<ListedItem>().Select(x => (x as RecycleBinItem)?.ItemOriginalPath?.Length ?? 0).Max(), // original path column
				19 => FileList.Items.Cast<ListedItem>().Select(x => (x as RecycleBinItem)?.ItemDateDeleted?.Length ?? 0).Max(), // date deleted column
				20 => FileList.Items.Cast<ListedItem>().Select(x => x.ItemDateModified?.Length ?? 0).Max(), // date modified column
				21 => FileList.Items.Cast<ListedItem>().Select(x => x.ItemDateCreated?.Length ?? 0).Max(), // date created column
				22 => FileList.Items.Cast<ListedItem>().Select(x => x.ItemType?.Length ?? 0).Max(), // item type column
				23 => FileList.Items.Cast<ListedItem>().Select(x => x.FileSize?.Length ?? 0).Max(), // item size column
				_ => 20 // cloud status column
			};

			// if called programmatically, the column could be hidden
			// in this case, resizing doesn't need to be done at all
			if (maxItemLength == 0)
				return;

			var columnSizeToFit = MeasureColumnEstimate(columnToResize, 5, maxItemLength);

			if (columnSizeToFit > 1)
			{
				var column = columnToResize switch
				{
					2 => ColumnsViewModel.NameColumn,
					3 => ColumnsViewModel.ExtensionColumn,
					4 => ColumnsViewModel.BookAuthorColumn,
					5 => ColumnsViewModel.BookSeriesColumn,
					6 => ColumnsViewModel.CadPartNumberColumn,
					7 => ColumnsViewModel.CadMaterialColumn,
					8 => ColumnsViewModel.CadMassColumn,
					9 => ColumnsViewModel.CadVersionColumn,
					10 => ColumnsViewModel.CadPrintTimeColumn,
					11 => ColumnsViewModel.GitStatusColumn,
					12 => ColumnsViewModel.GitLastCommitDateColumn,
					13 => ColumnsViewModel.GitLastCommitMessageColumn,
					14 => ColumnsViewModel.GitCommitAuthorColumn,
					15 => ColumnsViewModel.GitLastCommitShaColumn,
					16 => ColumnsViewModel.TagColumn,
					17 => ColumnsViewModel.PathColumn,
					18 => ColumnsViewModel.OriginalPathColumn,
					19 => ColumnsViewModel.DateDeletedColumn,
					20 => ColumnsViewModel.DateModifiedColumn,
					21 => ColumnsViewModel.DateCreatedColumn,
					22 => ColumnsViewModel.ItemTypeColumn,
					23 => ColumnsViewModel.SizeColumn,
					_ => ColumnsViewModel.StatusColumn
				};

				if (columnToResize == 2) // file name column
					columnSizeToFit += 20;

				var minFitLength = Math.Max(columnSizeToFit, column.NormalMinLength);
				var maxFitLength = Math.Min(minFitLength + 36, column.NormalMaxLength); // 36 to account for SortIcon & padding

				column.UserLength = new GridLength(maxFitLength, GridUnitType.Pixel);
			}

			var folderSettings = FolderSettings
				?? throw new InvalidOperationException("The details layout does not have folder settings.");
			folderSettings.ColumnsViewModel = ColumnsViewModel;
		}

		private double MeasureColumnEstimate(int columnIndex, int measureItemsCount, int maxItemLength)
		{
			// Consysto fork: indexes shifted by three for the extension and book columns
			if (columnIndex == 18) // sync status
				return maxItemLength;

			if (columnIndex == 11) // file tag
				return MeasureTagColumnEstimate(columnIndex);

			return MeasureTextColumnEstimate(columnIndex, measureItemsCount, maxItemLength);
		}

		private double MeasureTagColumnEstimate(int columnIndex)
		{
			var grids = DependencyObjectHelpers
				.FindChildren<Grid>(FileList.ItemsPanelRoot)
				.Where(grid => IsCorrectColumn(grid, columnIndex));

			// Get the list of stack panels with the most letters
			var stackPanels = grids
				.Select(DependencyObjectHelpers.FindChildren<StackPanel>)
				.OrderByDescending(sps => sps.Select(sp => DependencyObjectHelpers.FindChildren<TextBlock>(sp).Select(tb => tb.Text.Length).Sum()).Sum())
				.First()
				.ToArray();

			var mesuredSize = stackPanels.Select(x =>
			{
				x.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));

				return x.DesiredSize.Width;
			}).Sum();

			if (stackPanels.Length >= 2)
				mesuredSize += 4 * (stackPanels.Length - 1); // The spacing between the tags

			return mesuredSize;
		}

		private double MeasureTextColumnEstimate(int columnIndex, int measureItemsCount, int maxItemLength)
		{
			var tbs = DependencyObjectHelpers
				.FindChildren<TextBlock>(FileList.ItemsPanelRoot)
				.Where(tb => IsCorrectColumn(tb, columnIndex));

			// heuristic: usually, text with more letters are wider than shorter text with wider letters
			// with this, we can calculate avg width using longest text(s) to avoid overshooting the width
			var widthPerLetter = tbs
				.OrderByDescending(x => x.Text.Length)
				.Where(tb => !string.IsNullOrEmpty(tb.Text))
				.Take(measureItemsCount)
				.Select(tb =>
				{
					var sampleTb = new TextBlock { Text = tb.Text, FontSize = tb.FontSize, FontFamily = tb.FontFamily };
					sampleTb.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));

					return sampleTb.DesiredSize.Width / Math.Max(1, tb.Text.Length);
				});

			if (!widthPerLetter.Any())
				return 0;

			// Take weighted avg between mean and max since width is an estimate
			var weightedAvg = (widthPerLetter.Average() + widthPerLetter.Max()) / 2;
			return weightedAvg * maxItemLength;
		}

		private bool IsCorrectColumn(FrameworkElement element, int columnIndex)
		{
			int columnIndexFromName = element.Name switch
			{
				"ItemName" => 2,
				"ItemExtensionTextBlock" => 3, // Consysto fork: extension and book columns, later indexes shifted by three
				"ItemBookAuthorTextBlock" => 4,
				"ItemBookSeriesTextBlock" => 5,
				"ItemCadPartNumberTextBlock" => 6,
				"ItemCadMaterialTextBlock" => 7,
				"ItemCadMassTextBlock" => 8,
				"ItemCadVersionTextBlock" => 9,
				"ItemCadPrintTimeTextBlock" => 10,
				"ItemGitStatusTextBlock" => 11,
				"ItemGitLastCommitDateTextBlock" => 12,
				"ItemGitLastCommitMessageTextBlock" => 13,
				"ItemGitCommitAuthorTextBlock" => 14,
				"ItemGitLastCommitShaTextBlock" => 15,
				"ItemTagGrid" => 16,
				"ItemPathTextBlock" => 17,
				"ItemOriginalPath" => 18,
				"ItemDateDeleted" => 19,
				"ItemDateModifiedTextBlock" => 20,
				"ItemDateCreatedTextBlock" => 21,
				"ItemTypeTextBlock" => 22,
				"ItemSize" => 23,
				"ItemStatus" => 24,
				_ => -1,
			};

			return columnIndexFromName != -1 && columnIndexFromName == columnIndex;
		}

		private void FileList_Loaded(object sender, RoutedEventArgs e)
		{
			ContentScroller = FileList.FindDescendant<ScrollViewer>(x => x.Name == "ScrollViewer");

			// Consysto fork: keep the empty-space stripes in step with scrolling and resizing
			if (ContentScroller is not null)
			{
				ContentScroller.ViewChanged -= ContentScroller_ViewChanged;
				ContentScroller.ViewChanged += ContentScroller_ViewChanged;
			}
			FileList.SizeChanged -= FileList_SizeChanged;
			FileList.SizeChanged += FileList_SizeChanged;

			const double OffsetCorrection = 88; // HeaderGrid (40) + ListViewHeaderItem (44 + 4 margin)

			RootGridZoom.ViewChangeStarted += (_, args) =>
			{
				if (args.IsSourceZoomedInView || ContentScroller is not { } scroller)
					return;
				void OnZoomScrolled(object? s, ScrollViewerViewChangedEventArgs ve)
				{
					scroller.ViewChanged -= OnZoomScrolled; 
					scroller.ChangeView(0, Math.Max(0, scroller.VerticalOffset - OffsetCorrection), null, true);
				}
				scroller.ViewChanged += OnZoomScrolled;
			};
		}

		private void SetDetailsColumnsAsDefault_Click(object sender, RoutedEventArgs e)
		{
			LayoutPreferencesManager.SetDefaultLayoutPreferences(ColumnsViewModel);
		}

		[DynamicWindowsRuntimeCast(typeof(CheckBox))]
		private void ItemSelected_Checked(object sender, RoutedEventArgs e)
		{
			if (sender is CheckBox checkBox && checkBox.DataContext is ListedItem item && !FileList.SelectedItems.Contains(item))
				FileList.SelectedItems.Add(item);
		}

		[DynamicWindowsRuntimeCast(typeof(CheckBox))]
		private void ItemSelected_Unchecked(object sender, RoutedEventArgs e)
		{
			if (sender is not CheckBox checkBox)
				return;

			if (checkBox.DataContext is ListedItem item && FileList.SelectedItems.Contains(item))
				FileList.SelectedItems.Remove(item);

			// Workaround for #17298
			checkBox.IsTabStop = false;
			checkBox.IsEnabled = false;
			checkBox.IsEnabled = true;
			checkBox.IsTabStop = true;
			FileList.Focus(FocusState.Programmatic);
		}

		[DynamicWindowsRuntimeCast(typeof(CheckBox))]
		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		private new void FileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
		{
			var selectionCheckbox = GetSelectionCheckbox(args.ItemContainer);

			selectionCheckbox.PointerEntered -= SelectionCheckbox_PointerEntered;
			selectionCheckbox.PointerExited -= SelectionCheckbox_PointerExited;
			selectionCheckbox.PointerCanceled -= SelectionCheckbox_PointerCanceled;
			selectionCheckbox.Checked -= ItemSelected_Checked;
			selectionCheckbox.Unchecked -= ItemSelected_Unchecked;

			base.FileList_ContainerContentChanging(sender, args);
			if (args.InRecycleQueue)
				return;

			SetCheckboxSelectionState(args.Item, args.ItemContainer as ListViewItem);
			ApplyRowStripe(args.ItemContainer, args.ItemIndex);
			ApplyRowOffsets(args.ItemContainer);

			selectionCheckbox.PointerEntered += SelectionCheckbox_PointerEntered;
			selectionCheckbox.PointerExited += SelectionCheckbox_PointerExited;
			selectionCheckbox.PointerCanceled += SelectionCheckbox_PointerCanceled;
		}

		// Consysto fork: macOS-like zebra rows. Every other row gets a faint plate as its resting background; hover and
		// selection keep their own brushes, so they still win over the stripe.
		private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _rowStripeLightBrush = new(Windows.UI.Color.FromArgb(0x0A, 0x00, 0x00, 0x00));
		private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _rowStripeDarkBrush = new(Windows.UI.Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));

		// ListViewItemPresenter never paints a resting background (the container's Background is not template-bound), and it
		// must stay the root of the container template, so the stripe is the background of the row's own panel from the item
		// template. The presenter draws hover and selection underneath the content; a 4-5% stripe over them is barely visible.
		[WinRT.DynamicWindowsRuntimeCast(typeof(UserControl))]
		[WinRT.DynamicWindowsRuntimeCast(typeof(Microsoft.UI.Xaml.Controls.Panel))]
		private void ApplyRowStripe(SelectorItem container, int index)
		{
			if (container.ContentTemplateRoot is not UserControl { Content: Microsoft.UI.Xaml.Controls.Panel row })
				return;

			if (index % 2 == 1 && Files.App.Controls.VisualStyle.IsMac)
				row.Background = ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark ? _rowStripeDarkBrush : _rowStripeLightBrush;
			else
				row.ClearValue(Microsoft.UI.Xaml.Controls.Panel.BackgroundProperty);
		}

		[WinRT.DynamicWindowsRuntimeCast(typeof(SelectorItem))]
		private void RefreshRowStripes()
		{
			if (FileList?.ItemsPanelRoot is not { } panel)
				return;

			foreach (var child in panel.Children)
			{
				if (child is SelectorItem container && FileList.IndexFromContainer(container) is var index and >= 0)
					ApplyRowStripe(container, index);
			}

			QueueEmptyRowStripesUpdate();
		}

		private void DetailsLayoutPage_ActualThemeChanged(FrameworkElement sender, object args)
			=> RefreshRowStripes();

		// Consysto fork: like Finder, the stripes go on below the last row down to the bottom of the list. They are drawn
		// on a non-hit-testable canvas, measured from the last realized row so they line up with the real ones.
		private bool _emptyRowStripesQueued;

		private void QueueEmptyRowStripesUpdate()
		{
			if (_emptyRowStripesQueued)
				return;

			_emptyRowStripesQueued = true;
			DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
			{
				_emptyRowStripesQueued = false;
				UpdateEmptyRowStripes();
			});
		}

		[WinRT.DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		[WinRT.DynamicWindowsRuntimeCast(typeof(UserControl))]
		[WinRT.DynamicWindowsRuntimeCast(typeof(Microsoft.UI.Xaml.Controls.Panel))]
		private void UpdateEmptyRowStripes()
		{
			EmptyRowStripes.Children.Clear();

			// The Windows 11 look has plain rows
			if (!Files.App.Controls.VisualStyle.IsMac)
				return;

			var count = FileList.Items.Count;
			if (count == 0 || CollectionViewSource.IsSourceGrouped || FileList.ActualHeight <= 0)
				return;

			// The last row is not realized when the list runs past the viewport: then there is no empty space to fill.
			if (FileList.ContainerFromIndex(count - 1) is not ListViewItem { ContentTemplateRoot: UserControl { Content: Microsoft.UI.Xaml.Controls.Panel lastRow } })
				return;

			var lastTop = lastRow.TransformToVisual(EmptyRowStripes).TransformPoint(default);
			var pitch = lastRow.ActualHeight;
			if (count > 1 && FileList.ContainerFromIndex(count - 2) is ListViewItem { ContentTemplateRoot: UserControl { Content: Microsoft.UI.Xaml.Controls.Panel previousRow } })
				pitch = lastTop.Y - previousRow.TransformToVisual(EmptyRowStripes).TransformPoint(default).Y;
			if (pitch <= 0 || lastRow.ActualWidth <= 0)
				return;

			var listBottom = FileList.TransformToVisual(EmptyRowStripes).TransformPoint(new Windows.Foundation.Point(0, FileList.ActualHeight)).Y;
			var brush = ActualTheme == Microsoft.UI.Xaml.ElementTheme.Dark ? _rowStripeDarkBrush : _rowStripeLightBrush;

			// Shapes on a Canvas are not layout-rounded; snapping to physical pixels keeps the stripe edges as crisp as the rows'.
			var scale = XamlRoot?.RasterizationScale ?? 1d;
			double Snap(double value) => Math.Round(value * scale) / scale;

			var index = count;
			for (var top = lastTop.Y + pitch; top < listBottom; top += pitch, index++)
			{
				if (index % 2 == 0)
					continue;

				var stripeTop = Snap(top);
				var stripe = new Microsoft.UI.Xaml.Shapes.Rectangle
				{
					Width = Snap(lastTop.X + lastRow.ActualWidth) - Snap(lastTop.X),
					Height = Snap(Math.Min(top + pitch, listBottom)) - stripeTop,
					Fill = brush,
				};
				Canvas.SetLeft(stripe, Snap(lastTop.X));
				Canvas.SetTop(stripe, stripeTop);
				EmptyRowStripes.Children.Add(stripe);
			}
		}

		private void ContentScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
			=> QueueEmptyRowStripesUpdate();

		private void FileList_SizeChanged(object sender, SizeChangedEventArgs e)
			=> QueueEmptyRowStripesUpdate();

		private readonly ConditionalWeakTable<SelectorItem, Tuple<object?, CheckBox>> selectionCheckboxCache = new();

		// The template-root identity check invalidates the cache when a container is re-templated
		[DynamicWindowsRuntimeCast(typeof(CheckBox))]
		private CheckBox GetSelectionCheckbox(SelectorItem container)
		{
			var root = container.ContentTemplateRoot;
			if (selectionCheckboxCache.TryGetValue(container, out var cached) && ReferenceEquals(cached.Item1, root))
				return cached.Item2;

			var checkbox = (CheckBox)container.FindDescendant("SelectionCheckbox")!;
			selectionCheckboxCache.AddOrUpdate(container, new Tuple<object?, CheckBox>(root, checkbox));
			return checkbox;
		}

		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		[DynamicWindowsRuntimeCast(typeof(CheckBox))]
		private void SetCheckboxSelectionState(object item, ListViewItem? lviContainer = null)
		{
			var container = lviContainer ?? FileList.ContainerFromItem(item) as ListViewItem;
			if (container is not null)
			{
				var checkbox = container.FindDescendant("SelectionCheckbox") as CheckBox;
				if (checkbox is not null)
				{
					// Temporarily disable events to avoid selecting wrong items
					checkbox.Checked -= ItemSelected_Checked;
					checkbox.Unchecked -= ItemSelected_Unchecked;

					checkbox.IsChecked = FileList.SelectedItems.Contains(item);

					checkbox.Checked += ItemSelected_Checked;
					checkbox.Unchecked += ItemSelected_Unchecked;
				}
				UpdateCheckboxVisibility(container, checkbox?.IsPointerOver ?? false);
			}
		}

		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		[DynamicWindowsRuntimeCast(typeof(StackPanel))]
		private void TagItem_Tapped(object sender, TappedRoutedEventArgs e)
		{
			var tagName = ((sender as StackPanel)?.Children[TAG_TEXT_BLOCK] as TextBlock)?.Text;
			if (tagName is null)
				return;

			ParentShellPageInstance?.SubmitSearch(FolderSearch.FormatTagQuery(tagName));
		}

		[DynamicWindowsRuntimeCast(typeof(UserControl))]
		private void FileTag_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			VisualStateManager.GoToState((UserControl)sender, "PointerOver", true);
		}

		[DynamicWindowsRuntimeCast(typeof(UserControl))]
		private void FileTag_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			VisualStateManager.GoToState((UserControl)sender, "Normal", true);
		}

		[DynamicWindowsRuntimeCast(typeof(StackPanel))]
		[DynamicWindowsRuntimeCast(typeof(FontIcon))]
		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		private async void RemoveTagIcon_Tapped(object sender, TappedRoutedEventArgs e)
		{
			var parent = (sender as FontIcon)?.Parent as StackPanel;
			var tagName = (parent?.Children[TAG_TEXT_BLOCK] as TextBlock)?.Text;

			if (tagName is null || parent?.DataContext is not ListedItem item)
				return;

			var tagId = FileTagsSettingsService.GetTagsByName(tagName).FirstOrDefault()?.Uid;

			if (tagId is not null)
			{
				var fileTags = item.FileTags
					?? throw new InvalidOperationException("The selected item does not have initialized tags.");
				item.FileTags = fileTags
					.Except((string[])[tagId])
					.ToArray();

				if (ParentShellPageInstance is not null)
				{
					var shellViewModel = ParentShellPageInstance.GetRequiredShellViewModel();
					await shellViewModel.RefreshTagGroups();
				}
			}

			e.Handled = true;
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void SelectionCheckbox_PointerEntered(object sender, PointerRoutedEventArgs e)
		{
			UpdateCheckboxVisibility((sender as FrameworkElement)!.FindAscendant<ListViewItem>()!, true);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void SelectionCheckbox_PointerExited(object sender, PointerRoutedEventArgs e)
		{
			UpdateCheckboxVisibility((sender as FrameworkElement)!.FindAscendant<ListViewItem>()!, false);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void SelectionCheckbox_PointerCanceled(object sender, PointerRoutedEventArgs e)
		{
			UpdateCheckboxVisibility((sender as FrameworkElement)!.FindAscendant<ListViewItem>()!, false);
		}

		[DynamicWindowsRuntimeCast(typeof(ListViewItem))]
		private void UpdateCheckboxVisibility(object sender, bool isPointerOver)
		{
			if (sender is ListViewItem control && control.FindDescendant<UserControl>() is UserControl userControl)
			{
				// Handle visual states
				// Show checkboxes when items are selected (as long as the setting is enabled)
				// Show checkboxes when hovering of the thumbnail (regardless of the setting to hide them)
				if (UserSettingsService.FoldersSettingsService.ShowCheckboxesWhenSelectingItems && control.IsSelected
					|| isPointerOver)
					VisualStateManager.GoToState(userControl, "ShowCheckbox", true);
				else
					VisualStateManager.GoToState(userControl, "HideCheckbox", true);
			}
		}

		// Workaround for https://github.com/microsoft/microsoft-ui-xaml/issues/170
		private void TextBlock_IsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs e)
		{
			SetToolTip(sender);
		}

		[DynamicWindowsRuntimeCast(typeof(TextBlock))]
		private void TextBlock_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
		{
			if (sender is TextBlock textBlock)
				SetToolTip(textBlock);
		}

		private void SetToolTip(TextBlock textBlock)
		{
			ToolTipService.SetToolTip(textBlock, textBlock.IsTextTrimmed ? textBlock.Text : null);
		}

		private async void ViewSizeButton_Click(object sender, RoutedEventArgs e)
		{
			if (sender is not FrameworkElement { DataContext: ListedItem item })
				return;

			item.IsCalculatingSize = true;
			var sizeProvider = Ioc.Default.GetRequiredService<Services.SizeProvider.ISizeProvider>();
			var updateTask = Task.Run(() => sizeProvider.UpdateAsync(item.ItemPath!, default));

			try
			{
				if (await Task.WhenAny(updateTask, Task.Delay(300)) != updateTask)
					item.ShowCalculatingText = true;
				await updateTask;
			}
			finally
			{
				item.ShowCalculatingText = false;
				item.IsCalculatingSize = false;
			}
		}

		private void FileList_LosingFocus(UIElement sender, LosingFocusEventArgs args)
		{
			// Fixes an issue where clicking an empty space would scroll to the top of the file list
			if (args.NewFocusedElement == FileList)
				args.TryCancel();
		}

		private void FileListHeader_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			// Fixes an issue where double clicking the column header would navigate back as if clicking on empty space
			e.Handled = true;
		}

		private static GitProperties GetEnabledGitProperties(ColumnsViewModel columnsViewModel)
		{
			var enableStatus = !columnsViewModel.GitStatusColumn.IsHidden && !columnsViewModel.GitStatusColumn.UserCollapsed;
			var enableCommit = !columnsViewModel.GitLastCommitDateColumn.IsHidden && !columnsViewModel.GitLastCommitDateColumn.UserCollapsed
				|| !columnsViewModel.GitLastCommitMessageColumn.IsHidden && !columnsViewModel.GitLastCommitMessageColumn.UserCollapsed
				|| !columnsViewModel.GitCommitAuthorColumn.IsHidden && !columnsViewModel.GitCommitAuthorColumn.UserCollapsed
				|| !columnsViewModel.GitLastCommitShaColumn.IsHidden && !columnsViewModel.GitLastCommitShaColumn.UserCollapsed;
			return (enableStatus, enableCommit) switch
			{
				(true, true) => GitProperties.All,
				(true, false) => GitProperties.Status,
				(false, true) => GitProperties.Commit,
				(false, false) => GitProperties.None
			};
		}
	}
}
