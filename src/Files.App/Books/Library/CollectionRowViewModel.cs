// Consysto fork: one row of a collection page — an item, or an author, series or other group that leads to its items.

using Consysto.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Files.App.Books.Library
{
	public sealed partial class CollectionRowViewModel : ObservableObject
	{
		// Enough for a tile; the list scales it down
		private const uint ThumbnailSize = 256;

		private ImageSource? thumbnail;
		private bool isChecked;
		private bool isSelected;
		private bool isHovered;
		private bool isSelecting;
		private bool thumbnailRequested;

		public CollectionItem? Item { get; init; }

		/// <summary>A group of a facet; the row leads to its items.</summary>
		public CollectionGroup? Group { get; init; }

		/// <summary>The duplicate group of the item, in the duplicates list.</summary>
		public DuplicateGroup? Duplicates { get; init; }

		public CollectionKindInfo? Kind { get; init; }

		public required string Title { get; init; }

		public required string Glyph { get; init; }

		public string? Subtitle { get; init; }

		public string? Detail { get; init; }

		/// <summary>Shown above the first row of a duplicate group.</summary>
		public string? GroupHeader { get; init; }

		public string? MarkExtraLabel { get; init; }

		public string? Badge { get; init; }

		public Visibility SubtitleVisibility => VisibleIf(Subtitle);

		public Visibility DetailVisibility => VisibleIf(Detail);

		public Visibility GroupHeaderVisibility => VisibleIf(GroupHeader);

		public Visibility BadgeVisibility => VisibleIf(Badge);

		public Visibility CheckBoxVisibility => Duplicates is null ? Visibility.Collapsed : Visibility.Visible;

		public Visibility ChevronVisibility => Group is null ? Visibility.Collapsed : Visibility.Visible;

		/// <summary>Photos are square cells, books tall covers, albums square covers with a caption.</summary>
		public double TileWidth => Kind?.Id switch
		{
			CollectionKinds.PhotosId => 168,
			CollectionKinds.BooksId => 128,
			_ => 160,
		};

		public double TileImageHeight => Kind?.Id switch
		{
			CollectionKinds.PhotosId => 168,
			CollectionKinds.BooksId => 192,
			_ => 160,
		};

		/// <summary>A photo speaks for itself; everything else is signed.</summary>
		public Visibility CaptionVisibility => Kind?.Id == CollectionKinds.PhotosId && Item is not null ? Visibility.Collapsed : Visibility.Visible;

		public string? TileSubtitle => Item is not null ? Kind?.Byline(Item) : Subtitle;

		public Visibility TileSubtitleVisibility => CaptionVisibility == Visibility.Visible ? VisibleIf(TileSubtitle) : Visibility.Collapsed;

		/// <summary>The item itself, or for an album, author or year the first of its items that has a picture.</summary>
		private CollectionItem? CoverItem => Item ?? Group?.Items.FirstOrDefault(candidate => candidate.HasCover);

		public double CoverWidth => Item is null ? 32 : 40;

		public double CoverHeight => Item is null ? 32 : 60;

		public Visibility GlyphVisibility => thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>Mirrors the list's selection, so the tick box of the row shows it and can change it.</summary>
		public bool IsSelected
		{
			get => isSelected;
			set
			{
				if (SetProperty(ref isSelected, value))
					OnPropertyChanged(nameof(SelectBoxVisibility));
			}
		}

		public bool IsHovered
		{
			get => isHovered;
			set
			{
				if (SetProperty(ref isHovered, value))
					OnPropertyChanged(nameof(SelectBoxVisibility));
			}
		}

		/// <summary>Something on the page is selected: every item then shows its tick box, as in Explorer and Google Photos.</summary>
		public bool IsSelecting
		{
			get => isSelecting;
			set
			{
				if (SetProperty(ref isSelecting, value))
					OnPropertyChanged(nameof(SelectBoxVisibility));
			}
		}

		/// <summary>The selection tick box: on hover, on selected items and while anything is selected. Duplicates have their own marks.</summary>
		public Visibility SelectBoxVisibility
			=> Item is not null && Duplicates is null && (isSelected || isHovered || isSelecting) ? Visibility.Visible : Visibility.Collapsed;

		public bool IsChecked
		{
			get => isChecked;
			set => SetProperty(ref isChecked, value);
		}

		public ImageSource? Thumbnail
		{
			get => thumbnail;
			private set
			{
				if (SetProperty(ref thumbnail, value))
					OnPropertyChanged(nameof(GlyphVisibility));
			}
		}

		/// <summary>Called when the row is realized, so a long list reads only the pictures on screen.</summary>
		public async Task LoadThumbnailAsync()
		{
			if (thumbnailRequested || CoverItem is not { HasCover: true } item || Kind?.Thumbnail is not { } thumbnailOf)
				return;

			thumbnailRequested = true;
			try
			{
				if (await thumbnailOf(item, ThumbnailSize) is { } image)
					Thumbnail = await image.ToBitmapAsync();
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "A collection picture could not be shown");
			}
		}

		private static Visibility VisibleIf(string? text)
			=> string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
	}

	/// <summary>Tiles under one heading, e.g. the photos of a month.</summary>
	/// <remarks>Exposed to WinRT like Files' own GroupedCollection: a plain List subclass reaches the GridView as headers without items.</remarks>
	[WinRT.GeneratedWinRTExposedType]
	public sealed partial class CollectionTileGroup(string header, IEnumerable<CollectionRowViewModel> rows) : System.Collections.ObjectModel.ObservableCollection<CollectionRowViewModel>(rows)
	{
		public string Header { get; } = header;
	}
}
