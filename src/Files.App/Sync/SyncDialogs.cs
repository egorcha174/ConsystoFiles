// Consysto fork: adding and editing a backup pair.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;

namespace Files.App.Sync
{
	internal static class SyncDialogs
	{
		public static Task<SyncPair?> AddAsync()
			=> EditAsync(null);

		/// <summary>Name, source, target and exclusions; the pair is saved when the dialog is confirmed.</summary>
		public static async Task<SyncPair?> EditAsync(SyncPair? pair)
		{
			var name = new TextBox { Header = Strings.ConsystoSyncName.GetLocalizedResource(), Text = pair?.Name ?? string.Empty };
			var (sourcePanel, source) = FolderField(Strings.ConsystoSyncSource.GetLocalizedResource(), pair?.Source);
			var (targetPanel, target) = FolderField(Strings.ConsystoSyncTarget.GetLocalizedResource(), pair?.Target);
			var exclusions = new TextBox
			{
				Header = Strings.ConsystoSyncExclusions.GetLocalizedResource(),
				Text = string.Join("; ", pair?.Exclusions ?? [.. SyncManager.DefaultExclusions]),
			};
			var note = new TextBlock
			{
				Text = Strings.ConsystoSyncBackupNote.GetLocalizedResource(),
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
				Title = pair is null ? Strings.ConsystoSyncAddPair.GetLocalizedResource().TrimEnd('…') : Strings.ConsystoSyncEdit.GetLocalizedResource(),
				Content = new StackPanel { Spacing = 12, MinWidth = 460, Children = { name, sourcePanel, targetPanel, exclusions, note, error } },
				PrimaryButtonText = Strings.ConsystoOpdsSave.GetLocalizedResource(),
				CloseButtonText = Strings.Cancel.GetLocalizedResource(),
				DefaultButton = ContentDialogButton.Primary,
			};

			// Wrong folders keep the dialog open with the reason, so what was typed is not lost
			dialog.PrimaryButtonClick += (_, args) =>
			{
				var problem = Validate(source.Text, target.Text);
				if (problem is null)
					return;

				args.Cancel = true;
				error.Text = problem;
				error.Visibility = Visibility.Visible;
			};

			if (await dialog.TryShowAsync() != ContentDialogResult.Primary)
				return null;

			var sourcePath = SystemIO.Path.GetFullPath(source.Text.Trim());
			var targetPath = SystemIO.Path.GetFullPath(target.Text.Trim());
			var title = string.IsNullOrWhiteSpace(name.Text) ? $"{SystemIO.Path.GetFileName(sourcePath.TrimEnd('\\'))} → {targetPath}" : name.Text.Trim();
			var excluded = exclusions.Text.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

			if (pair is null)
				return SyncManager.Instance.Add(title, sourcePath, targetPath, excluded);

			pair.Name = title;
			pair.Source = sourcePath;
			pair.Target = targetPath;
			pair.Exclusions = [.. excluded];
			SyncManager.Instance.Update(pair);
			return pair;
		}

		private static string? Validate(string source, string target)
		{
			if (string.IsNullOrWhiteSpace(source) || !SystemIO.Directory.Exists(source.Trim()))
				return Strings.ConsystoSyncSourceMissing.GetLocalizedResource();
			if (string.IsNullOrWhiteSpace(target) || !SystemIO.Path.IsPathRooted(target.Trim()))
				return Strings.ConsystoSyncTargetMissing.GetLocalizedResource();

			var from = SystemIO.Path.GetFullPath(source.Trim()).TrimEnd('\\') + '\\';
			var to = SystemIO.Path.GetFullPath(target.Trim()).TrimEnd('\\') + '\\';

			// A backup inside its own source would copy itself over and over
			if (to.StartsWith(from, StringComparison.OrdinalIgnoreCase) || from.StartsWith(to, StringComparison.OrdinalIgnoreCase))
				return Strings.ConsystoSyncNested.GetLocalizedResource();

			return null;
		}

		private static (FrameworkElement Panel, TextBox Box) FolderField(string header, string? value)
		{
			var box = new TextBox { Header = header, Text = value ?? string.Empty };
			var browse = new Button { Content = Strings.ConsystoSyncBrowse.GetLocalizedResource(), VerticalAlignment = VerticalAlignment.Bottom };
			browse.Click += async (_, _) =>
			{
				var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
				picker.FileTypeFilter.Add("*");
				WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.Instance.WindowHandle);
				if (await picker.PickSingleFolderAsync() is { } folder)
					box.Text = folder.Path;
			};

			var grid = new Grid { ColumnSpacing = 8 };
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			Grid.SetColumn(browse, 1);
			grid.Children.Add(box);
			grid.Children.Add(browse);
			return (grid, box);
		}
	}
}
