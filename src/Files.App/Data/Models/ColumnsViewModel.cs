// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.UI.Xaml;

namespace Files.App.Data.Models
{
	[WinRT.GeneratedBindableCustomProperty(
		[
			nameof(IconColumn),
			nameof(GitStatusColumn),
			nameof(GitLastCommitDateColumn),
			nameof(GitLastCommitMessageColumn),
			nameof(GitCommitAuthorColumn),
			nameof(GitLastCommitShaColumn),
			nameof(TagColumn),
			nameof(NameColumn),
			nameof(ExtensionColumn),
			nameof(BookAuthorColumn),
			nameof(BookSeriesColumn),
			nameof(CadPartNumberColumn),
			nameof(CadMaterialColumn),
			nameof(CadMassColumn),
			nameof(CadVersionColumn),
			nameof(StatusColumn),
			nameof(DateModifiedColumn),
			nameof(PathColumn),
			nameof(OriginalPathColumn),
			nameof(ItemTypeColumn),
			nameof(DateDeletedColumn),
			nameof(DateCreatedColumn),
			nameof(SizeColumn),
		],
		[])]
	public sealed partial class ColumnsViewModel : ObservableObject
	{
		private DetailsLayoutColumnItem iconColumn = new()
		{
			UserLength = new GridLength(24, GridUnitType.Pixel),
			IsResizable = false,
		};

		[RegistryIgnore]
		public DetailsLayoutColumnItem IconColumn
		{
			get => iconColumn;
			set => SetProperty(ref iconColumn, value);
		}

		private DetailsLayoutColumnItem _GitStatusColumn = new();
		public DetailsLayoutColumnItem GitStatusColumn
		{
			get => _GitStatusColumn;
			set => SetProperty(ref _GitStatusColumn, value);
		}

		private DetailsLayoutColumnItem _GitLastCommitDateColumn = new();
		public DetailsLayoutColumnItem GitLastCommitDateColumn
		{
			get => _GitLastCommitDateColumn;
			set => SetProperty(ref _GitLastCommitDateColumn, value);
		}

		private DetailsLayoutColumnItem _GitLastCommitMessageColumn = new();
		public DetailsLayoutColumnItem GitLastCommitMessageColumn
		{
			get => _GitLastCommitMessageColumn;
			set => SetProperty(ref _GitLastCommitMessageColumn, value);
		}

		private DetailsLayoutColumnItem _GitCommitAuthorColumn = new();
		public DetailsLayoutColumnItem GitCommitAuthorColumn
		{
			get => _GitCommitAuthorColumn;
			set => SetProperty(ref _GitCommitAuthorColumn, value);
		}

		private DetailsLayoutColumnItem _GitLastCommitShaColumn = new();
		public DetailsLayoutColumnItem GitLastCommitShaColumn
		{
			get => _GitLastCommitShaColumn;
			set => SetProperty(ref _GitLastCommitShaColumn, value);
		}

		private DetailsLayoutColumnItem tagColumn = new();
		public DetailsLayoutColumnItem TagColumn
		{
			get => tagColumn;
			set => SetProperty(ref tagColumn, value);
		}

		private DetailsLayoutColumnItem nameColumn = new()
		{
			NormalMaxLength = 1000d
		};
		public DetailsLayoutColumnItem NameColumn
		{
			get => nameColumn;
			set => SetProperty(ref nameColumn, value);
		}

		// Consysto fork: file extension column right after the name, like Commander One
		private DetailsLayoutColumnItem extensionColumn = new()
		{
			// Wide enough for the localized header ("Расширение") next to the sort glyph
			UserLength = new GridLength(90, GridUnitType.Pixel),
			NormalMinLength = 40,
			NormalMaxLength = 200,
		};
		public DetailsLayoutColumnItem ExtensionColumn
		{
			get => extensionColumn;
			set => SetProperty(ref extensionColumn, value);
		}

		// Consysto fork: book author and series after the extension; hidden in folders without books, like the Git columns
		private DetailsLayoutColumnItem bookAuthorColumn = new()
		{
			UserLength = new GridLength(180, GridUnitType.Pixel),
			NormalMaxLength = 500,
			IsHidden = true,
		};
		public DetailsLayoutColumnItem BookAuthorColumn
		{
			get => bookAuthorColumn;
			set => SetProperty(ref bookAuthorColumn, value);
		}

		private DetailsLayoutColumnItem bookSeriesColumn = new()
		{
			UserLength = new GridLength(180, GridUnitType.Pixel),
			NormalMaxLength = 500,
			IsHidden = true,
		};
		public DetailsLayoutColumnItem BookSeriesColumn
		{
			get => bookSeriesColumn;
			set => SetProperty(ref bookSeriesColumn, value);
		}
		private DetailsLayoutColumnItem cadPartNumberColumn = new()
		{
			UserLength = new GridLength(140, GridUnitType.Pixel),
			NormalMaxLength = 500,
			IsHidden = true,
		};
		public DetailsLayoutColumnItem CadPartNumberColumn
		{
			get => cadPartNumberColumn;
			set => SetProperty(ref cadPartNumberColumn, value);
		}
		private DetailsLayoutColumnItem cadMaterialColumn = new()
		{
			UserLength = new GridLength(140, GridUnitType.Pixel),
			NormalMaxLength = 500,
			IsHidden = true,
		};
		public DetailsLayoutColumnItem CadMaterialColumn
		{
			get => cadMaterialColumn;
			set => SetProperty(ref cadMaterialColumn, value);
		}
		private DetailsLayoutColumnItem cadMassColumn = new()
		{
			UserLength = new GridLength(100, GridUnitType.Pixel),
			NormalMaxLength = 500,
			IsHidden = true,
		};
		public DetailsLayoutColumnItem CadMassColumn
		{
			get => cadMassColumn;
			set => SetProperty(ref cadMassColumn, value);
		}
		private DetailsLayoutColumnItem cadVersionColumn = new()
		{
			UserLength = new GridLength(130, GridUnitType.Pixel),
			NormalMaxLength = 500,
			IsHidden = true,
		};
		public DetailsLayoutColumnItem CadVersionColumn
		{
			get => cadVersionColumn;
			set => SetProperty(ref cadVersionColumn, value);
		}

		// Consysto fork: user order of the columns after the name, as comma-separated property names; empty keeps the default order
		private string? columnOrder;
		public string? ColumnOrder
		{
			get => columnOrder;
			set => SetProperty(ref columnOrder, value);
		}

		private DetailsLayoutColumnItem statusColumn = new()
		{
			UserLength = new GridLength(50),
			NormalMaxLength = 80,
		};
		public DetailsLayoutColumnItem StatusColumn
		{
			get => statusColumn;
			set => SetProperty(ref statusColumn, value);
		}

		private DetailsLayoutColumnItem dateModifiedColumn = new();
		public DetailsLayoutColumnItem DateModifiedColumn
		{
			get => dateModifiedColumn;
			set => SetProperty(ref dateModifiedColumn, value);
		}

		private DetailsLayoutColumnItem pathColumn = new()
		{
			NormalMaxLength = 500,
		};
		public DetailsLayoutColumnItem PathColumn
		{
			get => pathColumn;
			set => SetProperty(ref pathColumn, value);
		}

		private DetailsLayoutColumnItem originalPathColumn = new()
		{
			NormalMaxLength = 500,
		};
		public DetailsLayoutColumnItem OriginalPathColumn
		{
			get => originalPathColumn;
			set => SetProperty(ref originalPathColumn, value);
		}

		private DetailsLayoutColumnItem itemTypeColumn = new();
		public DetailsLayoutColumnItem ItemTypeColumn
		{
			get => itemTypeColumn;
			set => SetProperty(ref itemTypeColumn, value);
		}

		private DetailsLayoutColumnItem dateDeletedColumn = new();
		public DetailsLayoutColumnItem DateDeletedColumn
		{
			get => dateDeletedColumn;
			set => SetProperty(ref dateDeletedColumn, value);
		}

		private DetailsLayoutColumnItem dateCreatedColumn = new()
		{
			UserCollapsed = true
		};
		public DetailsLayoutColumnItem DateCreatedColumn
		{
			get => dateCreatedColumn;
			set => SetProperty(ref dateCreatedColumn, value);
		}

		private DetailsLayoutColumnItem sizeColumn = new();
		public DetailsLayoutColumnItem SizeColumn
		{
			get => sizeColumn;
			set => SetProperty(ref sizeColumn, value);
		}

		public double TotalWidth =>
			IconColumn.Length.Value +
			GitStatusColumn.Length.Value +
			GitLastCommitDateColumn.Length.Value +
			GitLastCommitMessageColumn.Length.Value +
			GitCommitAuthorColumn.Length.Value +
			GitLastCommitShaColumn.Length.Value +
			TagColumn.Length.Value +
			NameColumn.Length.Value +
			ExtensionColumn.Length.Value +
			BookAuthorColumn.Length.Value +
			BookSeriesColumn.Length.Value +
			CadPartNumberColumn.Length.Value +
			CadMaterialColumn.Length.Value +
			CadMassColumn.Length.Value +
			CadVersionColumn.Length.Value +
			DateModifiedColumn.Length.Value +
			PathColumn.Length.Value +
			OriginalPathColumn.Length.Value +
			ItemTypeColumn.Length.Value +
			DateDeletedColumn.Length.Value +
			DateCreatedColumn.Length.Value +
			SizeColumn.Length.Value +
			StatusColumn.Length.Value;

		public override bool Equals(object? obj)
		{
			if (obj is null)
				return false;

			if (obj == this)
				return true;

			if (obj is ColumnsViewModel model)
			{
				return
					model.DateCreatedColumn.Equals(DateCreatedColumn) &&
					model.DateDeletedColumn.Equals(DateDeletedColumn) &&
					model.DateModifiedColumn.Equals(DateModifiedColumn) &&
					model.ItemTypeColumn.Equals(ItemTypeColumn) &&
					model.NameColumn.Equals(NameColumn) &&
					model.ExtensionColumn.Equals(ExtensionColumn) &&
					model.BookAuthorColumn.Equals(BookAuthorColumn) &&
					model.BookSeriesColumn.Equals(BookSeriesColumn) &&
					model.CadPartNumberColumn.Equals(CadPartNumberColumn) &&
					model.CadMaterialColumn.Equals(CadMaterialColumn) &&
					model.CadMassColumn.Equals(CadMassColumn) &&
					model.CadVersionColumn.Equals(CadVersionColumn) &&
					string.Equals(model.ColumnOrder, ColumnOrder, StringComparison.Ordinal) &&
					model.PathColumn.Equals(PathColumn) &&
					model.OriginalPathColumn.Equals(OriginalPathColumn) &&
					model.SizeColumn.Equals(SizeColumn) &&
					model.StatusColumn.Equals(StatusColumn) &&
					model.TagColumn.Equals(TagColumn) &&
					model.GitStatusColumn.Equals(GitStatusColumn) &&
					model.GitLastCommitDateColumn.Equals(GitLastCommitDateColumn) &&
					model.GitLastCommitMessageColumn.Equals(GitLastCommitMessageColumn) &&
					model.GitCommitAuthorColumn.Equals(GitCommitAuthorColumn) &&
					model.GitLastCommitShaColumn.Equals(GitLastCommitShaColumn);
			}

			return base.Equals(obj);
		}

		public override int GetHashCode()
		{
			var hashCode = DateCreatedColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ DateDeletedColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ DateModifiedColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ ItemTypeColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ NameColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ ExtensionColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ BookAuthorColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ BookSeriesColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ CadPartNumberColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ CadMaterialColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ CadMassColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ CadVersionColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ (ColumnOrder?.GetHashCode(StringComparison.Ordinal) ?? 0);
			hashCode = (hashCode * 397) ^ PathColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ OriginalPathColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ SizeColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ StatusColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ TagColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ GitStatusColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ GitLastCommitDateColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ GitLastCommitMessageColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ GitCommitAuthorColumn.GetHashCode();
			hashCode = (hashCode * 397) ^ GitLastCommitShaColumn.GetHashCode();

			return hashCode;
		}
	}
}
