namespace Files.App.Actions
{
	/// <summary>
	/// Consysto fork: Ctrl+U swaps the folders of the two panes, as in Commander One.
	/// </summary>
	[GeneratedRichCommand]
	internal sealed partial class SwapPanesAction : ObservableObject, IAction
	{
		private readonly IContentPageContext context;

		public string Label
			=> Strings.ConsystoSwapPanes.GetLocalizedResource();

		public string Description
			=> Strings.ConsystoSwapPanesDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.DualPane;

		public RichGlyph Glyph
			=> new("\uE8AB");

		public HotKey HotKey
			=> new(Keys.U, KeyModifiers.Ctrl);

		public bool IsExecutable
			=> context.IsMultiPaneActive;

		public SwapPanesAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();

			context.PropertyChanged += Context_PropertyChanged;
		}

		public Task ExecuteAsync(object? parameter = null)
		{
			context.ShellPage?.PaneHolder?.SwapPanes();

			return Task.CompletedTask;
		}

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IContentPageContext.IsMultiPaneActive))
				OnPropertyChanged(nameof(IsExecutable));
		}
	}
}
