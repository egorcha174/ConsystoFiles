// Consysto fork: one row of an OPDS catalog page.

using Consysto.BookPreview.Opds;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Files.App.Books.Opds
{
	public sealed partial class OpdsEntryViewModel : ObservableObject
	{
		public OpdsEntryViewModel(OpdsEntry entry, bool isFirstOfGroup)
		{
			Entry = entry;
			GroupHeader = isFirstOfGroup ? entry.Group : null;
		}

		public OpdsEntry Entry { get; }

		public string Title
			=> Entry.Title ?? "—";

		public string? Subtitle
			=> Entry.Authors.Count > 0
				? string.Join(", ", Entry.Authors)
				: Entry.Summary?.Split('\n', 2)[0];

		public Visibility SubtitleVisibility
			=> string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;

		public string? GroupHeader { get; }

		public Visibility GroupHeaderVisibility
			=> string.IsNullOrEmpty(GroupHeader) ? Visibility.Collapsed : Visibility.Visible;

		/// <summary>Books get a cover-shaped slot, sections a small square one.</summary>
		public double CoverWidth
			=> Entry.IsPublication ? 48 : 32;

		public double CoverHeight
			=> Entry.IsPublication ? 72 : 32;

		public IEnumerable<OpdsAcquisition> Downloads
			=> Entry.Acquisitions.Where(acquisition => acquisition.IsDirectDownload).OrderBy(acquisition => acquisition.Format.Rank);

		/// <summary>What the download button takes: FictionBook, else EPUB, else the best of the rest.</summary>
		public OpdsAcquisition? PreferredDownload
			=> Downloads.FirstOrDefault();

		public string DownloadLabel
			=> PreferredDownload?.Format.Name ?? string.Empty;

		public Visibility DownloadVisibility
			=> PreferredDownload is null ? Visibility.Collapsed : Visibility.Visible;

		public Visibility ChevronVisibility
			=> Entry.Navigation is not null && !Entry.IsPublication ? Visibility.Visible : Visibility.Collapsed;

		private ImageSource? thumbnail;
		public ImageSource? Thumbnail
		{
			get => thumbnail;
			private set => SetProperty(ref thumbnail, value);
		}

		public async Task LoadThumbnailAsync()
			=> Thumbnail = await OpdsImages.LoadAsync(Entry.Thumbnail, 144);
	}

	internal static class OpdsImages
	{
		/// <summary>Remote images are left to BitmapImage; data: URIs, which Project Gutenberg uses for icons, are decoded here.</summary>
		public static async Task<ImageSource?> LoadAsync(string? source, int decodeHeight)
		{
			if (string.IsNullOrEmpty(source))
				return null;

			try
			{
				if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
				{
					var comma = source.IndexOf(',');
					if (comma < 0 || !source[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
						return null;

					return await Convert.FromBase64String(source[(comma + 1)..]).ToBitmapAsync();
				}

				return new BitmapImage(new Uri(source))
				{
					DecodePixelHeight = decodeHeight,
					DecodePixelType = DecodePixelType.Logical,
				};
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "An OPDS image could not be loaded");
				return null;
			}
		}
	}
}
