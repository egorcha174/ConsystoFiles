using Files.App.MacStyle;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace Files.App.Actions
{
	/// <summary>
	/// Consysto fork: compares the folders open in both panes and copies the chosen differences, newer files replacing older ones.
	/// </summary>
	[GeneratedRichCommand]
	internal sealed partial class CompareFoldersAction : ObservableObject, IAction
	{
		private readonly IContentPageContext context;

		public string Label
			=> Strings.ConsystoCompareFolders.GetLocalizedResource();

		public string Description
			=> Strings.ConsystoCompareFoldersDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.DualPane;

		public RichGlyph Glyph
			=> new("\uE895");

		public bool IsExecutable
			=> context.IsMultiPaneActive;

		public CompareFoldersAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();

			context.PropertyChanged += Context_PropertyChanged;
		}

		public async Task ExecuteAsync(object? parameter = null)
		{
			if (context.ShellPage?.PaneHolder?.GetPanes().ToList() is not { Count: >= 2 } panes)
				return;

			var leftPane = panes[0];
			var rightPane = panes[1];
			var leftPath = leftPane.ShellViewModel?.WorkingDirectory;
			var rightPath = rightPane.ShellViewModel?.WorkingDirectory;
			if (!IsFolder(leftPath) || !IsFolder(rightPath))
			{
				await DialogDisplayHelper.ShowDialogAsync(Label, Strings.ConsystoCompareFoldersNeedFolders.GetLocalizedResource());
				return;
			}

			var dialog = new CompareFoldersDialog(leftPath, rightPath);
			if (await dialog.TryShowAsync() != ContentDialogResult.Primary)
				return;

			var entries = dialog.GetEntriesToSynchronize();
			await CopyAsync(leftPane, entries.Where(entry => entry.Action is FolderSyncDirection.LeftToRight), leftPath, rightPath);
			await CopyAsync(rightPane, entries.Where(entry => entry.Action is FolderSyncDirection.RightToLeft), rightPath, leftPath);

			await leftPane.RefreshIfNoWatcherExistsAsync();
			await rightPane.RefreshIfNoWatcherExistsAsync();
		}

		private static bool IsFolder([NotNullWhen(true)] string? path)
			=> !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && Directory.Exists(path);

		private static async Task CopyAsync(IShellPage sourcePane, IEnumerable<FolderCompareEntry> entries, string fromRoot, string toRoot)
		{
			var items = entries.ToList();
			if (items.Count == 0)
				return;

			var sources = items
				.Select(entry => StorageHelpers.FromPathAndType(
					Path.Combine(fromRoot, entry.RelativePath),
					entry.IsDirectory ? FilesystemItemType.Directory : FilesystemItemType.File))
				.ToList();
			var destinations = items.Select(entry => Path.Combine(toRoot, entry.RelativePath)).ToList();

			// The user confirmed every item in the comparison, so older files are replaced without asking again.
			await sourcePane.FilesystemHelpers.CopyItemsAsync(sources, destinations, false, true, FileNameConflictResolveOptionType.ReplaceExisting);
		}

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IContentPageContext.IsMultiPaneActive))
				OnPropertyChanged(nameof(IsExecutable));
		}
	}
}
