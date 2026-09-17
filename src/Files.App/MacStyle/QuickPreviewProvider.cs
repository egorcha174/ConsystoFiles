using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace Files.App.MacStyle
{
	/// <summary>
	/// Consysto fork: the built-in quick preview behind Space, in the same provider slot as QuickLook, Seer Pro and Peek.
	/// </summary>
	internal sealed class QuickPreviewProvider : IPreviewPopupProvider
	{
		public static QuickPreviewProvider Instance { get; } = new();

		private readonly IContentPageContext _context = Ioc.Default.GetRequiredService<IContentPageContext>();

		public async Task TogglePreviewPopupAsync(string path)
		{
			if (GetOverlay() is not { } overlay)
				return;

			if (overlay.IsOpen)
			{
				overlay.Close();
				return;
			}

			if (FindItem(path) is { } item)
				await overlay.ShowAsync(item);
		}

		public async Task SwitchPreviewAsync(string path)
		{
			if (GetOverlay() is { IsOpen: true } overlay && FindItem(path) is { } item)
				await overlay.ShowAsync(item);
		}

		public Task<bool> DetectAvailability()
			=> Task.FromResult(true);

		private ListedItem? FindItem(string path)
			=> _context.SelectedItems.FirstOrDefault(item => string.Equals(item.ItemPath, path, StringComparison.OrdinalIgnoreCase))
				?? _context.SelectedItem;

		[DynamicWindowsRuntimeCast(typeof(Frame))]
		private static QuickPreviewOverlay? GetOverlay()
			=> App.AppModel.IsMainWindowClosed
				? null
				: ((MainWindow.Instance.Content as Frame)?.Content as MainPage)?.QuickPreview;
	}
}
