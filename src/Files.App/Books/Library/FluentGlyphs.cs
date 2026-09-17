// Consysto fork: Segoe Fluent Icons glyphs used by code-built UI.

namespace Files.App.Books.Library
{
	/// <summary>Built from code points: editors and tools tend to drop private-use characters written into string literals.</summary>
	internal static class FluentGlyphs
	{
		public static readonly string Add = Glyph(0xE710);
		public static readonly string Cancel = Glyph(0xE711);
		public static readonly string More = Glyph(0xE712);
		public static readonly string Settings = Glyph(0xE713);
		public static readonly string Book = Glyph(0xE736);
		public static readonly string Delete = Glyph(0xE74D);
		public static readonly string Contact = Glyph(0xE77B);
		public static readonly string Catalog = Glyph(0xE82D);
		public static readonly string Recent = Glyph(0xE823);
		public static readonly string OpenInNewTab = Glyph(0xE8A7);
		public static readonly string Rename = Glyph(0xE8AC);
		public static readonly string Copy = Glyph(0xE8C8);
		public static readonly string Phone = Glyph(0xE8EA);
		public static readonly string Tag = Glyph(0xE8EC);
		public static readonly string Library = Glyph(0xE8F1);
		public static readonly string List = Glyph(0xE8FD);
		public static readonly string Camera = Glyph(0xE722);
		public static readonly string Calendar = Glyph(0xE787);
		public static readonly string Audio = Glyph(0xE8D6);
		public static readonly string Photo = Glyph(0xE91B);
		public static readonly string Album = Glyph(0xE93C);
		public static readonly string Tiles = Glyph(0xF0E2);
		public static readonly string Drawing = Glyph(0xE8A5);

		private static string Glyph(int codePoint)
			=> ((char)codePoint).ToString();
	}
}
