// Consysto fork: collects everything needed to look into a problem into one file the user can look through and send.

using System.IO.Compression;
using System.Text;

namespace Files.App.Helpers
{
	/// <summary>
	/// Puts the log and a short description of the computer into a .zip on the desktop. Nothing is sent anywhere: the user
	/// opens the file, sees what is inside and decides for themselves whether to attach it to a bug report.
	/// </summary>
	public static class ErrorReportHelper
	{
		/// <returns>The path of the saved report.</returns>
		public static string Save()
		{
			var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			var target = SystemIO.Path.Combine(desktop, $"ConsystoFiles-report-{DateTime.Now:yyyy-MM-dd-HHmm}.zip");

			using (var archive = ZipFile.Open(target, ZipArchiveMode.Create))
			{
				foreach (var name in new[] { "debug.log", "debug_fulltrust.log" })
				{
					var path = SystemIO.Path.Combine(AppStorage.LocalFolderPath, name);
					if (!SystemIO.File.Exists(path))
						continue;

					// The app keeps writing to the log, so it is copied rather than added from disk directly
					using var source = new SystemIO.FileStream(path, SystemIO.FileMode.Open, SystemIO.FileAccess.Read, SystemIO.FileShare.ReadWrite);
					using var entry = archive.CreateEntry(name).Open();
					source.CopyTo(entry);
				}

				using var about = new SystemIO.StreamWriter(archive.CreateEntry("about.txt").Open(), Encoding.UTF8);
				about.Write(Describe());
			}

			return target;
		}

		private static string Describe()
		{
			var version = AppStorage.PackageVersion;
			var builder = new StringBuilder();
			builder.AppendLine($"Consysto Files {version.Major}.{version.Minor}.{version.Build}.{version.Revision}");
			builder.AppendLine(AppStorage.IsPortable ? "Сборка: портативная" : "Сборка: установленная");
			builder.AppendLine($"Windows: {Environment.OSVersion.Version}");
			builder.AppendLine($"Разрядность: {(Environment.Is64BitOperatingSystem ? "64" : "32")}");
			builder.AppendLine($".NET: {Environment.Version}");
			builder.AppendLine($"Язык: {System.Globalization.CultureInfo.CurrentUICulture.Name}");
			builder.AppendLine($"Ядер: {Environment.ProcessorCount}");
			builder.AppendLine($"Сохранено: {DateTime.Now:yyyy-MM-dd HH:mm}");
			return builder.ToString();
		}
	}
}
