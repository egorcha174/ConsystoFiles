// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class DeleteItemAction : BaseDeleteAction, IAction
	{
		public string Label
			=> Strings.Delete.GetLocalizedResource();

		public string Description
			=> Strings.DeleteItemDescription.GetLocalizedFormatResource(context.SelectedItems.Count);

		public override ActionCategory Category
			=> ActionCategory.FileSystem;

		public RichGlyph Glyph
			=> new RichGlyph(themedIconStyle: "App.ThemedIcons.Delete");

		public string AutomationId
			=> "InnerNavigationToolbarDeleteButton";

		public string AccessKey
			=> "D";

		public HotKey HotKey
			=> new(Keys.Delete);

		public HotKey SecondHotKey
			=> new(Keys.D, KeyModifiers.Ctrl);

		// Consysto fork: F8 as in Commander One, Ctrl+Backspace as Cmd+Backspace in Finder
		public HotKey ThirdHotKey
			=> new(Keys.F8);

		public HotKey MediaHotKey
			=> new(Keys.Back, KeyModifiers.Ctrl);

		public Task ExecuteAsync(object? parameter = null)
		{
			return DeleteItemsAsync(false);
		}
	}
}
