// Consysto fork: the dialog that creates a library of books, photos or music.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace Files.App.Books.Library
{
	internal static class CollectionDialogs
	{
		/// <summary>Name, kind and first folder of a new Windows library.</summary>
		public static async Task<CollectionSettings?> CreateLibraryAsync()
		{
			var kinds = CollectionKinds.All;
			var kindBox = new ComboBox
			{
				Header = Strings.ConsystoCollectionKind.GetLocalizedResource(),
				ItemsSource = kinds.Select(kind => kind.Name).Append(Strings.ConsystoLibraryKindFolder.GetLocalizedResource()).ToList(),
				SelectedIndex = 0,
				MinWidth = 200,
			};
			var name = new TextBox
			{
				Header = Strings.ConsystoCollectionName.GetLocalizedResource(),
				Text = kinds[0].Name,
			};

			string? folder = null;
			var folderChosen = false;
			var nameEdited = false;
			name.TextChanged += (_, _) => nameEdited = name.FocusState != FocusState.Unfocused || nameEdited;

			var folderText = new TextBlock
			{
				Text = Strings.ConsystoCollectionNoFolder.GetLocalizedResource(),
				Foreground = Brush("TextFillColorSecondaryBrush"),
				VerticalAlignment = VerticalAlignment.Center,
				TextTrimming = TextTrimming.CharacterEllipsis,
			};
			var choose = new Button { Content = Strings.ConsystoCollectionChooseFolder.GetLocalizedResource() };
			var folderRow = new Grid { ColumnSpacing = 8 };
			folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			Grid.SetColumn(choose, 1);
			folderRow.Children.Add(folderText);
			folderRow.Children.Add(choose);

			var error = new TextBlock
			{
				Foreground = Brush("SystemFillColorCriticalBrush"),
				TextWrapping = TextWrapping.Wrap,
				Visibility = Visibility.Collapsed,
			};

			CollectionKindInfo? SelectedKind()
				=> kindBox.SelectedIndex >= 0 && kindBox.SelectedIndex < kinds.Count ? kinds[kindBox.SelectedIndex] : null;

			void ShowFolder(string? path)
			{
				folder = path;
				folderText.Text = path ?? Strings.ConsystoCollectionNoFolder.GetLocalizedResource();
				folderText.Foreground = Brush(path is null ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
				ToolTipService.SetToolTip(folderText, path);
			}

			// Until the user picks a folder or types a name, the kind suggests both: "Photos" in Pictures, "Books" in Documents
			void OfferDefaults()
			{
				var kind = SelectedKind();
				if (!nameEdited)
					name.Text = kind?.Name ?? string.Empty;

				if (folderChosen)
					return;

				var suggested = kind?.SuggestedFolder is { } special ? Environment.GetFolderPath(special) : null;
				ShowFolder(!string.IsNullOrEmpty(suggested) && SystemIO.Directory.Exists(suggested) ? suggested : null);
			}

			kindBox.SelectionChanged += (_, _) => OfferDefaults();
			OfferDefaults();

			choose.Click += async (_, _) =>
			{
				if (await PickFolderAsync() is not { } picked)
					return;

				folderChosen = true;
				ShowFolder(picked);
				error.Visibility = Visibility.Collapsed;
			};

			var dialog = new ContentDialog
			{
				Title = Strings.ConsystoLibraryCreateTitle.GetLocalizedResource(),
				Content = new StackPanel
				{
					Spacing = 12,
					MinWidth = 400,
					Children =
					{
						kindBox,
						name,
						new StackPanel
						{
							Spacing = 4,
							Children = { new TextBlock { Text = Strings.ConsystoCollectionFolder.GetLocalizedResource() }, folderRow },
						},
						error,
					},
				},
				PrimaryButtonText = Strings.ConsystoCollectionAddButton.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DefaultButton = ContentDialogButton.Primary,
			};

			dialog.PrimaryButtonClick += (_, args) =>
			{
				var (canCreate, reason) = App.LibraryManager.CanCreateLibrary(name.Text.Trim());
				var message = folder is null ? Strings.ConsystoCollectionFolderRequired.GetLocalizedResource() : canCreate ? null : reason;
				if (message is null)
					return;

				error.Text = message;
				error.Visibility = Visibility.Visible;
				args.Cancel = true;
			};

			if (await dialog.TryShowAsync() != ContentDialogResult.Primary || folder is null)
				return null;

			return await CollectionManager.Instance.CreateLibraryAsync(name.Text.Trim(), SelectedKind()?.Id, folder);
		}

		public static async Task<string?> PickFolderAsync()
		{
			var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
			picker.FileTypeFilter.Add("*");
			WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
			return (await picker.PickSingleFolderAsync())?.Path;
		}

		private static Brush Brush(string key)
			=> (Brush)Application.Current.Resources[key];
	}
}
