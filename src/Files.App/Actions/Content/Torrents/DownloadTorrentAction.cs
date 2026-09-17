// Consysto fork: a selected .torrent file starts downloading inside Files.

using Files.App.Torrents;

namespace Files.App.Actions
{
	[GeneratedRichCommand]
	internal sealed partial class DownloadTorrentAction : ObservableObject, IAction
	{
		private readonly IContentPageContext context;

		public string Label
			=> Strings.ConsystoTorrentDownload.GetLocalizedResource();

		public string Description
			=> Strings.ConsystoTorrentDownloadDescription.GetLocalizedResource();

		public RichGlyph Glyph
			=> new(TorrentPaths.Glyph);

		public bool IsExecutable
			=> context.SelectedItems.Count == 1 && TorrentHost.IsTorrentFile(context.SelectedItem?.ItemPath);

		public DownloadTorrentAction()
		{
			context = Ioc.Default.GetRequiredService<IContentPageContext>();
			context.PropertyChanged += Context_PropertyChanged;
		}

		public Task ExecuteAsync(object? parameter = null)
			=> context.SelectedItem?.ItemPath is { } path ? TorrentHost.AddAsync(path, context.ShellPage) : Task.CompletedTask;

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IContentPageContext.SelectedItems))
				OnPropertyChanged(nameof(IsExecutable));
		}
	}

	/// <summary>Consysto fork: the torrent downloads tab, from the command palette.</summary>
	[GeneratedRichCommand]
	internal sealed partial class OpenTorrentsAction : IAction
	{
		public string Label
			=> Strings.ConsystoTorrents.GetLocalizedResource();

		public string Description
			=> Strings.ConsystoTorrentsDescription.GetLocalizedResource();

		public RichGlyph Glyph
			=> new(TorrentPaths.Glyph);

		public Task ExecuteAsync(object? parameter = null)
			=> TorrentHost.OpenPageAsync();
	}
}
