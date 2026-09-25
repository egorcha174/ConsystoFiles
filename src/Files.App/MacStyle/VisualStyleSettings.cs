// Consysto fork: stores the macOS / Windows 11 look choice. It lives in the local settings rather than in the settings
// services because the main window is created before the DI container is ready and must already know its caption buttons.
using Files.App.Controls;
using Microsoft.UI.Xaml;

namespace Files.App.MacStyle
{
	public static class VisualStyleSettings
	{
		private const string Key = "ConsystoVisualStyle";
		private const string MacThemeSource = "ms-appx:///MacStyle/MacTheme.xaml";

		/// <summary>The look saved for the next start; may differ from <see cref="VisualStyle.IsMac"/> until a restart.</summary>
		public static bool SavedIsMac
		{
			get
			{
				try
				{
					return !(AppStorage.LocalSettings.TryGetValue(Key, out var value) && value is string s && s == "windows");
				}
				catch (Exception)
				{
					return true;
				}
			}
			set => AppStorage.LocalSettings[Key] = value ? "mac" : "windows";
		}

		/// <summary>Called once before the main window is created.</summary>
		public static void ApplyAtStartup(Application app)
		{
			VisualStyle.IsMac = SavedIsMac;
			if (VisualStyle.IsMac)
				return;

			var merged = app.Resources.MergedDictionaries;
			for (var i = merged.Count - 1; i >= 0; i--)
			{
				if (merged[i].Source?.OriginalString == MacThemeSource)
					merged.RemoveAt(i);
			}

			merged.Add(new ResourceDictionary { Source = new Uri("ms-appx:///MacStyle/WindowsTheme.xaml") });
		}
	}
}
