// Consysto fork: a page of an OPDS catalog — browsing, search, book details and downloads.

using System.Collections.ObjectModel;
using Consysto.BookPreview.Opds;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Files.App.Books.Opds
{
	public sealed partial class OpdsPage : Page
	{
		private readonly ObservableCollection<OpdsEntryViewModel> entries = [];
		private IShellPage? appInstance;
		private string catalogPath = string.Empty;
		private OpdsCatalog? catalog;
		private OpdsFeed? feed;
		private Uri? pageAddress;
		private CancellationTokenSource? loading;
		private string? revealPath;

		public OpdsPage()
		{
			InitializeComponent();
			EntryList.ItemsSource = entries;
			SearchBox.PlaceholderText = Strings.ConsystoOpdsSearchPlaceholder.GetLocalizedResource();
			LoadMoreButton.Content = Strings.ConsystoOpdsLoadMore.GetLocalizedResource();
			RevealButton.Content = Strings.ConsystoOpdsShowInFolder.GetLocalizedResource();
			DetailsRelatedHeader.Text = Strings.ConsystoOpdsMoreInCatalog.GetLocalizedResource();
		}

		protected override async void OnNavigatedTo(NavigationEventArgs e)
		{
			base.OnNavigatedTo(e);
			if (e.Parameter is not NavigationArguments arguments)
				return;

			appInstance = arguments.AssociatedTabInstance;
			catalogPath = arguments.NavPathParam ?? string.Empty;
			catalog = OpdsCatalogManager.Instance.Find(catalogPath);
			pageAddress = Uri.TryCreate(arguments.ConsystoPageAddress, UriKind.Absolute, out var page)
				? page
				: catalog is not null && Uri.TryCreate(catalog.Address, UriKind.Absolute, out var start) ? start : null;

			await UpdateShellAsync();

			if (catalog is null || pageAddress is null)
			{
				PageTitle.Text = Strings.ConsystoOpdsCatalogs.GetLocalizedResource();
				ShowStatus(InfoBarSeverity.Warning, Strings.ConsystoOpdsNotFound.GetLocalizedResource(), null);
				return;
			}

			PageTitle.Text = catalog.Title;
			await LoadAsync(pageAddress, append: false, refresh: false);
		}

		protected override void OnNavigatedFrom(NavigationEventArgs e)
		{
			loading?.Cancel();
			base.OnNavigatedFrom(e);
		}

		/// <summary>Refresh from the toolbar or Ctrl+R: the page is read from the catalog again, bypassing the cache.</summary>
		public Task ReloadAsync()
			=> catalog is not null && pageAddress is not null ? LoadAsync(pageAddress, append: false, refresh: true) : Task.CompletedTask;

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
			await shellViewModel.SetWorkingDirectoryAsync(catalogPath);
			shellViewModel.CheckForBackgroundImage();
			shell.SlimContentPage?.StatusBarViewModel.UpdateGitInfo(false, string.Empty, null);

			var title = catalog?.Title ?? Strings.ConsystoOpdsCatalogs.GetLocalizedResource();
			shell.ToolbarViewModel.PathComponents.Clear();
			shell.ToolbarViewModel.PathComponents.Add(new PathBoxItem()
			{
				Title = title,
				Path = catalogPath,
				ChevronToolTip = string.Format(Strings.BreadcrumbBarChevronButtonToolTip.GetLocalizedResource(), title),
			});
		}

		private async Task LoadAsync(Uri address, bool append, bool refresh)
		{
			if (catalog is null)
				return;

			loading?.Cancel();
			var cancellation = new CancellationTokenSource();
			loading = cancellation;
			SetBusy(true, append);

			try
			{
				var page = await OpdsCatalogManager.Instance.GetFeedAsync(catalog, address, refresh, cancellation.Token);
				if (cancellation.IsCancellationRequested)
					return;

				if (!append)
				{
					entries.Clear();
					CloseDetails();
					StatusBar.IsOpen = false;
					PageTitle.Text = page.Title ?? catalog.Title;
					PageSubtitle.Text = page.Title is not null && page.Title != catalog.Title ? catalog.Title : page.Subtitle ?? string.Empty;
					SearchBox.Visibility = OpdsCatalogManager.Instance.HasSearch(catalog, page) ? Visibility.Visible : Visibility.Collapsed;
				}

				feed = page;
				var previousGroup = entries.LastOrDefault()?.Entry.Group;
				foreach (var entry in page.Entries)
				{
					var model = new OpdsEntryViewModel(entry, entry.Group is not null && entry.Group != previousGroup);
					previousGroup = entry.Group;
					entries.Add(model);
					_ = model.LoadThumbnailAsync();
				}

				LoadMoreButton.Visibility = page.NextPage is null ? Visibility.Collapsed : Visibility.Visible;
				EmptyText.Text = Strings.ConsystoOpdsEmpty.GetLocalizedResource();
				EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

				// A page that is just one book (the full record behind a search result) opens straight into its details
				if (!append && page.Entries.Count > 0 && page.Entries.All(entry => entry.IsPublication) && page.Entries.Count <= 2)
					ShowDetails(entries[0]);
			}
			catch (OpdsAuthenticationRequiredException) when (!cancellation.IsCancellationRequested)
			{
				SetBusy(false, append);
				if (await OpdsCatalogDialogs.SignInAsync(catalog))
					await LoadAsync(address, append, refresh: true);
				else
					ShowStatus(InfoBarSeverity.Warning, Strings.ConsystoOpdsSignInRequired.GetLocalizedResource(), null);
			}
			catch (Exception ex) when (!cancellation.IsCancellationRequested)
			{
				App.Logger.LogWarning(ex, "An OPDS catalog page could not be loaded");
				ShowStatus(InfoBarSeverity.Error, Strings.ConsystoOpdsLoadFailed.GetLocalizedResource(), ex.Message);
			}
			finally
			{
				if (loading == cancellation)
					SetBusy(false, append);
			}
		}

		// In a narrow pane (two panes side by side) the search box moves under the title instead of squeezing it
		private void HeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			var narrow = e.NewSize.Width < 640;
			Grid.SetRow(SearchBox, narrow ? 1 : 0);
			Grid.SetColumn(SearchBox, narrow ? 0 : 1);
			Grid.SetColumnSpan(SearchBox, narrow ? 2 : 1);
			SearchBox.Width = narrow ? double.NaN : 300;
			SearchBox.HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
		}

		private void SetBusy(bool isBusy, bool append)
		{
			LoadingRing.IsActive = isBusy && !append;
			LoadMoreButton.IsEnabled = !isBusy;
			if (isBusy && !append)
				EmptyText.Visibility = Visibility.Collapsed;
		}

		private void NavigateWithinCatalog(Uri address)
		{
			if (appInstance is not null)
				appInstance.NavigateToConsystoPage(catalogPath, address.AbsoluteUri);
			else
				_ = LoadAsync(address, append: false, refresh: false);
		}

		private void EntryList_ItemClick(object sender, ItemClickEventArgs e)
		{
			if (e.ClickedItem is not OpdsEntryViewModel model)
				return;

			if (model.Entry.IsPublication)
				ShowDetails(model);
			else if (model.Entry.Navigation is { } target)
				NavigateWithinCatalog(target);
		}

		private void LoadMoreButton_Click(object sender, RoutedEventArgs e)
		{
			if (feed?.NextPage is { } next)
				_ = LoadAsync(next, append: true, refresh: false);
		}

		private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
		{
			var terms = args.QueryText?.Trim();
			if (string.IsNullOrEmpty(terms) || catalog is null)
				return;

			try
			{
				var template = await OpdsCatalogManager.Instance.GetSearchTemplateAsync(catalog, feed, CancellationToken.None);
				if (template is not null)
					NavigateWithinCatalog(OpdsClient.BuildSearchAddress(template, terms));
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "The OPDS search could not be started");
				ShowStatus(InfoBarSeverity.Error, Strings.ConsystoOpdsLoadFailed.GetLocalizedResource(), ex.Message);
			}
		}

		private void ShowDetails(OpdsEntryViewModel model)
		{
			var entry = model.Entry;
			DetailsTitle.Text = model.Title;
			DetailsAuthors.Text = string.Join(", ", entry.Authors);
			DetailsAuthors.Visibility = entry.Authors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

			var series = entry.Series is null ? null : entry.SeriesIndex is null ? entry.Series : string.Format(Strings.ConsystoBookSeriesNumber.GetLocalizedResource(), entry.Series, entry.SeriesIndex);
			var meta = new[] { series, entry.Year, entry.Language, entry.Publisher, string.Join(", ", entry.Categories.Take(3)) }.Where(part => !string.IsNullOrWhiteSpace(part));
			DetailsMeta.Text = string.Join(" · ", meta);
			DetailsMeta.Visibility = string.IsNullOrEmpty(DetailsMeta.Text) ? Visibility.Collapsed : Visibility.Visible;

			DetailsSummary.Text = entry.Summary ?? string.Empty;
			DetailsSummary.Visibility = entry.Summary is null ? Visibility.Collapsed : Visibility.Visible;

			DetailsDownloads.Children.Clear();
			foreach (var acquisition in model.Downloads)
			{
				var size = acquisition.Length is { } length ? $" · {length.ToSizeString()}" : string.Empty;
				var button = new Button
				{
					HorizontalAlignment = HorizontalAlignment.Stretch,
					HorizontalContentAlignment = HorizontalAlignment.Left,
					Content = $"{Strings.ConsystoOpdsDownload.GetLocalizedResource()} {acquisition.Format.Name}{size}",
				};
				if (acquisition.Title is not null)
					ToolTipService.SetToolTip(button, acquisition.Title);

				if (acquisition == model.PreferredDownload)
					button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];

				button.Click += (_, _) => _ = DownloadAsync(entry, acquisition);
				DetailsDownloads.Children.Add(button);
			}

			foreach (var acquisition in entry.Acquisitions.Where(acquisition => !acquisition.IsDirectDownload))
				DetailsDownloads.Children.Add(new HyperlinkButton { Content = Strings.ConsystoOpdsOnSite.GetLocalizedResource(), NavigateUri = acquisition.Address });

			DetailsRelated.Children.Clear();
			foreach (var related in entry.Related.Take(12))
			{
				var link = new HyperlinkButton { Content = related.Title, Padding = new Thickness(0, 4, 0, 4) };
				link.Click += (_, _) => NavigateWithinCatalog(related.Address);
				DetailsRelated.Children.Add(link);
			}

			DetailsRelatedHeader.Visibility = entry.Related.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

			DetailsCover.Source = null;
			_ = LoadDetailsCoverAsync(entry);
			DetailsPanel.Visibility = Visibility.Visible;
		}

		private async Task LoadDetailsCoverAsync(OpdsEntry entry)
		{
			var image = await OpdsImages.LoadAsync(entry.Image ?? entry.Thumbnail, 640);
			if (DetailsTitle.Text == (entry.Title ?? "—"))
				DetailsCover.Source = image;
		}

		private void CloseDetails()
		{
			DetailsPanel.Visibility = Visibility.Collapsed;
			DetailsCover.Source = null;
		}

		private void CloseDetails_Click(object sender, RoutedEventArgs e)
			=> CloseDetails();

		private void DownloadButton_Click(SplitButton sender, SplitButtonClickEventArgs args)
		{
			if (sender.DataContext is OpdsEntryViewModel { PreferredDownload: { } acquisition } model)
				_ = DownloadAsync(model.Entry, acquisition);
		}

		private void DownloadFlyout_Opening(object? sender, object e)
		{
			if (sender is not MenuFlyout flyout || flyout.Target?.DataContext is not OpdsEntryViewModel model)
				return;

			flyout.Items.Clear();
			foreach (var acquisition in model.Downloads)
			{
				var size = acquisition.Length is { } length ? $" · {length.ToSizeString()}" : string.Empty;
				var item = new MenuFlyoutItem { Text = $"{acquisition.Format.Name}{size}" + (acquisition.Title is null ? string.Empty : $" — {acquisition.Title}") };
				item.Click += (_, _) => _ = DownloadAsync(model.Entry, acquisition);
				flyout.Items.Add(item);
			}
		}

		private async Task DownloadAsync(OpdsEntry entry, OpdsAcquisition acquisition)
		{
			if (catalog is null)
				return;

			// Every download asks where to go, so books land in the folder the reader keeps them in
			var picker = new FolderPicker
			{
				SuggestedStartLocation = PickerLocationId.Downloads,
				SettingsIdentifier = "ConsystoOpdsDownloads",
			};
			picker.FileTypeFilter.Add("*");
			WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);

			var folder = await picker.PickSingleFolderAsync();
			if (folder is null)
				return;

			var title = entry.Title ?? acquisition.Address.Segments.LastOrDefault() ?? "book";
			var baseName = entry.Authors.Count > 0 ? $"{string.Join(", ", entry.Authors.Take(2))} - {title}" : title;
			ShowStatus(InfoBarSeverity.Informational, Strings.ConsystoOpdsDownloading.GetLocalizedResource(), $"{title} ({acquisition.Format.Name})", showProgress: true);

			try
			{
				var progress = new Progress<double>(value =>
				{
					DownloadProgress.IsIndeterminate = false;
					DownloadProgress.Value = value;
				});
				var path = await OpdsCatalogManager.Instance.DownloadAsync(catalog, acquisition, folder.Path, baseName, progress, CancellationToken.None);
				ShowStatus(InfoBarSeverity.Success, Strings.ConsystoOpdsDownloaded.GetLocalizedResource(), SystemIO.Path.GetFileName(path), revealPath: path);
			}
			catch (OpdsAuthenticationRequiredException)
			{
				StatusBar.IsOpen = false;
				if (await OpdsCatalogDialogs.SignInAsync(catalog))
					await DownloadRetryAsync(entry, acquisition, folder.Path, baseName);
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "A book could not be downloaded from an OPDS catalog");
				ShowStatus(InfoBarSeverity.Error, Strings.ConsystoOpdsDownloadFailed.GetLocalizedResource(), ex.Message);
			}
		}

		private async Task DownloadRetryAsync(OpdsEntry entry, OpdsAcquisition acquisition, string directory, string baseName)
		{
			if (catalog is null)
				return;

			ShowStatus(InfoBarSeverity.Informational, Strings.ConsystoOpdsDownloading.GetLocalizedResource(), entry.Title, showProgress: true);
			try
			{
				var progress = new Progress<double>(value =>
				{
					DownloadProgress.IsIndeterminate = false;
					DownloadProgress.Value = value;
				});
				var path = await OpdsCatalogManager.Instance.DownloadAsync(catalog, acquisition, directory, baseName, progress, CancellationToken.None);
				ShowStatus(InfoBarSeverity.Success, Strings.ConsystoOpdsDownloaded.GetLocalizedResource(), SystemIO.Path.GetFileName(path), revealPath: path);
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "A book could not be downloaded from an OPDS catalog");
				ShowStatus(InfoBarSeverity.Error, Strings.ConsystoOpdsDownloadFailed.GetLocalizedResource(), ex.Message);
			}
		}

		private void ShowStatus(InfoBarSeverity severity, string title, string? message, bool showProgress = false, string? revealPath = null)
		{
			StatusBar.Severity = severity;
			StatusBar.Title = title;
			StatusBar.Message = message ?? string.Empty;
			StatusBar.IsClosable = !showProgress;
			DownloadProgress.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
			DownloadProgress.IsIndeterminate = showProgress;
			DownloadProgress.Value = 0;
			this.revealPath = revealPath;
			RevealButton.Visibility = revealPath is null ? Visibility.Collapsed : Visibility.Visible;
			StatusBar.IsOpen = true;
		}

		private void RevealButton_Click(object sender, RoutedEventArgs e)
		{
			if (revealPath is null || appInstance is null || SystemIO.Path.GetDirectoryName(revealPath) is not { } folder)
				return;

			appInstance.NavigateToPath(folder, new NavigationArguments() { SelectItems = [SystemIO.Path.GetFileName(revealPath)] });
		}
	}
}
