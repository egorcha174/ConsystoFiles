namespace Files.App.Actions
{
	/// <summary>
	/// Consysto fork: F6 moves the selection into the folder open in the other pane.
	/// </summary>
	[GeneratedRichCommand]
	internal sealed partial class MoveToOtherPaneAction : BaseTransferToOtherPaneAction, IAction
	{
		public string Label
			=> Strings.ConsystoMoveToOtherPane.GetLocalizedResource();

		public string Description
			=> Strings.ConsystoMoveToOtherPaneDescription.GetLocalizedResource();

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.Cut");

		public HotKey HotKey
			=> new(Keys.F6);

		public Task ExecuteAsync(object? parameter = null)
			=> TransferAsync(move: true);
	}
}
