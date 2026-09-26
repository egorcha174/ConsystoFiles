// Consysto fork: how the time and plastic of a print job read in the details view and the preview pane.

using System.Globalization;
using Consysto.CadPreview.Print;

namespace Files.App.Cad
{
	internal static class PrintFormat
	{
		/// <summary>"1 ч 3 мин", "45 мин".</summary>
		public static string Time(TimeSpan time)
		{
			var minutes = (int)Math.Round(time.TotalMinutes);
			return minutes >= 60
				? string.Format(CultureInfo.CurrentCulture, Strings.ConsystoPrintHoursMinutes.GetLocalizedResource(), minutes / 60, minutes % 60)
				: string.Format(CultureInfo.CurrentCulture, Strings.ConsystoPrintMinutes.GetLocalizedResource(), Math.Max(1, minutes));
		}

		/// <summary>"20 г", "1,5 г".</summary>
		public static string Grams(double grams)
			=> string.Format(CultureInfo.CurrentCulture, Strings.ConsystoPrintGrams.GetLocalizedResource(), grams.ToString(grams < 10 ? "0.#" : "0", CultureInfo.CurrentCulture));

		/// <summary>"0,2 мм".</summary>
		public static string Millimeters(double value)
			=> string.Format(CultureInfo.CurrentCulture, Strings.ConsystoPrintMillimeters.GetLocalizedResource(), value.ToString("0.###", CultureInfo.CurrentCulture));

		/// <summary>"20 г PETG · 6,5 м".</summary>
		public static string? Filament(PrintInfo info)
		{
			var parts = new List<string>();
			var weight = info.FilamentGrams is { } grams ? Grams(grams) : null;
			if (weight is not null || info.FilamentType is not null)
				parts.Add(string.Join(" ", new[] { weight, info.FilamentType }.Where(part => part is not null)));
			if (info.FilamentMeters is { } meters)
				parts.Add(string.Format(CultureInfo.CurrentCulture, Strings.ConsystoPrintMeters.GetLocalizedResource(), meters.ToString("0.#", CultureInfo.CurrentCulture)));
			return parts.Count > 0 ? string.Join(" · ", parts) : null;
		}
	}
}
