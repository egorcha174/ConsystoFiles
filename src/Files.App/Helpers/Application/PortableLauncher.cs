// Consysto fork: opening a second window of the portable build.

using System.Diagnostics;

namespace Files.App.Helpers
{
	/// <summary>
	/// An installed build opens a new window by calling its own address ("consysto-files:…"), which Windows knows because the
	/// package declares it. A portable folder declares nothing, so Windows would answer "no app can open this link". It starts
	/// a second copy of itself instead, telling it not to hand the request over to the window that is already open.
	/// </summary>
	public static class PortableLauncher
	{
		/// <summary>Set for the new copy so that it opens its own window instead of a tab in the running one.</summary>
		public const string NewWindowVariable = "CONSYSTO_NEW_WINDOW";

		public static bool ShouldOpenOwnWindow
			=> Environment.GetEnvironmentVariable(NewWindowVariable) == "1";

		/// <param name="path">The folder for the new window, or null for an empty one.</param>
		public static bool OpenWindow(string? path = null)
		{
			try
			{
				var start = new ProcessStartInfo(SystemIO.Path.Combine(AppContext.BaseDirectory, "Files.exe"))
				{
					UseShellExecute = false,
					WorkingDirectory = AppContext.BaseDirectory,
				};
				start.Environment[NewWindowVariable] = "1";
				if (!string.IsNullOrEmpty(path))
					start.ArgumentList.Add(path);

				using var process = Process.Start(start);
				return process is not null;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
