// Consysto fork: tab paths of OPDS catalog pages.

namespace Files.App.Books.Opds
{
	/// <summary>
	/// A catalog page lives in a tab under the path "Opds:&lt;catalog id&gt;", like "Settings" or "ReleaseNotes";
	/// the page of the catalog being shown travels separately in <see cref="NavigationArguments.ConsystoPageAddress"/>.
	/// </summary>
	public static class OpdsPaths
	{
		public const string Prefix = "Opds:";

		/// <summary>Sidebar item that adds a catalog instead of opening one.</summary>
		public const string AddCatalogPath = "Opds:+";

		public static bool IsOpdsPath(string? path)
			=> path?.StartsWith(Prefix, StringComparison.Ordinal) == true;

		/// <summary>Sidebar items that run an action instead of opening a page.</summary>
		public static bool IsActionPath(string? path)
			=> path == AddCatalogPath;

		public static string ForCatalog(string id)
			=> Prefix + id;

		public static string? CatalogId(string? path)
			=> IsOpdsPath(path) ? path![Prefix.Length..] : null;
	}
}
