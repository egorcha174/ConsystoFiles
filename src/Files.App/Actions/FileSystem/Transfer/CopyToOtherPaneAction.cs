namespace Files.App.Actions
{
	/// <summary>
	/// Consysto fork: F5 copies the selection into the folder open in the other pane.
	/// </summary>
	[GeneratedRichCommand]
	internal sealed partial class CopyToOtherPaneAction : BaseTransferToOtherPaneAction, IAction
	{
		public string Label
			=> Strings.ConsystoCopyToOtherPane.GetLocalizedResource();

		public string Description
			=> Strings.ConsystoCopyToOtherPaneDescription.GetLocalizedResource();

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.Copy");

		public HotKey HotKey
			=> new(Keys.F5);

		public Task ExecuteAsync(object? parameter = null)
			=> TransferAsync(move: false);
	}
}
