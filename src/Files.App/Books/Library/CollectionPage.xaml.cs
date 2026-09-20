// Consysto fork: a collection page — its items by facet (author, series, genre...), search, details and duplicates.

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Consysto.Collections;
using Consysto.MediaPreview.Music;
using Consysto.MediaPreview.Photos;
using Files.App.Books.Opds;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;

namespace Files.App.Books.Library
{
	public sealed partial class CollectionPage : Page
	{
		private const string AllSection = "all";
		private const string NewestSection = "newest";
		private const string DuplicatesSection = "duplicates";
		private const uint DetailsCoverSize = 640;

		// Sections of the books page that lead to an online catalog; "+" adds one
		private const string CatalogPrefix = "opds:";
		private const string AddCatalog = "+";
		private const string ViewSettingPrefix = "ConsystoCollectionView.";

		private readonly CollectionViewSource tileSource = new() { IsSourceGrouped = true };
		private bool isBuildingSections;
		private readonly ObservableCollection<CollectionRowViewModel> rows = [];
		private readonly Dictionary<string, TextBlock> sectionCounts = [];
		private ICommandManager Commands { get; } = Ioc.Default.GetRequiredService<ICommandManager>();
		private IShellPage? appInstance;
		private string collectionPath = string.Empty;
		private CollectionSettings? collection;
		private CollectionKindInfo? kind;
		private string section = AllSection;
		private string? groupKey;
		private CollectionSnapshot? shown;
		private CollectionItem? detailsItem;

		public CollectionPage()
		{
			InitializeComponent();
			EntryList.ItemsSource = rows;
			ToolTipService.SetToolTip(SettingsButton, Strings.ConsystoLibrarySettings.GetLocalizedResource());
			OpenButton.Content = Strings.ConsystoLibraryOpen.GetLocalizedResource();
			RevealButton.Content = Strings.ConsystoOpdsShowInFolder.GetLocalizedResource();
		}

		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			base.OnNavigatedTo(e);
			if (e.Parameter is NavigationArguments arguments)
			{
				appInstance = arguments.AssociatedTabInstance;
				collectionPath = arguments.NavPathParam ?? string.Empty;
			}

			collection = CollectionManager.Instance.Find(collectionPath);
			kind = CollectionKinds.Find(collection?.KindId);
			PageTitle.Text = collection?.Title ?? Strings.ConsystoCollections.GetLocalizedResource();
			SearchBox.PlaceholderText = kind?.SearchPlaceholder ?? string.Empty;
			SearchBox.Visibility = kind is null ? Visibility.Collapsed : Visibility.Visible;

			// Music is browsed by album first
			section = kind?.Id == CollectionKinds.MusicId ? MusicFields.Album : AllSection;
			BuildSections();

			CollectionManager.Instance.StateChanged += Manager_StateChanged;
			CollectionManager.Instance.ScanProgressChanged += Manager_ScanProgressChanged;
			OpdsCatalogManager.Instance.DataChanged += Catalogs_DataChanged;
			await UpdateShellAsync();
			Refresh(force: true);
		}

		protected override void OnNavigatedFrom(NavigationEventArgs e)
		{
			CollectionManager.Instance.StateChanged -= Manager_StateChanged;
			CollectionManager.Instance.ScanProgressChanged -= Manager_ScanProgressChanged;
			OpdsCatalogManager.Instance.DataChanged -= Catalogs_DataChanged;
			base.OnNavigatedFrom(e);
		}

		private void Catalogs_DataChanged(object? sender, NotifyCollectionChangedEventArgs e)
			=> DispatcherQueue.TryEnqueue(() =>
			{
				BuildSections();
				Refresh(force: true);
			});

		/// <summary>Refresh from the toolbar or Ctrl+R: the collection folders are read again.</summary>
		public Task ReloadAsync()
			=> collection is null ? Task.CompletedTask : CollectionManager.Instance.RescanAsync(collection.Id);

		private async Task UpdateShellAsync()
		{
			if (appInstance is not { } shell)
				return;

			// Like Home, the page has no files: the toolbar hides its folder commands and the preview pane stays closed
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
			shell.ToolbarViewModel.CanRefresh = true;
			shell.ToolbarViewModel.CanGoBack = shell.CanNavigateBackward;
			shell.ToolbarViewModel.CanGoForward = shell.CanNavigateForward;
			shell.ToolbarViewModel.CanNavigateToParent = false;

			var shellViewModel = shell.GetRequiredShellViewModel();
			await shellViewModel.SetWorkingDirectoryAsync(collectionPath);
			shellViewModel.CheckForBackgroundImage();
			shell.SlimContentPage?.StatusBarViewModel.UpdateGitInfo(false, string.Empty, null);

			var title = PageTitle.Text;
			shell.ToolbarViewModel.PathComponents.Clear();
			shell.ToolbarViewModel.PathComponents.Add(new PathBoxItem()
			{
				Title = title,
				Path = collectionPath,
				ChevronToolTip = string.Format(Strings.BreadcrumbBarChevronButtonToolTip.GetLocalizedResource(), title),
			});
		}

		private void BuildSections()
		{
			isBuildingSections = true;
			try
			{
				SectionList.Items.Clear();
				sectionCounts.Clear();
				if (kind is null)
					return;

				AddSection(AllSection, kind.AllTitle, kind.Glyph);
				AddSection(NewestSection, Strings.ConsystoLibraryNewest.GetLocalizedResource(), FluentGlyphs.Recent);
				foreach (var facet in kind.Facets)
					AddSection(facet.Id, facet.Title, facet.Glyph);

				AddSection(DuplicatesSection, Strings.ConsystoLibraryDuplicates.GetLocalizedResource(), FluentGlyphs.Copy);

				if (kind.CanShare)
					AddCatalogSections();

				SelectCurrentSection();
			}
			finally
			{
				isBuildingSections = false;
			}
		}

		/// <summary>Online catalogs such as Flibusta live with the books they bring.</summary>
		private void AddCatalogSections()
		{
			SectionList.Items.Add(new ListViewItem
			{
				Content = new TextBlock
				{
					Text = Strings.ConsystoCatalogsOnline.GetLocalizedResource(),
					Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
					Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
				},
				Margin = new Thickness(0, 12, 0, 0),
				IsHitTestVisible = false,
				IsTabStop = false,
			});

			foreach (var catalog in OpdsCatalogManager.Instance.Catalogs)
			{
				var item = AddSection(CatalogPrefix + catalog.Id, catalog.Title, FluentGlyphs.Catalog);
				var menu = new MenuFlyout();
				menu.Items.Add(MenuItem(Strings.ConsystoOpdsEditCatalog.GetLocalizedResource(), FluentGlyphs.Rename, () => OpdsCatalogDialogs.EditAsync(catalog)));
				menu.Items.Add(MenuItem(Strings.ConsystoOpdsRemoveCatalog.GetLocalizedResource(), FluentGlyphs.Delete, () => OpdsCatalogDialogs.RemoveAsync(catalog)));
				item.ContextFlyout = menu;
			}

			AddSection(CatalogPrefix + AddCatalog, Strings.ConsystoOpdsAddCatalog.GetLocalizedResource(), FluentGlyphs.Add);
		}

		private static MenuFlyoutItem MenuItem(string text, string glyph, Func<Task> action)
		{
			var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
			item.Click += async (_, _) => await action();
			return item;
		}

		private void SelectCurrentSection()
		{
			var current = SectionList.Items.OfType<ListViewItem>().FirstOrDefault(item => item.Tag as string == section);
			SectionList.SelectedItem = current ?? SectionList.Items.FirstOrDefault();
			if (current is null)
				section = AllSection;
		}

		private async Task OpenCatalogAsync(string id)
		{
			if (id == AddCatalog)
			{
				if (await OpdsCatalogDialogs.AddAsync() is not { } added)
					return;

				id = added.Id;
			}

			appInstance?.NavigateToConsystoPage(OpdsPaths.ForCatalog(id));
		}

		private ListViewItem AddSection(string id, string title, string glyph)
		{
			var content = new Grid { ColumnSpacing = 12 };
			content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

			var text = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
			ToolTipService.SetToolTip(text, title);
			var count = new TextBlock
			{
				VerticalAlignment = VerticalAlignment.Center,
				Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
				Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
			};
			Grid.SetColumn(text, 1);
			Grid.SetColumn(count, 2);
			content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
			content.Children.Add(text);
			content.Children.Add(count);

			var item = new ListViewItem { Content = content, Tag = id, HorizontalContentAlignment = HorizontalAlignment.Stretch };
			SectionList.Items.Add(item);
			sectionCounts[id] = count;
			return item;
		}

		private void Manager_StateChanged(object? sender, EventArgs e)
			=> DispatcherQueue.TryEnqueue(() => Refresh(force: false));

		private void Manager_ScanProgressChanged(object? sender, EventArgs e)
			=> DispatcherQueue.TryEnqueue(UpdateScanProgress);

		/// <summary>The step of the running scan, a bar for how far it got, and the folder or file it is at.</summary>
		private void UpdateScanProgress()
		{
			var progress = collection is null ? null : CollectionManager.Instance.ScanProgressOf(collection.Id);
			if (progress is null)
			{
				ScanProgressPanel.Visibility = Visibility.Collapsed;
				return;
			}

			ScanProgressPanel.Visibility = Visibility.Visible;
			var determinate = progress.Total > 0 && progress.Stage is not CollectionScanStage.Listing;
			ScanProgressBar.IsIndeterminate = !determinate;
			if (determinate)
			{
				ScanProgressBar.Maximum = progress.Total;
				ScanProgressBar.Value = Math.Min(progress.Done, progress.Total);
			}

			var culture = System.Globalization.CultureInfo.CurrentCulture;
			ScanProgressText.Text = progress.Stage switch
			{
				CollectionScanStage.Listing => string.Format(culture, Strings.ConsystoCollectionScanListing.GetLocalizedResource(), progress.Done),
				CollectionScanStage.Reading => string.Format(culture, Strings.ConsystoCollectionScanReading.GetLocalizedResource(), progress.Done, progress.Total),
				CollectionScanStage.Comparing => string.Format(culture, Strings.ConsystoCollectionScanComparing.GetLocalizedResource(), progress.Done, progress.Total),
				_ => Strings.ConsystoCollectionScanSaving.GetLocalizedResource(),
			};
			ScanProgressPath.Text = progress.Path ?? string.Empty;
			ToolTipService.SetToolTip(ScanProgressPath, progress.Path);
		}

		private void Refresh(bool force)
		{
			var manager = CollectionManager.Instance;
			collection = manager.Find(collectionPath);
			if (collection is not null)
				PageTitle.Text = collection.Title;

			var library = collection is null ? CollectionSnapshot.Empty : manager.SnapshotOf(collection.Id);
			var scanning = collection is not null && manager.IsScanning(collection.Id);

			ScanRing.IsActive = scanning;
			ScanRing.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
			UpdateScanProgress();
			PageSubtitle.Text = collection is null || kind is null
				? string.Empty
				// The folders of a collection just made are not known here yet, while its scan is already running and finding
				// files: saying that it has no folders would be plainly wrong in front of a page full of drawings
				: collection.Folders.Count == 0 && !scanning && library.Items.Count == 0
					? Strings.ConsystoLibraryNoFolders.GetLocalizedResource()
					: scanning && library.Scanned is null
						? Strings.ConsystoCollectionScanning.GetLocalizedResource()
						: string.Format(kind.CountFormat, library.Items.Count);

			if (!force && ReferenceEquals(library, shown))
				return;

			shown = library;
			if (kind is not null)
			{
				SetCount(AllSection, library.Items.Count);
				foreach (var facet in kind.Facets)
					SetCount(facet.Id, library.Facet(facet.Id).Count);

				SetCount(DuplicatesSection, library.Duplicates.Count);
			}

			ShowList();
		}

		private void SetCount(string id, int count)
		{
			if (sectionCounts.TryGetValue(id, out var text))
				text.Text = count > 0 ? count.ToString(CultureInfo.CurrentCulture) : string.Empty;
		}

		private void ShowList()
		{
			var library = shown ?? CollectionSnapshot.Empty;
			var query = SearchBox.Text;
			var checkedPaths = rows
				.Where(row => row.IsChecked && row.Item is not null)
				.Select(row => row.Item!.Path)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);

			foreach (var row in rows)
				row.PropertyChanged -= Row_PropertyChanged;

			rows.Clear();

			var facet = kind?.Facets.FirstOrDefault(candidate => candidate.Id == section);
			var groups = facet is null ? null : library.Facet(facet.Id);
			var group = groupKey is null ? null : groups?.FirstOrDefault(candidate => candidate.Key == groupKey);
			if (group is null)
				groupKey = null;

			if (kind is not null)
			{
				if (section == DuplicatesSection)
					AddDuplicates(kind, library, query, checkedPaths);
				else if (group is not null)
					AddItems(kind, library.Search(query, group.Items));
				else if (facet is not null && groups is not null)
					AddGroups(kind, facet, groups.Where(candidate => CollectionSnapshot.Matches(candidate.Name, query)));
				else
					AddItems(kind, library.Search(query, section == NewestSection ? library.Newest : library.Items));
			}

			var sectionTitle = SectionTitle(facet);
			ListTitle.Text = group is null ? sectionTitle : $"{sectionTitle} › {group.Name}";
			BackButton.Visibility = group is null ? Visibility.Collapsed : Visibility.Visible;

			var isDuplicates = section == DuplicatesSection;
			ListHint.Text = isDuplicates ? Strings.ConsystoLibraryDuplicatesHint.GetLocalizedResource() : string.Empty;
			ListHint.Visibility = isDuplicates && rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
			DeleteButton.Visibility = isDuplicates && rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
			UpdateDeleteButton();

			EmptyText.Text = EmptyMessage(library, query);
			EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

			if (detailsItem is not null && library.Find(detailsItem.Id) is null)
				CloseDetails();

			UpdateView();
		}

		private string ViewSettingKey
			=> ViewSettingPrefix + (collection?.Id ?? string.Empty);

		/// <summary>Tiles unless the user switched this library to the list.</summary>
		private bool PrefersTiles()
		{
			try
			{
				return AppStorage.LocalSettings[ViewSettingKey] as string != "list";
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "The collection view setting could not be read");
				return true;
			}
		}

		private void ViewButton_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				AppStorage.LocalSettings[ViewSettingKey] = PrefersTiles() ? "list" : "tiles";
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "The collection view setting could not be saved");
			}

			UpdateView();
		}

		// Duplicates are compared side by side, so they stay a list
		private void UpdateView()
		{
			var canTile = kind is not null && section != DuplicatesSection;
			var tiles = canTile && PrefersTiles();

			ViewButton.Visibility = canTile ? Visibility.Visible : Visibility.Collapsed;
			ViewIcon.Glyph = tiles ? FluentGlyphs.List : FluentGlyphs.Tiles;
			ToolTipService.SetToolTip(ViewButton, (tiles ? Strings.ConsystoViewList : Strings.ConsystoViewTiles).GetLocalizedResource());

			EntryList.Visibility = tiles ? Visibility.Collapsed : Visibility.Visible;
			TileGrid.Visibility = tiles ? Visibility.Visible : Visibility.Collapsed;
			if (!tiles)
			{
				TileGrid.ItemsSource = null;
				return;
			}

			if (kind!.Id == CollectionKinds.PhotosId && section != NewestSection && rows.Count > 0 && rows.All(row => row.Item is not null))
			{
				tileSource.Source = new ObservableCollection<CollectionTileGroup>(rows
					.GroupBy(row => MonthOf(row.Item!))
					.OrderByDescending(group => group.Key, StringComparer.Ordinal)
					.Select(group => new CollectionTileGroup(MonthTitle(group.Key), group.OrderByDescending(row => row.Item!.Value(PhotoFields.Taken), StringComparer.Ordinal))));
				TileGrid.ItemsSource = tileSource.View;
			}
			else if (!ReferenceEquals(TileGrid.ItemsSource, rows))
			{
				TileGrid.ItemsSource = rows;
			}
		}

		/// <summary>"2026-09" from the date a photo was taken, or from the file when the photo does not say.</summary>
		private static string MonthOf(CollectionItem photo)
			=> photo.Value(PhotoFields.Taken) is { Length: >= 7 } taken ? taken[..7] : photo.Modified.ToLocalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

		private static string MonthTitle(string month)
		{
			if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
				return month;

			var title = date.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
			return title.Length > 0 ? char.ToUpper(title[0], CultureInfo.CurrentCulture) + title[1..] : title;
		}

		private void AddItems(CollectionKindInfo kind, IEnumerable<CollectionItem> items)
		{
			foreach (var item in items)
				rows.Add(new CollectionRowViewModel { Item = item, Kind = kind, Title = item.Title, Subtitle = kind.Subtitle(item), Glyph = kind.ItemGlyph });
		}

		private void AddGroups(CollectionKindInfo kind, CollectionFacetInfo facet, IEnumerable<CollectionGroup> groups)
		{
			foreach (var group in groups)
				rows.Add(new CollectionRowViewModel { Group = group, Kind = kind, Title = group.Name, Subtitle = string.Format(kind.CountFormat, group.Items.Count), Glyph = facet.Glyph });
		}

		private void AddDuplicates(CollectionKindInfo kind, CollectionSnapshot library, string? query, HashSet<string> checkedPaths)
		{
			var identicalFiles = Strings.ConsystoLibraryIdenticalFiles.GetLocalizedResource();
			var identicalBadge = Strings.ConsystoLibraryIdenticalBadge.GetLocalizedResource();
			var markExtra = Strings.ConsystoLibraryMarkExtra.GetLocalizedResource();

			foreach (var group in library.Duplicates)
			{
				if (library.Search(query, group.Items).Count == 0)
					continue;

				var byline = kind.Byline(group.Items[0]);
				var label = group.Kind == DuplicateKind.IdenticalFiles ? identicalFiles : kind.SameItemLabel;
				var header = byline is null ? $"{group.Title} · {label}" : $"{group.Title} — {byline} · {label}";

				for (var index = 0; index < group.Items.Count; index++)
				{
					var item = group.Items[index];

					// Among different files of one item, the ones that are also identical to each other are pointed out
					var isIdenticalCopy = group.Kind == DuplicateKind.SameItem
						&& item.ContentHash is not null
						&& group.Items.Count(other => other.ContentHash == item.ContentHash) > 1;

					var row = new CollectionRowViewModel
					{
						Item = item,
						Kind = kind,
						Duplicates = group,
						Title = item.FileName,
						Subtitle = SystemIO.Path.GetDirectoryName(item.Path),
						Detail = $"{item.Size.ToSizeString()} · {item.Modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}",
						GroupHeader = index == 0 ? header : null,
						MarkExtraLabel = markExtra,
						Badge = isIdenticalCopy ? identicalBadge : null,
						Glyph = kind.ItemGlyph,
						IsChecked = checkedPaths.Contains(item.Path),
					};
					row.PropertyChanged += Row_PropertyChanged;
					rows.Add(row);
				}
			}
		}

		private string SectionTitle(CollectionFacetInfo? facet)
			=> section switch
			{
				NewestSection => Strings.ConsystoLibraryNewest.GetLocalizedResource(),
				DuplicatesSection => Strings.ConsystoLibraryDuplicates.GetLocalizedResource(),
				_ when facet is not null => facet.Title,
				_ => kind?.AllTitle ?? string.Empty,
			};

		private string EmptyMessage(CollectionSnapshot library, string? query)
		{
			if (collection is null || kind is null)
				return Strings.ConsystoCollectionNotFound.GetLocalizedResource();
			if (collection.Folders.Count == 0)
				return Strings.ConsystoLibraryNoFolders.GetLocalizedResource();
			if (CollectionManager.Instance.IsScanning(collection.Id) && library.Scanned is null)
				return Strings.ConsystoCollectionScanning.GetLocalizedResource();
			if (!string.IsNullOrWhiteSpace(query))
				return Strings.ConsystoLibraryNothingFound.GetLocalizedResource();
			if (section == DuplicatesSection)
				return Strings.ConsystoLibraryNoDuplicates.GetLocalizedResource();

			return Strings.ConsystoLibraryEmpty.GetLocalizedResource();
		}

		private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(CollectionRowViewModel.IsChecked))
				UpdateDeleteButton();
		}

		private void UpdateDeleteButton()
		{
			var count = rows.Count(row => row.IsChecked);
			DeleteButton.Content = string.Format(Strings.ConsystoLibraryDeleteMarked.GetLocalizedResource(), count);
			DeleteButton.IsEnabled = count > 0;
		}

		private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (isBuildingSections || SectionList.SelectedItem is not ListViewItem item)
				return;

			// A catalog opens its own page; this page keeps the section it showed
			if (item.Tag is not string selected || selected.StartsWith(CatalogPrefix, StringComparison.Ordinal))
			{
				DispatcherQueue.TryEnqueue(() =>
				{
					isBuildingSections = true;
					SelectCurrentSection();
					isBuildingSections = false;
				});

				if (item.Tag is string catalog)
					_ = OpenCatalogAsync(catalog[CatalogPrefix.Length..]);
				return;
			}

			section = selected;
			groupKey = null;
			CloseDetails();
			ShowList();
		}

		private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
			=> ShowList();

		private void BackButton_Click(object sender, RoutedEventArgs e)
		{
			groupKey = null;
			ShowList();
		}

		private void EntryList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
		{
			if (!args.InRecycleQueue && args.Item is CollectionRowViewModel row)
				_ = row.LoadThumbnailAsync();
		}

		private void EntryList_ItemClick(object sender, ItemClickEventArgs e)
		{
			switch (e.ClickedItem)
			{
				case CollectionRowViewModel { Group: { } group }:
					groupKey = group.Key;
					ShowList();
					break;
				case CollectionRowViewModel { Item: { } item }:
					ShowDetails(item);
					break;
			}
		}

		private async void EntryList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
		{
			if ((e.OriginalSource as FrameworkElement)?.DataContext is CollectionRowViewModel { Item: { } item } && appInstance is not null)
				await NavigationHelpers.OpenPath(item.Path, appInstance, FilesystemItemType.File);
		}

		private void MarkExtraCopies_Click(object sender, RoutedEventArgs e)
		{
			if ((sender as FrameworkElement)?.DataContext is not CollectionRowViewModel { Duplicates: { } group })
				return;

			// The first copy of a group is the one suggested to keep
			foreach (var row in rows.Where(row => ReferenceEquals(row.Duplicates, group)))
				row.IsChecked = !ReferenceEquals(row.Item, group.Items[0]);
		}

		private async void DeleteButton_Click(object sender, RoutedEventArgs e)
		{
			var marked = rows.Where(row => row.IsChecked && row.Item is not null).ToList();
			if (marked.Count == 0 || appInstance is null || collection is null)
				return;

			// Deleting duplicates must never delete the item itself
			foreach (var group in marked.Select(row => row.Duplicates).OfType<DuplicateGroup>().Distinct())
			{
				if (group.Items.All(item => marked.Any(row => ReferenceEquals(row.Item, item))))
				{
					ShowStatus(InfoBarSeverity.Warning, string.Format(Strings.ConsystoLibraryKeepOneCopy.GetLocalizedResource(), group.Title), null);
					return;
				}
			}

			StatusBar.IsOpen = false;
			try
			{
				var items = marked.Select(row => StorageHelpers.FromPathAndType(row.Item!.Path, FilesystemItemType.File)).ToList();
				await appInstance.FilesystemHelpers.DeleteItemsAsync(items, DeleteConfirmationPolicies.Always, permanently: false, registerHistory: true);
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Duplicate files could not be deleted");
				ShowStatus(InfoBarSeverity.Error, Strings.ConsystoLibraryDeleteFailed.GetLocalizedResource(), ex.Message);
			}

			// The folder watcher would notice as well, a few seconds later
			await CollectionManager.Instance.RescanAsync(collection.Id);
		}

		private async void SettingsButton_Click(object sender, RoutedEventArgs e)
			=> await Commands.OpenSettings.ExecuteAsync(new SettingsNavigationParams() { PageKind = SettingsPageKind.CollectionsPage });

		private void ShowDetails(CollectionItem item)
		{
			detailsItem = item;
			DetailsTitle.Text = item.Title;

			var byline = kind?.Byline(item);
			DetailsAuthors.Text = byline ?? string.Empty;
			DetailsAuthors.Visibility = byline is null ? Visibility.Collapsed : Visibility.Visible;

			DetailsMeta.Text = string.Join(" · ", (kind?.Details(item) ?? []).Where(part => !string.IsNullOrWhiteSpace(part)));

			var description = kind?.Description(item);
			DetailsSummary.Text = description ?? string.Empty;
			DetailsSummary.Visibility = description is null ? Visibility.Collapsed : Visibility.Visible;
			DetailsPath.Text = item.Path;

			DetailsCover.Source = null;
			_ = LoadDetailsCoverAsync(item);
			DetailsPanel.Visibility = Visibility.Visible;
		}

		private async Task LoadDetailsCoverAsync(CollectionItem item)
		{
			if (!item.HasCover || kind?.Thumbnail is not { } thumbnailOf)
				return;

			try
			{
				if (await thumbnailOf(item, DetailsCoverSize) is { } image && ReferenceEquals(detailsItem, item))
					DetailsCover.Source = await image.ToBitmapAsync();
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "A collection picture could not be shown");
			}
		}

		private void CloseDetails()
		{
			detailsItem = null;
			DetailsPanel.Visibility = Visibility.Collapsed;
			DetailsCover.Source = null;
		}

		private void CloseDetails_Click(object sender, RoutedEventArgs e)
			=> CloseDetails();

		private async void OpenButton_Click(object sender, RoutedEventArgs e)
		{
			if (detailsItem is not null && appInstance is not null)
				await NavigationHelpers.OpenPath(detailsItem.Path, appInstance, FilesystemItemType.File);
		}

		private void RevealButton_Click(object sender, RoutedEventArgs e)
		{
			if (detailsItem is null || appInstance is null || SystemIO.Path.GetDirectoryName(detailsItem.Path) is not { } folder)
				return;

			appInstance.NavigateToPath(folder, new NavigationArguments() { SelectItems = [detailsItem.FileName] });
		}

		private void ShowStatus(InfoBarSeverity severity, string title, string? message)
		{
			StatusBar.Severity = severity;
			StatusBar.Title = title;
			StatusBar.Message = message ?? string.Empty;
			StatusBar.IsOpen = true;
		}

		// In a narrow pane the search moves under the title and the details cover the list instead of squeezing it
		private void HeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			var narrow = e.NewSize.Width < 640;
			Grid.SetRow(HeaderTools, narrow ? 1 : 0);
			Grid.SetColumn(HeaderTools, narrow ? 0 : 1);
			Grid.SetColumnSpan(HeaderTools, narrow ? 2 : 1);
			HeaderTools.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
			SearchBox.Width = narrow ? double.NaN : 300;
		}

		private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			var narrow = e.NewSize.Width < 900;
			SectionList.Width = e.NewSize.Width < 640 ? 160 : 210;
			Grid.SetColumn(DetailsPanel, narrow ? 1 : 2);
			Grid.SetColumnSpan(DetailsPanel, narrow ? 2 : 1);
			DetailsPanel.HorizontalAlignment = narrow ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
			DetailsPanel.Width = narrow ? Math.Max(240, Math.Min(360, e.NewSize.Width - SectionList.Width - 40)) : 360;
			DetailsPanel.Background = narrow ? (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"] : null;
		}
	}
}
