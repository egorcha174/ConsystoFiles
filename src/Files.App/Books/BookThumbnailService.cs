// Consysto fork: book covers as thumbnails.

using System.Security.Cryptography;
using System.Text;
using Consysto.BookPreview;
using Consysto.CadPreview.WinUI;
using Microsoft.Extensions.Logging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Files.App.Books
{
	/// <summary>
	/// Thumbnails from the cover stored inside a book (EPUB, FictionBook, Kindle) or the first page of a PDF; the shell has neither.
	/// Cached on disk like CAD thumbnails, "no cover" included, so a folder of books is read once.
	/// </summary>
	public static class BookThumbnailService
	{
		private const uint MinimumSize = 48;
		private const string CacheVersion = "v1";

		// Reading a cover is disk and CPU work; a folder full of books must not starve the UI.
		private static readonly SemaphoreSlim renderGate = new(Math.Max(1, Environment.ProcessorCount / 2));

		private static readonly Lazy<string> cacheDirectory = new(() =>
		{
			var directory = SystemIO.Path.Combine(AppStorage.LocalCacheFolderPath, "book-thumbnails");
			SystemIO.Directory.CreateDirectory(directory);
			return directory;
		});

		public static bool IsSupported(string? path)
			=> BookReader.GetFormat(path) is BookFormat.Epub or BookFormat.Fb2 or BookFormat.Mobi or BookFormat.Azw3 or BookFormat.Pdf;

		public static async Task<byte[]?> GetThumbnailAsync(string path, uint size)
		{
			if (size < MinimumSize)
				return null;

			string? noCoverPath = null;
			try
			{
				var file = new SystemIO.FileInfo(path);
				if (!file.Exists)
					return null;

				var key = CacheKey(file, size);
				var cachePath = SystemIO.Path.Combine(cacheDirectory.Value, key + ".png");
				noCoverPath = SystemIO.Path.Combine(cacheDirectory.Value, key + ".none");
				if (SystemIO.File.Exists(cachePath))
					return await SystemIO.File.ReadAllBytesAsync(cachePath);
				if (SystemIO.File.Exists(noCoverPath))
					return null;

				await renderGate.WaitAsync();
				try
				{
					var cover = BookReader.GetFormat(path) is BookFormat.Pdf
						? await RenderPdfFirstPageAsync(path, size)
						: await Task.Run(() => BookReader.Read(path, includeCover: true)?.Cover);

					var png = cover is null ? null : await CadThumbnailRenderer.RenderImageAsync(cover, (int)size);
					if (png is not null)
						await SystemIO.File.WriteAllBytesAsync(cachePath, png);
					else
						await SystemIO.File.WriteAllBytesAsync(noCoverPath, []);

					return png;
				}
				finally
				{
					renderGate.Release();
				}
			}
			catch (Exception ex)
			{
				// Damaged or password-protected books keep their icon; the key includes size and date, so a rewritten file is tried again.
				App.Logger.LogWarning(ex, "Book thumbnail could not be read");
				if (noCoverPath is not null)
				{
					try { await SystemIO.File.WriteAllBytesAsync(noCoverPath, []); }
					catch (SystemIO.IOException) { }
				}

				return null;
			}
		}

		private static async Task<byte[]?> RenderPdfFirstPageAsync(string path, uint size)
		{
			var file = await StorageFile.GetFileFromPathAsync(path);
			var document = await PdfDocument.LoadFromFileAsync(file);
			if (document.PageCount == 0)
				return null;

			using var page = document.GetPage(0);
			var options = new PdfPageRenderOptions { BackgroundColor = Microsoft.UI.Colors.White };
			if (page.Size.Width >= page.Size.Height)
				options.DestinationWidth = size;
			else
				options.DestinationHeight = size;

			using var stream = new InMemoryRandomAccessStream();
			await page.RenderToStreamAsync(stream, options);

			var bytes = new byte[stream.Size];
			using var reader = new DataReader(stream.GetInputStreamAt(0));
			await reader.LoadAsync((uint)stream.Size);
			reader.ReadBytes(bytes);
			return bytes;
		}

		private static string CacheKey(SystemIO.FileInfo file, uint size)
		{
			var identity = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{size}|{CacheVersion}";
			return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
		}
	}
}
