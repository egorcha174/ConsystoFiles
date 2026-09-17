// Consysto fork: the terminal opened inside Files, in the second pane of the tab, instead of a separate window.

using Files.App.Terminal;

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class OpenTerminalInTabAction : OpenTerminalAction, IAction
	{
		public override string Label
			=> Strings.ConsystoOpenTerminalInTab.GetLocalizedResource();

		public override string Description
			=> Strings.ConsystoOpenTerminalInTabDescription.GetLocalizedResource();

		public override HotKey HotKey
			=> new(Keys.Oem3, KeyModifiers.CtrlAlt);

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
