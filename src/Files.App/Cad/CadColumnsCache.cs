// Consysto fork: part number, material, mass and program version of drawings and models for the details view columns.

using System.Collections.Concurrent;
using Consysto.CadPreview;
using Consysto.CadPreview.Inventor;
using Microsoft.Extensions.Logging;

namespace Files.App.Cad
{
	/// <summary>The iProperties the details view shows and sorts by.</summary>
	/// <remarks>A print job fills the same columns: plastic as the material, its weight as the mass, the slicer as the version.</remarks>
	public sealed record CadColumns(string? PartNumber, string? Material, string? Mass, double? MassKilograms, string? Version, string? PrintTime = null, double? PrintMinutes = null);

	/// <summary>
	/// Reads the iProperties of a document and remembers them by path, size and date, so returning to a folder or sorting it
	/// again does not open the files a second time.
	/// </summary>
	public static class CadColumnsCache
	{
		private static readonly ConcurrentDictionary<string, CadColumns?> cache = new(StringComparer.OrdinalIgnoreCase);

		public static bool IsSupported(string? path)
			=> CadVersionReader.IsSupported(SystemIO.Path.GetExtension(path))
				|| Consysto.CadPreview.Print.GcodeReader.IsPrintFile(path);

		public static CadColumns? Read(string path)
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

			CadColumns? columns = null;
			if (Consysto.CadPreview.Print.GcodeReader.IsPrintFile(file.FullName))
			{
				columns = ReadPrint(path);
				cache[key] = columns;
				return columns;
			}

			try
			{
				// Only Inventor documents carry iProperties; a DWG or DXF has just its format version
				var properties = InventorPropertyReader.IsSupported(file.Extension) ? InventorPropertyReader.Read(path) : null;
				var mass = properties?.MassKilograms is { } kilograms
					? string.Format(Strings.ConsystoIPropertyMassValue.GetLocalizedResource(), CadPreviewViewModel.FormatMass(kilograms))
					: null;
				var version = CadVersionReader.Read(path);
				if (properties?.PartNumber is not null || properties?.Material is not null || mass is not null || version is not null)
					columns = new(properties?.PartNumber, properties?.Material, mass, properties?.MassKilograms, version);
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "Inventor columns could not be read");
			}

			cache[key] = columns;
			return columns;
		}

		private static CadColumns? ReadPrint(string path)
		{
			try
			{
				var info = Consysto.CadPreview.Print.GcodeReader.Read(path);
				if (info.PrintTime is null && info.FilamentGrams is null && info.FilamentType is null && info.Slicer is null)
					return null;

				return new(
					PartNumber: null,
					Material: info.FilamentType,
					Mass: info.FilamentGrams is { } grams ? PrintFormat.Grams(grams) : null,
					MassKilograms: info.FilamentGrams / 1000,
					Version: info.Slicer,
					PrintTime: info.PrintTime is { } time ? PrintFormat.Time(time) : null,
					PrintMinutes: info.PrintTime?.TotalMinutes);
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "Print job columns could not be read");
				return null;
			}
		}
	}
}
