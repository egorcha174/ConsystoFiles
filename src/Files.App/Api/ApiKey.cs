// Consysto fork: the key for the web entrance of the control channel.

using Microsoft.Extensions.Logging;
using System.IO;
using System.Security.Cryptography;

namespace Files.App.Api
{
	/// <summary>
	/// A key made once and kept in the data folder of the program. A caller reads the file and sends what is in it; the folder
	/// belongs to this user, so another person signed in on the machine cannot read it.
	/// </summary>
	public static class ApiKey
	{
		private static readonly Lazy<string> key = new(Read, true);

		public static string Value
			=> key.Value;

		public static string FilePath
			=> Path.Combine(AppStorage.LocalFolderPath, "api-key.txt");

		private static string Read()
		{
			try
			{
				if (File.Exists(FilePath))
				{
					var saved = File.ReadAllText(FilePath).Trim();
					if (saved.Length >= 32)
						return saved;
				}

				var made = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
				Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
				File.WriteAllText(FilePath, made);

				return made;
			}
			catch (Exception ex)
			{
				// Without a file the key still works for as long as the program runs; it simply cannot be looked up from outside
				App.Logger.LogWarning(ex, "The key of the control channel could not be kept");
				return Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
			}
		}
	}
}
