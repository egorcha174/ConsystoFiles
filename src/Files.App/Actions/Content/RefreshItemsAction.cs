// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class RefreshItemsAction : ObservableObject, IAction
	{
		private readonly IContentPageContext context;

		public string Label
			=> Strings.Refresh.GetLocalizedResource();

		public string Description
			=> Strings.RefreshItemsDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.FileSystem;

		public RichGlyph Glyph
			=> new("\uE72C");

		public HotKey HotKey
			=> new(Keys.R, KeyModifiers.Ctrl);

		// Consysto fork: F5 copies into the other pane (Commander One), refresh stays on Ctrl+R
		public bool IsExecutable
			=> context.CanRefresh;

		public RefreshItemsAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();

			context.PropertyChanged += Context_PropertyChanged;
		}

		public async Task ExecuteAsync(object? parameter = null)
		{
			if (context.ShellPage is null)
				return;

			await context.ShellPage.Refresh_Click();
		}

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(IContentPageContext.CanRefresh):
					OnPropertyChanged(nameof(IsExecutable));
					break;
			}
		}
	}
}
