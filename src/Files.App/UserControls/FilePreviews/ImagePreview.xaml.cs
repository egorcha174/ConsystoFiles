using Files.App.ViewModels.Previews;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// The User Control item template is documented at https://go.microsoft.com/fwlink/?LinkId=234236

namespace Files.App.UserControls.FilePreviews
{
	public sealed partial class ImagePreview : UserControl
	{
		public ImagePreview(ImagePreviewViewModel model)
		{
			ViewModel = model;
			InitializeComponent();
		}

		private ImagePreviewViewModel ViewModel { get; set; }

		private void ImagePreview_ImageFailed(object sender, ExceptionRoutedEventArgs e)
			=> Content = new UnsupportedPreview(ViewModel);
	}
}
