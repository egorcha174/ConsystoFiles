// Consysto fork: author and series of books for the details view columns.

using System.Collections.Concurrent;
using System.Globalization;
using Consysto.BookPreview;
using Microsoft.Extensions.Logging;

namespace Files.App.Books
{
	/// <summary>Author and series as the details view shows and sorts them.</summary>
	public sealed record BookColumns(string? Author, string? Series, string? SeriesSortKey);

	/// <summary>
	/// Reads a book's description without its cover and remembers the result by path, size and date,
	/// so returning to a folder or sorting it again does not open the files a second time.
	/// </summary>
	public static class BookColumnsCache
	{
		private static readonly ConcurrentDictionary<string, BookColumns?> cache = new(StringComparer.OrdinalIgnoreCase);

		public static bool IsSupported(string? path)
			=> BookPreviewViewModel.IsSupported(path);

		public static BookColumns? Read(string path)
		{
			SystemIO.FileInfo file;
			try
			{
				file = new SystemIO.FileInfo(path);
				if (!file.Exists)
					return null;
			}
			catch (Exception ex) when (ex is SystemIO.IOException or UnauthorizedAccessException or ArgumentException)
			{
				return null;
			}

			var key = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
			if (cache.TryGetValue(key, out var cached))
				return cached;

			BookColumns? columns = null;
			try
			{
				if (BookReader.Read(path, includeCover: false) is { } book)
					columns = new(book.Authors.Count > 0 ? string.Join(", ", book.Authors) : null, SeriesText(book), SeriesSortKey(book));
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "Book columns could not be read");
			}

			cache[key] = columns;
			return columns;
		}

		private static string? SeriesText(BookInfo book)
			=> book.Series is not { } series
				? null
				: book.SeriesIndex is { } index
					? string.Format(Strings.ConsystoBookSeriesNumber.GetLocalizedResource(), series, index)
					: series;

		private static string? SeriesSortKey(BookInfo book)
		{
			if (book.Series is not { } series)
				return null;

			var position = double.TryParse(book.SeriesIndex, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
			return $"{series} {position.ToString("000000.###", CultureInfo.InvariantCulture)}";
		}
	}
}
