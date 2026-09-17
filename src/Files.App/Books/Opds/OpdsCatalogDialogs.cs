// Consysto fork: dialogs for adding, editing and removing OPDS catalogs and for signing in to one.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Files.App.Books.Opds
{
	internal static class OpdsCatalogDialogs
	{
		public static Task<OpdsCatalog?> AddAsync()
			=> EditAsync(null);

		public static async Task<OpdsCatalog?> EditAsync(OpdsCatalog? catalog)
		{
			var title = new TextBox
			{
				Header = Strings.ConsystoOpdsCatalogName.GetLocalizedResource(),
				Text = catalog?.Title ?? string.Empty,
			};
			var address = new TextBox
			{
				Header = Strings.ConsystoOpdsCatalogAddress.GetLocalizedResource(),
				Text = catalog?.Address ?? string.Empty,
				PlaceholderText = "https://example.org/opds",
				InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Url) } },
			};
			var userName = new TextBox
			{
				Header = Strings.ConsystoOpdsUserName.GetLocalizedResource(),
				Text = catalog?.UserName ?? string.Empty,
			};
			var password = new PasswordBox
			{
				Header = Strings.ConsystoOpdsPassword.GetLocalizedResource(),
				PlaceholderText = catalog?.UserName is null ? string.Empty : Strings.ConsystoOpdsPasswordKeep.GetLocalizedResource(),
			};
			var note = new TextBlock
			{
				Text = Strings.ConsystoOpdsPasswordNote.GetLocalizedResource(),
				Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
				Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
				TextWrapping = TextWrapping.Wrap,
			};
			var error = new TextBlock
			{
				Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
				TextWrapping = TextWrapping.Wrap,
				Visibility = Visibility.Collapsed,
			};

			var dialog = new ContentDialog
			{
				Title = catalog is null ? Strings.ConsystoOpdsAddCatalogTitle.GetLocalizedResource() : Strings.ConsystoOpdsEditCatalog.GetLocalizedResource().TrimEnd('…', '.'),
				Content = new StackPanel { Spacing = 12, MinWidth = 400, Children = { title, address, userName, password, note, error } },
				PrimaryButtonText = Strings.ConsystoOpdsSave.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DefaultButton = ContentDialogButton.Primary,
			};

			dialog.PrimaryButtonClick += (_, args) =>
			{
				if (!Uri.TryCreate(address.Text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
				{
					error.Text = Strings.ConsystoOpdsInvalidAddress.GetLocalizedResource();
					error.Visibility = Visibility.Visible;
					args.Cancel = true;
				}
			};

			if (await dialog.TryShowAsync() != ContentDialogResult.Primary)
				return null;

			var catalogAddress = address.Text.Trim();
			var name = string.IsNullOrWhiteSpace(title.Text) ? new Uri(catalogAddress).Host : title.Text.Trim();
			var manager = OpdsCatalogManager.Instance;

			if (catalog is null)
				return manager.Add(name, catalogAddress, userName.Text, password.Password);

			manager.Update(catalog, name, catalogAddress, userName.Text, password.Password.Length > 0 ? password.Password : null);
			return catalog;
		}

		public static async Task<bool> RemoveAsync(OpdsCatalog catalog)
		{
			var dialog = new ContentDialog
			{
				Title = Strings.ConsystoOpdsRemoveCatalog.GetLocalizedResource(),
				Content = string.Format(Strings.ConsystoOpdsRemoveConfirm.GetLocalizedResource(), catalog.Title),
				PrimaryButtonText = Strings.ConsystoOpdsRemoveCatalog.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DefaultButton = ContentDialogButton.Close,
			};

			if (await dialog.TryShowAsync() != ContentDialogResult.Primary)
				return false;

			OpdsCatalogManager.Instance.Remove(catalog);
			return true;
		}

		public static async Task<bool> SignInAsync(OpdsCatalog catalog)
		{
			var userName = new TextBox
			{
				Header = Strings.ConsystoOpdsUserName.GetLocalizedResource(),
				Text = catalog.UserName ?? string.Empty,
			};
			var password = new PasswordBox { Header = Strings.ConsystoOpdsPassword.GetLocalizedResource() };
			var host = Uri.TryCreate(catalog.Address, UriKind.Absolute, out var uri) ? uri.Host : catalog.Title;

			var dialog = new ContentDialog
			{
				Title = Strings.ConsystoOpdsSignInTitle.GetLocalizedResource(),
				Content = new StackPanel
				{
					Spacing = 12,
					MinWidth = 360,
					Children =
					{
						new TextBlock { Text = string.Format(Strings.ConsystoOpdsSignInText.GetLocalizedResource(), host), TextWrapping = TextWrapping.Wrap },
						userName,
						password,
						new TextBlock
						{
							Text = Strings.ConsystoOpdsPasswordNote.GetLocalizedResource(),
							Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
							Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
							TextWrapping = TextWrapping.Wrap,
						},
					},
				},
				PrimaryButtonText = Strings.ConsystoOpdsSignIn.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DefaultButton = ContentDialogButton.Primary,
			};

			if (await dialog.TryShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(userName.Text))
				return false;

			OpdsCatalogManager.Instance.SignIn(catalog, userName.Text, password.Password);
			return true;
		}
	}
}
