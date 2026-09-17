// Consysto fork: tab paths of collection pages, and of the fork's own pages in general.

using Files.App.Books.Opds;

namespace Files.App.Books.Library
{
	/// <summary>A library with a kind opens in a tab under the path "Collection:&lt;library name&gt;", like "Settings" or an OPDS catalog.</summary>
	public static class CollectionPaths
	{
		public const string Prefix = "Collection:";

		/// <summary>Sidebar item at the end of the Libraries section that creates a library.</summary>
		public const string AddCollectionPath = "Collection:+";

		public static bool IsCollectionPath(string? path)
			=> path?.StartsWith(Prefix, StringComparison.Ordinal) == true;

		public static string ForCollection(string id)
			=> Prefix + id;

		public static string? CollectionId(string? path)
			=> IsCollectionPath(path) ? path![Prefix.Length..] : null;
	}

	/// <summary>The fork's pages that are not folders — collections and OPDS catalogs — as the shell and the sidebar see them.</summary>
	public static class ConsystoPages
	{
		public static bool IsPagePath(string? path)
			=> CollectionPaths.IsCollectionPath(path) || OpdsPaths.IsOpdsPath(path) || Files.App.Terminal.TerminalPaths.IsTerminalPath(path) || Files.App.Torrents.TorrentPaths.IsTorrentsPath(path) || Files.App.Sync.SyncPaths.IsSyncPath(path);

		public static Type PageTypeOf(string path)
			=> CollectionPaths.IsCollectionPath(path) ? typeof(CollectionPage)
				: Files.App.Terminal.TerminalPaths.IsTerminalPath(path) ? typeof(Files.App.Terminal.TerminalPage)
				: Files.App.Torrents.TorrentPaths.IsTorrentsPath(path) ? typeof(Files.App.Torrents.TorrentsPage)
				: Files.App.Sync.SyncPaths.IsSyncPath(path) ? typeof(Files.App.Sync.SyncPage)
				: typeof(OpdsPage);

		/// <summary>Tab and path bar title.</summary>
		public static string TitleOf(string? path)
			=> CollectionPaths.IsCollectionPath(path) ? CollectionManager.Instance.TitleOf(path)
				: Files.App.Terminal.TerminalPaths.IsTerminalPath(path) ? Files.App.Terminal.TerminalPaths.TitleOf(path)
				: Files.App.Torrents.TorrentPaths.IsTorrentsPath(path) ? Files.App.Torrents.TorrentPaths.TitleOf(path)
				: Files.App.Sync.SyncPaths.IsSyncPath(path) ? Files.App.Sync.SyncManager.Instance.TitleOf(path)
				: OpdsCatalogManager.Instance.TitleOf(path);

		/// <summary>Tab icon.</summary>
		public static string GlyphOf(string? path)
			=> CollectionPaths.IsCollectionPath(path) ? CollectionManager.Instance.GlyphOf(path)
				: Files.App.Terminal.TerminalPaths.IsTerminalPath(path) ? Files.App.Terminal.TerminalPaths.Glyph
				: Files.App.Torrents.TorrentPaths.IsTorrentsPath(path) ? Files.App.Torrents.TorrentPaths.Glyph
				: Files.App.Sync.SyncPaths.IsSyncPath(path) ? Files.App.Sync.SyncPaths.Glyph
				: FluentGlyphs.Catalog;

		/// <summary>The library a collection page belongs to; OPDS catalogs live inside the books page, not in the sidebar.</summary>
		public static INavigationControlItem? SidebarItemOf(string? path)
			=> Files.App.Torrents.TorrentPaths.IsTorrentsPath(path) ? Files.App.Torrents.TorrentSidebar.ItemOf(path)
			: Files.App.Sync.SyncPaths.IsSyncPath(path) ? Files.App.Sync.SyncManager.Instance.SidebarItems.FirstOrDefault(item => item.Path == path)
			: CollectionManager.Instance.Find(path) is { } collection
				? App.LibraryManager.Libraries.FirstOrDefault(library => string.Equals(library.Path, collection.LibraryPath, StringComparison.OrdinalIgnoreCase))
				: null;
	}
}
