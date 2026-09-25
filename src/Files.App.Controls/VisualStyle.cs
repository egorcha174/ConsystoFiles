// Consysto fork: the look chosen in Settings → Appearance. Read once at startup; a change takes effect after a restart.

namespace Files.App.Controls
{
	public static class VisualStyle
	{
		/// <summary>True for the macOS look (traffic lights, Safari tabs, zebra rows), false for the stock Windows 11 look.</summary>
		public static bool IsMac { get; set; } = true;
	}
}
