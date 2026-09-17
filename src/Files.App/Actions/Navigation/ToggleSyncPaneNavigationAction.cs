namespace Files.App.Actions
{
	/// <summary>
	/// Consysto fork: toggles synchronized navigation, where stepping into a subfolder or up in the active pane repeats the
	/// same step in the other pane.
	/// </summary>
	[GeneratedRichCommand]
	internal sealed partial class ToggleSyncPaneNavigationAction : ObservableObject, IToggleAction
	{
		private readonly IContentPageContext context;
		private IShellPanesPage? paneHolder;

		public string Label
			=> Strings.ConsystoSyncNavigation.GetLocalizedResource();

		public string Description
			=> Strings.ConsystoSyncNavigationDescription.GetLocalizedResource();

		public ActionCategory Category
			=> ActionCategory.DualPane;

		public RichGlyph Glyph
			=> new("\uE71B");

		public bool IsOn
			=> paneHolder?.IsSyncNavigationEnabled ?? false;

		public bool IsExecutable
			=> context.IsMultiPaneActive;

		public ToggleSyncPaneNavigationAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();

			context.PropertyChanged += Context_PropertyChanged;
			SetPaneHolder(context.ShellPage?.PaneHolder);
		}

		public Task ExecuteAsync(object? parameter = null)
		{
			if (context.ShellPage?.PaneHolder is { } holder)
				holder.IsSyncNavigationEnabled = !holder.IsSyncNavigationEnabled;

			return Task.CompletedTask;
		}

		private void SetPaneHolder(IShellPanesPage? holder)
		{
			if (paneHolder is not null)
				paneHolder.PropertyChanged -= PaneHolder_PropertyChanged;

			paneHolder = holder;

			if (paneHolder is not null)
				paneHolder.PropertyChanged += PaneHolder_PropertyChanged;

			OnPropertyChanged(nameof(IsOn));
		}

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(IContentPageContext.ShellPage):
					SetPaneHolder(context.ShellPage?.PaneHolder);
					OnPropertyChanged(nameof(IsExecutable));
					break;
				case nameof(IContentPageContext.IsMultiPaneActive):
					OnPropertyChanged(nameof(IsExecutable));
					break;
			}
		}

		private void PaneHolder_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IShellPanesPage.IsSyncNavigationEnabled))
				OnPropertyChanged(nameof(IsOn));
		}
	}
}
