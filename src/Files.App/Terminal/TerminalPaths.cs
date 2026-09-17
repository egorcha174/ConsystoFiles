// Consysto fork: tab paths of terminal pages.

using System.IO;

namespace Files.App.Terminal
{
	/// <summary>A terminal opens in a tab under the path "Terminal:&lt;folder&gt;", like an OPDS catalog; the folder is where the shell starts.</summary>
	public static class TerminalPaths
	{
		public const string Prefix = "Terminal:";

		/// <summary>Command prompt glyph, the same the "Open in Terminal" command uses.</summary>
		public const string Glyph = "";

		public static bool IsTerminalPath(string? path)
			=> path?.StartsWith(Prefix, StringComparison.Ordinal) == true;

		public static string ForFolder(string folder)
			=> Prefix + folder;

		public static string? FolderOf(string? path)
			=> IsTerminalPath(path) ? path![Prefix.Length..] : null;

		public static string TitleOf(string? path)
		{
			var folder = FolderOf(path)?.TrimEnd(Path.DirectorySeparatorChar);
			var name = string.IsNullOrEmpty(folder) ? null : Path.GetFileName(folder) is { Length: > 0 } leaf ? leaf : folder;
			return name is null
				? Strings.ConsystoTerminal.GetLocalizedResource()
				: $"{Strings.ConsystoTerminal.GetLocalizedResource()} — {name}";
		}
	}
}
