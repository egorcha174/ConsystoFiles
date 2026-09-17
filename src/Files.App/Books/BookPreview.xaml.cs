// Consysto fork: book preview for the info pane, the gallery and quick preview.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.Books
{
	public sealed partial class BookPreview : UserControl
	{
		private const double WideLayoutMinWidth = 480;

		public BookPreview(BookPreviewViewModel model)
		{
			ViewModel = model;
			InitializeComponent();

			CoverBorder.Visibility = model.CoverImage is null ? Visibility.Collapsed : Visibility.Visible;
			AuthorsBlock.Visibility = VisibleWhenSet(model.AuthorsText);
			SeriesBlock.Visibility = VisibleWhenSet(model.SeriesText);
			AnnotationBlock.Visibility = VisibleWhenSet(model.Annotation);

			SizeChanged += BookPreview_SizeChanged;
			Unloaded += BookPreview_Unloaded;
		}

		private BookPreviewViewModel ViewModel { get; }

		private static Visibility VisibleWhenSet(string? text)
			=> string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

		// The info pane is tall and narrow, the gallery's preview wide and short: the cover goes above the text or beside it.
		private void BookPreview_SizeChanged(object sender, SizeChangedEventArgs e)
		{
			var size = e.NewSize;
			var padding = LayoutRoot.Padding;
			var wide = size.Width >= WideLayoutMinWidth && size.Width > size.Height * 1.2;

			if (wide)
			{
				CoverColumn.Width = GridLength.Auto;
				TextColumn.Width = new GridLength(1, GridUnitType.Star);
				LayoutRoot.ColumnSpacing = 24;
				Grid.SetRow(TextPanel, 0);
				Grid.SetColumn(TextPanel, 1);
				CoverBorder.HorizontalAlignment = HorizontalAlignment.Left;
				CoverPicture.MaxHeight = Math.Max(120, size.Height - padding.Top - padding.Bottom);
				CoverPicture.MaxWidth = size.Width * 0.4;
			}
			else
			{
				CoverColumn.Width = new GridLength(1, GridUnitType.Star);
				TextColumn.Width = new GridLength(0);
				LayoutRoot.ColumnSpacing = 0;
				Grid.SetRow(TextPanel, 1);
				Grid.SetColumn(TextPanel, 0);
				CoverBorder.HorizontalAlignment = HorizontalAlignment.Center;
				CoverPicture.MaxHeight = Math.Clamp(size.Height * 0.55, 160, 420);
				CoverPicture.MaxWidth = double.PositiveInfinity;
			}
		}

		private void BookPreview_Unloaded(object sender, RoutedEventArgs e)
		{
			ViewModel.PreviewControlBase_Unloaded(sender, e);
			SizeChanged -= BookPreview_SizeChanged;
			Unloaded -= BookPreview_Unloaded;
		}
	}
}
