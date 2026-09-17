// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Windows.ApplicationModel.DataTransfer;

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class CutItemAction : BaseTransferItemAction, IAction
	{
		public string Label
			=> Strings.Cut.GetLocalizedResource();

		public string Description
			=> Strings.CutItemDescription.GetLocalizedFormatResource(ContentPageContext.SelectedItems.Count);

		public ActionCategory Category
			=> ActionCategory.FileSystem;

		public RichGlyph Glyph
			=> new(themedIconStyle: "App.ThemedIcons.Cut");

		public string AutomationId
			=> "InnerNavigationToolbarCutButton";

		public string AccessKey
			=> "X";

		public HotKey HotKey
			=> new(Keys.X, KeyModifiers.Ctrl);

		/// <summary>Cutting a part out of an assembly would move it away from the assembly that uses it.</summary>
		public override bool IsExecutable
			=> base.IsExecutable && !Files.App.Cad.InventorAssemblyPaths.IsShowingAssembly(ContentPageContext.ShellPage);

		public CutItemAction() : base()
		{
		}

		public Task ExecuteAsync(object? parameter = null)
		{
			return ExecuteTransferAsync(DataPackageOperation.Move);
		}
	}
}
