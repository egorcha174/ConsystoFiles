// Consysto fork: settings of the libraries — their kinds and folders, and sharing a books library with a phone. Changes apply at once.

using System.Collections.Specialized;
using CommunityToolkit.WinUI.Controls;
using Files.App.Books.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Files.App.Views.Settings
{
	public sealed partial class CollectionsPage : Page
	{
		private sealed record LibraryView(string LibraryPath, SettingsExpander Expander, Button? RescanButton, ToggleSwitch? SharingToggle, TextBlock? Address, NumberBox? Port);

		private readonly List<LibraryView> views = [];
		private readonly HashSet<string> expandedPaths = new(StringComparer.OrdinalIgnoreCase);
		private string shownStructure = string.Empty;
		private bool isRefreshing;

		public CollectionsPage()
		{
			// The settings search builds this page only to read its headers, so nothing is subscribed before it is shown
			InitializeComponent();
			Loaded += CollectionsPage_Loaded;
			Unloaded += CollectionsPage_Unloaded;
		}

		private static CollectionManager Manager
			=> CollectionManager.Instance;

		private void CollectionsPage_Loaded(object sender, RoutedEventArgs e)
		{
			Manager.StateChanged += Manager_StateChanged;
			App.LibraryManager.DataChanged += LibraryManager_DataChanged;
			Refresh();
		}

		private void CollectionsPage_Unloaded(object sender, RoutedEventArgs e)
		{
			Manager.StateChanged -= Manager_StateChanged;
			App.LibraryManager.DataChanged -= LibraryManager_DataChanged;
		}

		private void Manager_StateChanged(object? sender, EventArgs e)
			=> DispatcherQueue.TryEnqueue(Refresh);

		private void LibraryManager_DataChanged(object? sender, NotifyCollectionChangedEventArgs e)
			=> DispatcherQueue.TryEnqueue(Refresh);

		private async void CreateLibraryButton_Click(object sender, RoutedEventArgs e)
			=> await CollectionDialogs.CreateLibraryAsync();

		private void Refresh()
		{
			isRefreshing = true;
			try
			{
				// Rebuilt only when libraries, their folders or kinds change, so a scan does not reset what is being edited
				var libraries = App.LibraryManager.Libraries.Where(library => library.Path is not null).ToArray();
				var structure = string.Join("|", libraries.Select(library => $"{library.Path}:{library.Text}:{Manager.KindOfLibrary(library.Path)}:{string.Join(";", library.Folders)}"));
				if (structure != shownStructure)
				{
					shownStructure = structure;
					Rebuild(libraries);
				}

				foreach (var view in views)
					RefreshView(view);
			}
			finally
			{
				isRefreshing = false;
			}
		}

		private void RefreshView(LibraryView view)
		{
			var collection = Manager.FindByLibrary(view.LibraryPath);
			var kind = CollectionKinds.Find(collection?.KindId);
			if (collection is null || kind is null)
			{
				view.Expander.Description = Strings.ConsystoLibraryKindFolder.GetLocalizedResource();
				return;
			}

			var scanning = Manager.IsScanning(collection.Id);
			var status = scanning
				? Strings.ConsystoCollectionScanning.GetLocalizedResource()
				: string.Format(kind.CountFormat, Manager.SnapshotOf(collection.Id).Items.Count);
			view.Expander.Description = $"{kind.Name} · {status}";
			if (view.RescanButton is not null)
				view.RescanButton.IsEnabled = !scanning;

			if (view.SharingToggle is null || view.Address is null || view.Port is null)
				return;

			var isShared = Manager.SharedCollectionId == collection.Id;
			view.SharingToggle.IsOn = isShared;
			if (view.Port.FocusState == FocusState.Unfocused)
				view.Port.Value = Manager.Port;

			if (!isShared)
			{
				view.Address.Text = Strings.ConsystoBookServerOff.GetLocalizedResource();
				view.Address.Foreground = Brush("TextFillColorSecondaryBrush");
			}
			else if (Manager.SharingError is { } error)
			{
				view.Address.Text = error;
				view.Address.Foreground = Brush("SystemFillColorCriticalBrush");
			}
			else
			{
				var addresses = Manager.Addresses;
				view.Address.Text = addresses.Count > 0 ? string.Join("\n", addresses) : Strings.ConsystoBookServerNoNetwork.GetLocalizedResource();
				view.Address.Foreground = Brush("TextFillColorPrimaryBrush");
			}
		}

		private void Rebuild(IReadOnlyList<LibraryLocationItem> libraries)
		{
			foreach (var old in views)
			{
				if (old.Expander.IsExpanded)
					expandedPaths.Add(old.LibraryPath);
				else
					expandedPaths.Remove(old.LibraryPath);
			}

			views.Clear();
			CollectionList.Children.Clear();
			NoCollectionsText.Visibility = libraries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

			var kinds = CollectionKinds.All;
			foreach (var library in libraries)
			{
				var libraryPath = library.Path!;
				var kindId = Manager.KindOfLibrary(libraryPath);
				var kind = CollectionKinds.Find(kindId);

				var kindBox = new ComboBox
				{
					MinWidth = 160,
					ItemsSource = kinds.Select(item => item.Name).Prepend(Strings.ConsystoLibraryKindFolder.GetLocalizedResource()).ToList(),
					SelectedIndex = kind is null ? 0 : kinds.ToList().IndexOf(kind) + 1,
				};
				AutomationProperties.SetName(kindBox, Strings.ConsystoLibraryKindMenu.GetLocalizedResource());
				kindBox.SelectionChanged += (_, _) =>
				{
					if (!isRefreshing && kindBox.SelectedIndex >= 0)
						Manager.SetKind(libraryPath, kindBox.SelectedIndex == 0 ? null : kinds[kindBox.SelectedIndex - 1].Id);
				};

				var expander = new SettingsExpander
				{
					Header = library.Text,
					HeaderIcon = new FontIcon { Glyph = kind?.Glyph ?? FluentGlyphs.Library },
					IsExpanded = expandedPaths.Contains(libraryPath),
					Content = kindBox,
					ItemsHeader = BuildFolderList(library),
				};

				Button? rescan = null;
				ToggleSwitch? toggle = null;
				TextBlock? address = null;
				NumberBox? port = null;
				if (Manager.FindByLibrary(libraryPath) is { } collection)
				{
					rescan = new Button { Content = Strings.ConsystoBookServerRescan.GetLocalizedResource() };
					rescan.Click += (_, _) => _ = Manager.RescanAsync(collection.Id);
					expander.Items.Add(new SettingsCard { Header = Strings.ConsystoLibraryIndex.GetLocalizedResource(), Content = rescan });

					if (kind?.CanShare == true)
						(toggle, address, port) = AddSharingCards(expander, collection);
				}

				views.Add(new LibraryView(libraryPath, expander, rescan, toggle, address, port));
				CollectionList.Children.Add(expander);
			}
		}

		private (ToggleSwitch Toggle, TextBlock Address, NumberBox Port) AddSharingCards(SettingsExpander expander, CollectionSettings collection)
		{
			var port = new NumberBox { Width = 140, Minimum = 1024, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
			AutomationProperties.SetName(port, Strings.ConsystoBookServerPort.GetLocalizedResource());
			port.ValueChanged += (_, args) =>
			{
				if (!isRefreshing && !double.IsNaN(args.NewValue))
					Manager.SetSharing(Manager.SharedCollectionId, (int)args.NewValue);
			};

			var toggle = new ToggleSwitch();
			AutomationProperties.SetName(toggle, Strings.ConsystoBookServerEnable.GetLocalizedResource());
			toggle.Toggled += (_, _) =>
			{
				if (isRefreshing)
					return;

				// One library is shared at a time: turning it on here moves sharing from another one
				var shared = toggle.IsOn ? collection.Id : Manager.SharedCollectionId == collection.Id ? null : Manager.SharedCollectionId;
				Manager.SetSharing(shared, double.IsNaN(port.Value) ? Manager.Port : (int)port.Value);
			};

			var address = new TextBlock
			{
				MaxWidth = 360,
				IsTextSelectionEnabled = true,
				TextAlignment = TextAlignment.Right,
				TextWrapping = TextWrapping.Wrap,
				Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
			};

			expander.Items.Add(new SettingsCard
			{
				Header = Strings.ConsystoBookServerEnable.GetLocalizedResource(),
				Description = Strings.ConsystoBookServerDescription.GetLocalizedResource(),
				HeaderIcon = new FontIcon { Glyph = FluentGlyphs.Phone },
				Content = toggle,
			});
			expander.Items.Add(new SettingsCard
			{
				Header = Strings.ConsystoBookServerAddress.GetLocalizedResource(),
				Description = Strings.ConsystoBookServerAddressHint.GetLocalizedResource(),
				Content = address,
			});
			expander.Items.Add(new SettingsCard { Header = Strings.ConsystoBookServerPort.GetLocalizedResource(), Content = port });
			return (toggle, address, port);
		}

		/// <summary>The folders of the Windows library itself: Files, Explorer and the collection page all see the same set.</summary>
		private static StackPanel BuildFolderList(LibraryLocationItem library)
		{
			var list = new StackPanel
			{
				Padding = new Thickness(58, 8, 16, 12),
				Spacing = 2,
				Background = Brush("CardBackgroundFillColorDefaultBrush"),
			};

			var folders = library.Folders.ToArray();
			foreach (var folder in folders)
			{
				var row = new Grid { ColumnSpacing = 8 };
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

				var text = new TextBlock
				{
					Text = folder,
					VerticalAlignment = VerticalAlignment.Center,
					TextTrimming = TextTrimming.CharacterEllipsis,
				};
				ToolTipService.SetToolTip(text, folder);
				row.Children.Add(text);

				// A Windows library keeps at least one folder
				if (folders.Length > 1)
				{
					var remove = new Button
					{
						Content = new FontIcon { Glyph = FluentGlyphs.Cancel, FontSize = 12 },
						Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
						BorderThickness = new Thickness(0),
					};
					ToolTipService.SetToolTip(remove, Strings.ConsystoBookServerRemoveFolder.GetLocalizedResource());
					Grid.SetColumn(remove, 1);

					var removed = folder;
					remove.Click += async (_, _) => await UpdateFoldersAsync(library, folders.Where(candidate => candidate != removed).ToArray());
					row.Children.Add(remove);
				}

				list.Children.Add(row);
			}

			var addFolder = new HyperlinkButton
			{
				Content = Strings.ConsystoBookServerAddFolder.GetLocalizedResource(),
				Margin = new Thickness(-12, 4, 0, 0),
			};
			addFolder.Click += async (_, _) =>
			{
				if (await CollectionDialogs.PickFolderAsync() is { } picked)
					await UpdateFoldersAsync(library, [.. folders, picked]);
			};
			list.Children.Add(addFolder);
			return list;
		}

		private static async Task UpdateFoldersAsync(LibraryLocationItem library, string[] folders)
		{
			var keepsSaveFolder = library.DefaultSaveFolder is { } saveFolder && folders.Contains(saveFolder, StringComparer.OrdinalIgnoreCase);
			await App.LibraryManager.UpdateLibrary(library.Path!, defaultSaveFolder: keepsSaveFolder ? null : folders[0], folders: folders);
		}

		private static Brush Brush(string key)
			=> (Brush)Application.Current.Resources[key];
	}
}
