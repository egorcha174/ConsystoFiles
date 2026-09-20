// Consysto fork: lets the portable build offer itself for .torrent files and magnet links.

using Microsoft.Win32;

namespace Files.App.Torrents
{
	/// <summary>
	/// An installed package declares its file types in its manifest. A portable folder has no manifest, so it writes the same
	/// declarations for the current user: the program then appears in "Open with" and on the Default apps page, where the user
	/// picks it. Everything lives under the user's own classes, so removing it takes no administrator rights.
	/// </summary>
	public static class PortableTorrentAssociation
	{
		private const string TorrentProgId = "ConsystoFiles.Torrent";

		private const string MagnetProgId = "ConsystoFiles.Magnet";

		private const string ApplicationKey = "ConsystoFiles";

		private static string Command
			=> $"\"{SystemIO.Path.Combine(AppContext.BaseDirectory, "Files.exe")}\" \"%1\"";

		/// <summary>Declares the program as a possible handler; which one actually opens the file stays the user's choice.</summary>
		public static void Register()
		{
			WriteProgId(TorrentProgId, Strings.ConsystoTorrentFileType.GetLocalizedResource(), isProtocol: false);
			WriteProgId(MagnetProgId, Strings.ConsystoTorrentMagnetType.GetLocalizedResource(), isProtocol: true);

			using (var extension = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.torrent\OpenWithProgids"))
				extension.SetValue(TorrentProgId, Array.Empty<byte>(), RegistryValueKind.None);

			// Windows looks for a program on the Default apps page by its capabilities, not by the types alone; without these
			// the program is offered for .torrent files but never for magnet links.
			using (var capabilities = Registry.CurrentUser.CreateSubKey($@"Software\{ApplicationKey}\Capabilities"))
			{
				capabilities.SetValue("ApplicationName", "Consysto Files");
				capabilities.SetValue("ApplicationDescription", Strings.ConsystoTorrentDefaultDescription.GetLocalizedResource());

				using (var files = capabilities.CreateSubKey("FileAssociations"))
					files.SetValue(".torrent", TorrentProgId);

				using var urls = capabilities.CreateSubKey("URLAssociations");
				urls.SetValue("magnet", MagnetProgId);
			}

			using var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications");
			registered.SetValue(ApplicationKey, $@"Software\{ApplicationKey}\Capabilities");
		}

		/// <summary>Removes the declarations, so nothing is left in the registry once the folder is deleted.</summary>
		public static void Unregister()
		{
			using (var extension = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.torrent\OpenWithProgids", writable: true))
				extension?.DeleteValue(TorrentProgId, false);

			Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{TorrentProgId}", false);
			Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{MagnetProgId}", false);
			Registry.CurrentUser.DeleteSubKeyTree($@"Software\{ApplicationKey}", false);

			using var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true);
			registered?.DeleteValue(ApplicationKey, false);
		}

		public static bool IsRegistered
		{
			get
			{
				using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{TorrentProgId}\shell\open\command");
				return key?.GetValue(string.Empty) is string command && command.Contains(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase);
			}
		}

		/// <summary>
		/// Clears what a copy that no longer exists left behind: its file types and its autorun entry. A portable folder can be
		/// deleted at any moment, and then nothing is left to clean up after itself, so every copy that starts does it.
		/// </summary>
		public static void RemoveTracesOfDeletedCopies()
		{
			using (var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{TorrentProgId}\shell\open\command"))
			{
				if (key?.GetValue(string.Empty) is string command && ProgramIsGone(command))
					Unregister();
			}

			using var autorun = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
			if (autorun?.GetValue("ConsystoFiles") is string autorunCommand && ProgramIsGone(autorunCommand))
				autorun.DeleteValue("ConsystoFiles", false);
		}

		private static bool ProgramIsGone(string command)
		{
			var path = command.StartsWith('"') ? command[1..].Split('"')[0] : command.Split(' ')[0];
			return !SystemIO.File.Exists(path);
		}

		private static void WriteProgId(string progId, string title, bool isProtocol)
		{
			using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}");
			key.SetValue(string.Empty, title);
			if (isProtocol)
				key.SetValue("URL Protocol", string.Empty);

			using (var icon = key.CreateSubKey("DefaultIcon"))
				icon.SetValue(string.Empty, SystemIO.Path.Combine(AppContext.BaseDirectory, "Files.exe") + ",0");

			using var command = key.CreateSubKey(@"shell\open\command");
			command.SetValue(string.Empty, Command);
		}
	}
}
