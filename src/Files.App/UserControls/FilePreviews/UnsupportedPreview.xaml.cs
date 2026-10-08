using Files.App.ViewModels.Previews;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.UserControls.FilePreviews
{
	public sealed partial class UnsupportedPreview : UserControl
	{
		/// <param name="message">Instead of "not supported", e.g. when the format is fine but the file holds no picture.</param>
		public UnsupportedPreview(BasePreviewModel? model = null, string? message = null)
		{
			InitializeComponent();
			if (message is not null)
				MessageText.Text = message;
			if (model is not null)
				Unloaded += model.PreviewControlBase_Unloaded;
		}
	}
}
