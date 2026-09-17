// Consysto fork: book cover and description for the info pane, the gallery and quick preview.

using System.Globalization;
using Consysto.BookPreview;
using Files.App.ViewModels.Previews;
using Files.App.ViewModels.Properties;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Files.App.Books
{
	public sealed partial class BookPreviewViewModel : BasePreviewModel
	{
		public BookPreviewViewModel(ListedItem item)
			: base(item)
		{
		}

		/// <summary>Books whose metadata the fork reads itself; PDF keeps the shell's page preview.</summary>
		public static bool IsSupported(string? path)
			=> BookReader.GetFormat(path) is BookFormat.Epub or BookFormat.Fb2 or BookFormat.Mobi or BookFormat.Azw3;

		public BookInfo? Book { get; private set; }

		/// <summary>False when the file could not be read; the pane then falls back to the regular previewers.</summary>
		public bool HasPreview
			=> Book is not null;

		private BitmapImage? coverImage;
		public BitmapImage? CoverImage
		{
			get => coverImage;
			private set => SetProperty(ref coverImage, value);
		}

		public string TitleText
			=> Book?.Title ?? Item.Name ?? string.Empty;

		public string? AuthorsText
			=> Book is { Authors.Count: > 0 } ? string.Join(", ", Book.Authors) : null;

		public string? SeriesText
			=> Book?.Series is not { } series
				? null
				: Book.SeriesIndex is { } index
					? string.Format(Strings.ConsystoBookSeriesNumber.GetLocalizedResource(), series, index)
					: series;

		public string? Annotation
			=> Book?.Annotation;

		public override async Task<List<FileProperty>> LoadPreviewAndDetailsAsync()
		{
			BookInfo? book;
			try
			{
				book = await Task.Run(() => BookReader.Read(Item.ItemPath!, includeCover: true), LoadCancelledTokenSource.Token);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogWarning(ex, "Book preview could not read the file");
				return [];
			}

			if (book is null)
				return [];

			Book = book;
			if (book.Cover is { } cover)
			{
				await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () =>
				{
					try
					{
						CoverImage = await cover.ToBitmapAsync();
					}
					catch (Exception ex)
					{
						App.Logger.LogWarning(ex, "Book preview could not decode the cover");
					}
				});
			}

			List<FileProperty> details = [];
			AddDetail(details, Strings.ConsystoBookAuthor, AuthorsText);
			AddDetail(details, Strings.ConsystoBookSeries, SeriesText);
			AddDetail(details, Strings.ConsystoBookGenre, string.Join(", ", book.Genres));
			AddDetail(details, Strings.ConsystoBookLanguage, LanguageName(book.Language));
			AddDetail(details, Strings.ConsystoBookYear, book.Year);
			AddDetail(details, Strings.ConsystoBookPublisher, book.Publisher);
			AddDetail(details, Strings.ConsystoBookIsbn, book.Isbn);
			AddDetail(details, Strings.ConsystoBookTranslator, string.Join(", ", book.Translators));
			return details;
		}

		private static void AddDetail(List<FileProperty> details, string nameResource, string? value)
		{
			if (!string.IsNullOrWhiteSpace(value))
				details.Add(GetFileProperty(nameResource, value));
		}

		/// <summary>"ru" or "rus" becomes "Русский"; codes the system does not know are shown as written.</summary>
		private static string? LanguageName(string? code)
		{
			if (string.IsNullOrWhiteSpace(code))
				return null;

			code = code.Trim();
			try
			{
				var culture = code.Length == 3
					? CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => string.Equals(c.ThreeLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase))
					: CultureInfo.GetCultureInfo(code);

				var name = culture?.NativeName;
				return string.IsNullOrEmpty(name) || culture!.ThreeLetterISOLanguageName is "" or "ivl"
					? code
					: char.ToUpper(name[0], culture) + name[1..];
			}
			catch (CultureNotFoundException)
			{
				return code;
			}
		}
	}
}
