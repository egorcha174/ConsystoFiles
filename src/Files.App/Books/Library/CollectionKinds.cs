// Consysto fork: the kinds of collections Files knows, and how the collection page shows each of them.

using System.Globalization;
using Consysto.BookPreview.Library;
using Consysto.CadPreview.Library;
using Consysto.CadPreview.WinUI;
using Consysto.Collections;
using Files.App.Cad;
using Consysto.MediaPreview.Music;
using Consysto.MediaPreview.Photos;

namespace Files.App.Books.Library
{
	public sealed record CollectionFacetInfo(string Id, string Title, string Glyph);

	/// <summary>A kind of collection as the user sees it: its names, icons, sections and the lines shown for an item.</summary>
	public sealed class CollectionKindInfo
	{
		public required string Id { get; init; }

		/// <summary>"Books": the kind in lists, and the default name of a new collection.</summary>
		public required string Name { get; init; }

		/// <summary>Icon of a collection in the sidebar, the tab and settings.</summary>
		public required string Glyph { get; init; }

		/// <summary>Icon of an item that has no picture.</summary>
		public required string ItemGlyph { get; init; }

		public required string AllTitle { get; init; }

		/// <summary>"Books: {0}".</summary>
		public required string CountFormat { get; init; }

		public required string SearchPlaceholder { get; init; }

		/// <summary>A group of copies of the same item in different files, e.g. "the same book, different files".</summary>
		public required string SameItemLabel { get; init; }

		public required Func<CollectionKind> Create { get; init; }

		public IReadOnlyList<CollectionFacetInfo> Facets { get; init; } = [];

		public required Func<CollectionItem, string> Subtitle { get; init; }

		/// <summary>The people behind an item: authors of a book, the designer of a drawing.</summary>
		public Func<CollectionItem, string?> Byline { get; init; } = _ => null;

		public Func<CollectionItem, IEnumerable<string?>> Details { get; init; } = _ => [];

		public Func<CollectionItem, string?> Description { get; init; } = _ => null;

		/// <summary>An encoded picture of the item at about the given size; null when there is none.</summary>
		public Func<CollectionItem, uint, Task<byte[]?>>? Thumbnail { get; init; }

		/// <summary>The collection can be shared with a phone as an OPDS catalog.</summary>
		public bool CanShare { get; init; }

		/// <summary>The folder offered for a new collection of this kind, before one is picked.</summary>
		public Environment.SpecialFolder? SuggestedFolder { get; init; }
	}

	public static class CollectionKinds
	{
		public const string BooksId = "books";
		public const string PhotosId = "photos";
		public const string MusicId = "music";
		public const string DrawingsId = "drawings";

		private static readonly Lazy<IReadOnlyList<CollectionKindInfo>> all = new(() => [CreateBooks(), CreatePhotos(), CreateMusic(), CreateDrawings()]);

		public static IReadOnlyList<CollectionKindInfo> All => all.Value;

		public static CollectionKindInfo? Find(string? id)
			=> All.FirstOrDefault(kind => kind.Id == id);

		public static string FormatName(CollectionItem item)
			=> item.Extension.TrimStart('.').ToUpperInvariant();

		private static CollectionKindInfo CreateBooks()
		{
			var seriesFormat = Strings.ConsystoBookSeriesNumber.GetLocalizedResource();

			string? SeriesOf(CollectionItem book)
				=> book.Series is not { } series ? null : book.SeriesIndex is { } index ? string.Format(seriesFormat, series, index) : series;

			return new()
			{
				Id = BooksId,
				Name = Strings.ConsystoKindBooks.GetLocalizedResource(),
				Glyph = FluentGlyphs.Library,
				ItemGlyph = FluentGlyphs.Book,
				AllTitle = Strings.ConsystoLibraryAll.GetLocalizedResource(),
				CountFormat = Strings.ConsystoLibraryGroupBooks.GetLocalizedResource(),
				SearchPlaceholder = Strings.ConsystoLibrarySearchPlaceholder.GetLocalizedResource(),
				SameItemLabel = Strings.ConsystoLibrarySameBook.GetLocalizedResource(),
				Create = () => new BookCollection(hostDrawsPdfCovers: true),
				Facets =
				[
					new(BookFields.Authors, Strings.ConsystoLibraryAuthors.GetLocalizedResource(), FluentGlyphs.Contact),
					new(BookFields.Series, Strings.ConsystoLibrarySeries.GetLocalizedResource(), FluentGlyphs.List),
					new(BookFields.Genres, Strings.ConsystoLibraryGenres.GetLocalizedResource(), FluentGlyphs.Tag),
				],
				Subtitle = book => string.Join(" · ", new[] { string.Join(", ", book.Authors), SeriesOf(book), FormatName(book) }.Where(part => !string.IsNullOrEmpty(part))),
				Byline = book => book.Authors.Count > 0 ? string.Join(", ", book.Authors) : null,
				Details = book => [SeriesOf(book), book.Year, book.Language, book.Publisher, string.Join(", ", book.Genres.Take(3)), $"{FormatName(book)}, {book.Size.ToSizeString()}"],
				Description = book => book.Annotation,
				Thumbnail = (book, size) => BookThumbnailService.IsSupported(book.Path) ? BookThumbnailService.GetThumbnailAsync(book.Path, size) : Task.FromResult<byte[]?>(null),
				CanShare = true,
				SuggestedFolder = Environment.SpecialFolder.MyDocuments,
			};
		}

		private static CollectionKindInfo CreatePhotos()
		{
			string? TakenOf(CollectionItem photo)
				=> DateTime.TryParseExact(photo.Value(PhotoFields.Taken), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var taken)
					? taken.ToString("g", CultureInfo.CurrentCulture)
					: null;

			return new()
			{
				Id = PhotosId,
				Name = Strings.ConsystoKindPhotos.GetLocalizedResource(),
				Glyph = FluentGlyphs.Photo,
				ItemGlyph = FluentGlyphs.Photo,
				AllTitle = Strings.ConsystoPhotosAll.GetLocalizedResource(),
				CountFormat = Strings.ConsystoPhotosCount.GetLocalizedResource(),
				SearchPlaceholder = Strings.ConsystoPhotosSearchPlaceholder.GetLocalizedResource(),
				SameItemLabel = Strings.ConsystoLibraryIdenticalFiles.GetLocalizedResource(),
				Create = () => new PhotoCollection(),
				Facets =
				[
					new(PhotoFields.Year, Strings.ConsystoCollectionYears.GetLocalizedResource(), FluentGlyphs.Calendar),
					new(PhotoFields.Camera, Strings.ConsystoPhotosCameras.GetLocalizedResource(), FluentGlyphs.Camera),
				],
				Subtitle = photo => Join(TakenOf(photo), photo.Value(PhotoFields.Camera), photo.Value(PhotoFields.Size)),
				Details = photo => [TakenOf(photo), photo.Value(PhotoFields.Camera), photo.Value(PhotoFields.Size), photo.Value(PhotoFields.Location), $"{FormatName(photo)}, {photo.Size.ToSizeString()}"],
				// The shell draws photo thumbnails, and caches them, for every format Windows can open
				Thumbnail = (photo, size) => FileThumbnailHelper.GetIconAsync(photo.Path, size, false, IconOptions.ReturnThumbnailOnly),
				SuggestedFolder = Environment.SpecialFolder.MyPictures,
			};
		}

		private static CollectionKindInfo CreateMusic()
		{
			var trackFormat = Strings.ConsystoMusicTrack.GetLocalizedResource();

			string? ArtistsOf(CollectionItem track)
				=> track.Values(MusicFields.Artists) is { Count: > 0 } artists ? string.Join(", ", artists) : null;

			return new()
			{
				Id = MusicId,
				Name = Strings.ConsystoKindMusic.GetLocalizedResource(),
				Glyph = FluentGlyphs.Audio,
				ItemGlyph = FluentGlyphs.Audio,
				AllTitle = Strings.ConsystoMusicAll.GetLocalizedResource(),
				CountFormat = Strings.ConsystoMusicCount.GetLocalizedResource(),
				SearchPlaceholder = Strings.ConsystoMusicSearchPlaceholder.GetLocalizedResource(),
				SameItemLabel = Strings.ConsystoMusicSameSong.GetLocalizedResource(),
				Create = () => new MusicCollection(),
				Facets =
				[
					new(MusicFields.Artists, Strings.ConsystoMusicArtists.GetLocalizedResource(), FluentGlyphs.Contact),
					new(MusicFields.Album, Strings.ConsystoMusicAlbums.GetLocalizedResource(), FluentGlyphs.Album),
					new(MusicFields.Genres, Strings.ConsystoLibraryGenres.GetLocalizedResource(), FluentGlyphs.Tag),
					new(MusicFields.Year, Strings.ConsystoCollectionYears.GetLocalizedResource(), FluentGlyphs.Calendar),
				],
				Subtitle = track => Join(ArtistsOf(track), track.Value(MusicFields.Album), DurationOf(track), FormatName(track)),
				Byline = ArtistsOf,
				Details = track =>
				[
					track.Value(MusicFields.Album),
					track.Value(MusicFields.Track) is { } number ? string.Format(trackFormat, number) : null,
					track.Value(MusicFields.Year),
					string.Join(", ", track.Values(MusicFields.Genres)),
					DurationOf(track),
					$"{FormatName(track)}, {track.Size.ToSizeString()}",
				],
				Thumbnail = MusicCoverAsync,
				SuggestedFolder = Environment.SpecialFolder.MyMusic,
			};
		}

		private static CollectionKindInfo CreateDrawings()
		{
			var partsFormat = Strings.ConsystoDrawingsParts.GetLocalizedResource();

			string? PartsOf(CollectionItem drawing)
				=> drawing.Value(DrawingFields.Parts) is { } parts ? string.Format(partsFormat, parts) : null;

			var massFormat = Strings.ConsystoIPropertyMassValue.GetLocalizedResource();

			// Stored in kilograms; three significant digits, so a small part reads 0,012 rather than 0
			string? MassOf(CollectionItem drawing)
			{
				if (!double.TryParse(drawing.Value(DrawingFields.Mass), NumberStyles.Float, CultureInfo.InvariantCulture, out var kilograms) || kilograms <= 0)
					return null;
				var decimals = kilograms >= 100 ? 1 : Math.Clamp(2 - (int)Math.Floor(Math.Log10(kilograms)), 0, 6);
				return string.Format(massFormat, Math.Round(kilograms, decimals).ToString("0." + new string('#', Math.Max(decimals, 1)), CultureInfo.CurrentCulture));
			}

			return new()
			{
				Id = DrawingsId,
				Name = Strings.ConsystoKindDrawings.GetLocalizedResource(),
				Glyph = FluentGlyphs.Drawing,
				ItemGlyph = FluentGlyphs.Drawing,
				AllTitle = Strings.ConsystoDrawingsAll.GetLocalizedResource(),
				CountFormat = Strings.ConsystoDrawingsCount.GetLocalizedResource(),
				SearchPlaceholder = Strings.ConsystoDrawingsSearchPlaceholder.GetLocalizedResource(),
				SameItemLabel = Strings.ConsystoLibraryIdenticalFiles.GetLocalizedResource(),
				Create = () => new DrawingCollection(),
				Facets =
				[
					new(DrawingFields.Kind, Strings.ConsystoDrawingsKinds.GetLocalizedResource(), FluentGlyphs.Tag),
					new(DrawingFields.Format, Strings.ConsystoDrawingsFormats.GetLocalizedResource(), FluentGlyphs.List),
					new(DrawingFields.Folder, Strings.ConsystoDrawingsFolders.GetLocalizedResource(), FluentGlyphs.Library),
					new(DrawingFields.Material, Strings.ConsystoDrawingsMaterials.GetLocalizedResource(), FluentGlyphs.Tiles),
					new(DrawingFields.Designer, Strings.ConsystoDrawingsDesigners.GetLocalizedResource(), FluentGlyphs.Contact),
					new(DrawingFields.Project, Strings.ConsystoDrawingsProjects.GetLocalizedResource(), FluentGlyphs.Catalog),
				],
				Subtitle = drawing => Join(drawing.Value(DrawingFields.PartNumber), drawing.Value(DrawingFields.Kind), drawing.Value(DrawingFields.Material), PartsOf(drawing), drawing.Value(DrawingFields.Folder)),
				Byline = drawing => drawing.Value(DrawingFields.Description),
				Details = drawing =>
				[
					drawing.Value(DrawingFields.PartNumber),
					drawing.Value(DrawingFields.Description),
					drawing.Value(DrawingFields.Kind),
					drawing.Value(DrawingFields.Material),
					MassOf(drawing),
					drawing.Value(DrawingFields.Designer),
					PartsOf(drawing),
					drawing.Value(DrawingFields.Folder),
					$"{FormatName(drawing)}, {drawing.Size.ToSizeString()}",
				],
				// The shell has no handler for DWG/DXF, so the CAD core draws the picture itself
				Thumbnail = (drawing, size) => CadThumbnailService.GetThumbnailAsync(drawing.Path, size),
				SuggestedFolder = Environment.SpecialFolder.MyDocuments,
			};
		}

		/// <summary>The cover stored in the file, scaled like book covers.</summary>
		private static async Task<byte[]?> MusicCoverAsync(CollectionItem track, uint size)
			=> await Task.Run(() => MusicCollection.ReadCover(track.Path)) is { } cover
				? await CadThumbnailRenderer.RenderImageAsync(cover, (int)size)
				: null;

		/// <summary>"3:07", or "1:02:15" for an hour and longer.</summary>
		private static string? DurationOf(CollectionItem track)
			=> int.TryParse(track.Value(MusicFields.Duration), out var seconds) && seconds > 0
				? TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture)
				: null;

		private static string Join(params string?[] parts)
			=> string.Join(" · ", parts.Where(part => !string.IsNullOrEmpty(part)));
	}
}
