// Consysto fork: a build for screenshots. Never shipped: it is turned on by -p:ConsystoDemo=true and by nothing else.

namespace Files.App.Helpers
{
	/// <summary>
	/// Replaces what is on screen about this particular computer — the names of its disks and of the computer itself — with
	/// invented ones, so that pictures for posts show the program rather than its owner's machine.
	///
	/// It changes captions only. Everything the program does, reads and shows about files stays exactly as it is: a picture
	/// taken from this build shows real behaviour, only in an invented setting.
	/// </summary>
	public static class DemoMode
	{
#if CONSYSTO_DEMO
		public const bool IsOn = true;
#else
		public const bool IsOn = false;
#endif

		private static readonly Dictionary<char, string> DriveNames = new()
		{
			['C'] = "Система",
			['D'] = "Работа",
			['E'] = "DVD-дисковод",
			['F'] = "Флешка",
			['G'] = "Флешка",
			['Z'] = "Архив",
		};

		/// <summary>The name of a disk as the picture should show it.</summary>
		public static string DriveText(string text, string path)
		{
			if (!IsOn || string.IsNullOrEmpty(path))
				return text;

			var letter = char.ToUpperInvariant(path[0]);
			if (!DriveNames.TryGetValue(letter, out var name))
				return text;

			// The letter stays: it is part of how a file manager looks, and it gives nothing away
			return $"{name} ({letter}:)";
		}

		/// <summary>The name of the computer as the picture should show it.</summary>
		public static string ComputerName(string name)
			=> IsOn ? "КОМПЬЮТЕР" : name;
	}
}
