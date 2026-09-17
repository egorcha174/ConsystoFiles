// Consysto fork: DWG/DXF thumbnails.

using System.Security.Cryptography;
using System.Text;
using Consysto.CadPreview;
using Consysto.CadPreview.WinUI;
using Microsoft.Extensions.Logging;
using Windows.Storage;

namespace Files.App.Cad
{
	/// <summary>
	/// Thumbnails drawn by the CAD core instead of the shell, which has no handler for DWG/DXF.
	/// Cached on disk by path, length, timestamp and size, so a folder of drawings is rendered once.
	/// </summary>
	public static class CadThumbnailService
	{
		private const uint MinimumSize = 48;
		private const long MaximumFileBytes = 64 * 1024 * 1024;
		private const string CacheVersion = "v1";

		// Parsing a drawing is CPU-bound; a folder full of them must not starve the UI.
		private static readonly SemaphoreSlim renderGate = new(Math.Max(1, Environment.ProcessorCount / 2));

		private static readonly Lazy<string> cacheDirectory = new(() =>
		{
			var directory = SystemIO.Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "cad-thumbnails");
			SystemIO.Directory.CreateDirectory(directory);
			return directory;
		});

		public static bool IsSupported(string? extension)
			=> CadPreviewSetup.IsSupported(extension);

		public static async Task<byte[]?> GetThumbnailAsync(string path, uint size)
		{
			if (size < MinimumSize)
				return null;

			try
			{
				var file = new SystemIO.FileInfo(path);
				if (!file.Exists || file.Length > MaximumFileBytes)
					return null;

				var cachePath = SystemIO.Path.Combine(cacheDirectory.Value, CacheKey(file, size) + ".png");
				if (SystemIO.File.Exists(cachePath))
					return await SystemIO.File.ReadAllBytesAsync(cachePath);

				await renderGate.WaitAsync();
				try
				{
					var content = await Task.Run(() => CadPreviewSource.Load(path));
					byte[]? png = content switch
					{
						{ Drawing: { } drawing } => await CadThumbnailRenderer.RenderDrawingAsync(drawing, (int)size),
						{ Mesh: { } mesh } => await CadThumbnailRenderer.RenderMeshAsync(mesh, (int)size),
						{ Image: { } image } => await CadThumbnailRenderer.RenderImageAsync(image, (int)size),
						_ => null,
					};

					if (png is not null)
						await SystemIO.File.WriteAllBytesAsync(cachePath, png);

					return png;
				}
				finally
				{
					renderGate.Release();
				}
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, "CAD thumbnail rendering failed");
				return null;
			}
		}

		private static string CacheKey(SystemIO.FileInfo file, uint size)
		{
			var identity = $"{file.FullName.ToUpperInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{size}|{CacheVersion}";
			return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(identity)));
		}
	}
}
