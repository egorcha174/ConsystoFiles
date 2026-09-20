// Consysto fork: the terminal in a tab, opened from the sidebar, next to "Open in Windows Terminal".

using Files.App.Terminal;

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class OpenTerminalInTabFromSidebarAction : OpenTerminalAction, IAction
	{
		private ISidebarContext SidebarContext { get; } = Ioc.Default.GetRequiredService<ISidebarContext>();

		public override string Label
			=> Strings.ConsystoOpenTerminalInTab.GetLocalizedResource();

		public override string Description
			=> Strings.ConsystoOpenTerminalInTabDescription.GetLocalizedResource();

		public override bool IsExecutable
			=> SidebarContext.IsItemRightClicked &&
				SidebarContext.RightClickedItem is { } item &&
				item.MenuOptions!.ShowShellItems &&
				!item.MenuOptions.ShowEmptyRecycleBin &&
				Ioc.Default.GetRequiredService<IContentPageContext>().ShellPage?.PaneHolder is not null;

		public override bool IsAccessibleGlobally
			=> false;

		public override HotKey HotKey
			=> HotKey.None;

		protected override string[] GetPaths()
			=> SidebarContext.IsItemRightClicked && SidebarContext.RightClickedItem is { } item
				? [item.GetRequiredPath()]
				: [];

		/// <summary>The terminal takes the second pane of the tab, so the folder it was opened from stays in sight.</summary>
		public new Task ExecuteAsync(object? parameter = null)
		{
			var paths = GetPaths();
			if (paths.Length is 0 || Ioc.Default.GetRequiredService<IContentPageContext>().ShellPage?.PaneHolder is not { } paneHolder)
				return Task.CompletedTask;

			var path = TerminalPaths.ForFolder(paths[0]);
			if (paneHolder.IsMultiPaneActive && paneHolder.GetOtherPane() is { } otherPane)
			{
				otherPane.NavigateToConsystoPage(path);
				paneHolder.FocusOtherPane();
			}
			else
			{
				paneHolder.OpenSecondaryPane(path);
			}

			return Task.CompletedTask;
		}
	}
}
