using System.IO;
using Windows.Storage;

namespace Files.App.Actions
{
	/// <summary>
	/// Consysto fork: Commander One-like F5/F6, copying or moving the selection into the folder open in the other pane
	/// after a confirmation.
	/// </summary>
	internal abstract class BaseTransferToOtherPaneAction : ObservableObject
	{
		protected readonly IContentPageContext context;

		public ActionCategory Category
			=> ActionCategory.DualPane;

		public bool IsExecutable
			=> context.IsMultiPaneActive &&
				context.HasSelection &&
				context.PageType is not (ContentPageTypes.Home or ContentPageTypes.RecycleBin or ContentPageTypes.ReleaseNotes or ContentPageTypes.Settings);

		protected BaseTransferToOtherPaneAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();

			context.PropertyChanged += Context_PropertyChanged;
		}

		protected async Task TransferAsync(bool move)
		{
			if (context.ShellPage is not { } shellPage ||
				shellPage.PaneHolder?.GetOtherPane() is not { } otherPane)
				return;

			var title = (move ? Strings.ConsystoMoveToOtherPane : Strings.ConsystoCopyToOtherPane).GetLocalizedResource();
			var destination = otherPane.ShellViewModel?.WorkingDirectory;
			if (string.IsNullOrEmpty(destination) || !Path.IsPathRooted(destination) || Files.App.Cad.InventorAssemblyPaths.IsAssemblyPath(destination))
			{
				await DialogDisplayHelper.ShowDialogAsync(title, Strings.ConsystoOtherPaneNotFolder.GetLocalizedResource());
				return;
			}

			// Parts listed in an assembly stay where the assembly expects them.
			if (move && Files.App.Cad.InventorAssemblyPaths.IsShowingAssembly(shellPage))
				return;

			var items = context.SelectedItems.ToList();
			if (items.Count == 0)
				return;

			// Nothing to do when the other pane shows the same folder.
			var destinationDirectory = destination.TrimEnd('\\');
			if (items.All(item => string.Equals(Path.GetDirectoryName(item.ItemPath)?.TrimEnd('\\'), destinationDirectory, StringComparison.OrdinalIgnoreCase)))
				return;

			var confirmation = string.Format(
				(move ? Strings.ConsystoMoveToOtherPaneConfirm : Strings.ConsystoCopyToOtherPaneConfirm).GetLocalizedResource(),
				items.Count,
				destination);
			var primaryText = (move ? Strings.MoveItemsDialogPrimaryButtonText : Strings.Copy).GetLocalizedResource();
			if (!await DialogDisplayHelper.ShowDialogAsync(title, confirmation, primaryText, Strings.Cancel.GetLocalizedResource()))
				return;

			var sources = items
				.Select(item => StorageHelpers.FromPathAndType(
					item.GetRequiredPath(),
					item.PrimaryItemAttribute == StorageItemTypes.Folder ? FilesystemItemType.Directory : FilesystemItemType.File))
				.ToList();
			var destinations = sources.Select(source => PathNormalization.Combine(destination, source.Name)).ToList();

			if (move)
				await shellPage.FilesystemHelpers.MoveItemsAsync(sources, destinations, true, true);
			else
				await shellPage.FilesystemHelpers.CopyItemsAsync(sources, destinations, true, true);

			await otherPane.RefreshIfNoWatcherExistsAsync();
			if (move)
				await shellPage.RefreshIfNoWatcherExistsAsync();
		}

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(IContentPageContext.IsMultiPaneActive):
				case nameof(IContentPageContext.HasSelection):
				case nameof(IContentPageContext.PageType):
					OnPropertyChanged(nameof(IsExecutable));
					break;
			}
		}
	}
}
