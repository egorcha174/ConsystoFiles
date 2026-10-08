using Files.App.UserControls.FilePreviews;
using Files.App.ViewModels.Previews;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage;

namespace Files.App.MacStyle
{
	/// <summary>
	/// Consysto fork: quick preview of the selected item over the window, toggled with Space like Finder's Quick Look. The
	/// control comes from the preview pane's chain, so CAD formats are drawn here too. The file list keeps keyboard focus,
	/// so the arrows switch the item and Space closes it again.
	/// </summary>
	public sealed partial class QuickPreviewOverlay : UserControl
	{
		private readonly IContentPageContext _context = Ioc.Default.GetRequiredService<IContentPageContext>();
		private readonly InfoPaneViewModel _infoPaneViewModel = Ioc.Default.GetRequiredService<InfoPaneViewModel>();

		private ListedItem? _item;
		private int _loadVersion;
		private CancellationTokenSource? _loadCancellation;

		public QuickPreviewOverlay()
		{
			InitializeComponent();

			_context.PropertyChanged += Context_PropertyChanged;
		}

		public bool IsOpen
			=> Visibility is Visibility.Visible;

		public async Task ShowAsync(ListedItem item)
		{
			Visibility = Visibility.Visible;
			if (ReferenceEquals(item, _item))
				return;

			_item = item;
			_loadCancellation?.Cancel();
			_loadCancellation?.Dispose();
			_loadCancellation = new CancellationTokenSource();
			var token = _loadCancellation.Token;
			var version = ++_loadVersion;
			FileNameText.Text = item.ItemNameRaw ?? item.Name;
			PreviewHost.Content = null;
			LoadingRing.IsActive = true;

			UIElement? control = null;
			try
			{
				control = await _infoPaneViewModel.GetBuiltInPreviewControlAsync(item, false, token);
				if (control is null)
				{
					var model = new BasicPreviewViewModel(item);
					await model.LoadAsync();
					control = item.PrimaryItemAttribute == StorageItemTypes.Folder || _infoPaneViewModel.ShowCloudItemButton
						? new BasicPreview(model) : new UnsupportedPreview(model);
				}
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				return;
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "Quick preview failed to load");
				control = new UnsupportedPreview();
			}

			// A newer item or a close came in while this one was loading.
			if (version != _loadVersion)
				return;

			LoadingRing.IsActive = false;
			PreviewHost.Content = control;
		}

		public void Close()
		{
			_loadVersion++;
			_loadCancellation?.Cancel();
			_loadCancellation?.Dispose();
			_loadCancellation = null;
			_item = null;
			LoadingRing.IsActive = false;
			PreviewHost.Content = null;
			Visibility = Visibility.Collapsed;
		}

		private void Context_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(IContentPageContext.Folder) && IsOpen)
				Close();
		}

		private void Backdrop_Tapped(object sender, TappedRoutedEventArgs e)
			=> Close();

		private void CloseButton_Click(object sender, RoutedEventArgs e)
			=> Close();

		private async void OpenButton_Click(object sender, RoutedEventArgs e)
		{
			Close();
			await Ioc.Default.GetRequiredService<ICommandManager>().OpenItem.ExecuteAsync();
		}
	}
}
