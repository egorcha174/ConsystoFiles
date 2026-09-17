// Consysto fork: the columns after the name can be rearranged by dragging their headers.

using System.ComponentModel;
using Files.App.UserControls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Files.App.Views.Layouts
{
	// The XAML keeps every column in its default slot, so sorting, splitters and fit-to-content keep their fixed column indexes.
	// A column the user moved is drawn at its new place by a horizontal render offset on its header, its splitter and its row cells.
	public sealed partial class DetailsLayoutPage
	{
		private const int FirstMovableHeaderColumn = 4;
		private const double ColumnDragThreshold = 6;

		// Movable columns in their default order: the key saved in ColumnsViewModel.ColumnOrder and the name of the row cell
		private static readonly (string Key, string CellName)[] MovableColumnNames =
		[
			("ExtensionColumn", "ItemExtensionTextBlock"),
			("BookAuthorColumn", "ItemBookAuthorTextBlock"),
			("BookSeriesColumn", "ItemBookSeriesTextBlock"),
			("CadPartNumberColumn", "ItemCadPartNumberTextBlock"),
			("CadMaterialColumn", "ItemCadMaterialTextBlock"),
			("CadMassColumn", "ItemCadMassTextBlock"),
			("CadVersionColumn", "ItemCadVersionTextBlock"),
			("GitStatusColumn", "ItemGitStatusTextBlock"),
			("GitLastCommitDateColumn", "ItemGitLastCommitDateTextBlock"),
			("GitLastCommitMessageColumn", "ItemGitLastCommitMessageTextBlock"),
			("GitCommitAuthorColumn", "ItemGitCommitAuthorTextBlock"),
			("GitLastCommitShaColumn", "ItemGitLastCommitShaTextBlock"),
			("TagColumn", "ItemTagGrid"),
			("PathColumn", "ItemPathTextBlock"),
			("OriginalPathColumn", "ItemOriginalPath"),
			("DateDeletedColumn", "ItemDateDeleted"),
			("DateModifiedColumn", "ItemDateModifiedTextBlock"),
			("DateCreatedColumn", "ItemDateCreatedTextBlock"),
			("ItemTypeColumn", "ItemTypeTextBlock"),
			("SizeColumn", "ItemSize"),
			("StatusColumn", "ItemStatus"),
		];

		private static readonly Dictionary<string, int> MovableCellIndexes =
			MovableColumnNames.Select((column, index) => (column.CellName, index)).ToDictionary(pair => pair.CellName, pair => pair.index);

		private readonly double[] columnOffsets = new double[MovableColumnNames.Length];
		private bool isColumnOffsetsUpdateQueued;

		private DataGridHeader? pressedColumnHeader;
		private int pressedColumnIndex = -1;
		private uint pressedColumnPointerId;
		private double pressedColumnX;
		private double columnDragDelta;
		private bool isDraggingColumn;
		private bool suppressSortAfterColumnDrag;

		private DetailsLayoutColumnItem[] MovableColumns =>
		[
			ColumnsViewModel.ExtensionColumn,
			ColumnsViewModel.BookAuthorColumn,
			ColumnsViewModel.BookSeriesColumn,
			ColumnsViewModel.CadPartNumberColumn,
			ColumnsViewModel.CadMaterialColumn,
			ColumnsViewModel.CadMassColumn,
			ColumnsViewModel.CadVersionColumn,
			ColumnsViewModel.GitStatusColumn,
			ColumnsViewModel.GitLastCommitDateColumn,
			ColumnsViewModel.GitLastCommitMessageColumn,
			ColumnsViewModel.GitCommitAuthorColumn,
			ColumnsViewModel.GitLastCommitShaColumn,
			ColumnsViewModel.TagColumn,
			ColumnsViewModel.PathColumn,
			ColumnsViewModel.OriginalPathColumn,
			ColumnsViewModel.DateDeletedColumn,
			ColumnsViewModel.DateModifiedColumn,
			ColumnsViewModel.DateCreatedColumn,
			ColumnsViewModel.ItemTypeColumn,
			ColumnsViewModel.SizeColumn,
			ColumnsViewModel.StatusColumn,
		];

		private void InitializeColumnReordering()
		{
			// Each movable header listens itself, handled events included: its inner button captures the pointer and handles input.
			// Handlers on the header grid alone never saw the moves.
			foreach (var child in HeaderGrid.Children)
			{
				if (child is not DataGridHeader header)
					continue;

				var gridColumn = Grid.GetColumn(header);
				if (gridColumn < FirstMovableHeaderColumn || (gridColumn - FirstMovableHeaderColumn) % 2 != 0)
					continue;

				header.AddHandler(PointerPressedEvent, new PointerEventHandler(HeaderGrid_ColumnPointerPressed), true);
				header.AddHandler(PointerMovedEvent, new PointerEventHandler(HeaderGrid_ColumnPointerMoved), true);
				header.AddHandler(PointerReleasedEvent, new PointerEventHandler(HeaderGrid_ColumnPointerReleased), true);
				header.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(HeaderGrid_ColumnPointerCaptureLost), true);
			}

			foreach (var column in MovableColumns)
				column.PropertyChanged += MovableColumn_PropertyChanged;

			ColumnsViewModel.PropertyChanged += ColumnsViewModel_PropertyChanged;
		}

		private void MovableColumn_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(DetailsLayoutColumnItem.LengthIncludingGridSplitterPixels))
				QueueColumnOffsetsUpdate();
		}

		private void ColumnsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(ColumnsViewModel.ColumnOrder))
				QueueColumnOffsetsUpdate();
		}

		/// <summary>Default indexes of the movable columns in the order the user arranged them.</summary>
		private List<int> GetColumnDisplayOrder()
		{
			var order = new List<int>();
			foreach (var key in (ColumnsViewModel.ColumnOrder ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var index = Array.FindIndex(MovableColumnNames, column => column.Key == key);
				if (index >= 0 && !order.Contains(index))
					order.Add(index);
			}

			// A column missing from a saved order (added by a later version) goes right after its default predecessor
			for (var index = 0; index < MovableColumnNames.Length; index++)
			{
				if (!order.Contains(index))
					order.Insert(index == 0 ? 0 : order.IndexOf(index - 1) + 1, index);
			}

			return order;
		}

		private void SetColumnDisplayOrder(List<int> order)
		{
			var isDefaultOrder = order.SequenceEqual(Enumerable.Range(0, MovableColumnNames.Length));
			ColumnsViewModel.ColumnOrder = isDefaultOrder ? null : string.Join(',', order.Select(index => MovableColumnNames[index].Key));
			UpdateColumnOffsets();

			if (FolderSettings is { } folderSettings)
				folderSettings.ColumnsViewModel = ColumnsViewModel;
		}

		private void ResetColumnOrder_Click(object sender, RoutedEventArgs e)
			=> SetColumnDisplayOrder([.. Enumerable.Range(0, MovableColumnNames.Length)]);

		private void QueueColumnOffsetsUpdate()
		{
			if (isColumnOffsetsUpdateQueued)
				return;

			isColumnOffsetsUpdateQueued = true;
			DispatcherQueue.TryEnqueue(UpdateColumnOffsets);
		}

		[WinRT.DynamicWindowsRuntimeCast(typeof(SelectorItem))]
		private void UpdateColumnOffsets()
		{
			isColumnOffsetsUpdateQueued = false;

			var columns = MovableColumns;
			var defaultLefts = new double[columns.Length];
			var left = 0d;
			for (var index = 0; index < columns.Length; index++)
			{
				defaultLefts[index] = left;
				left += columns[index].LengthIncludingGridSplitterPixels;
			}

			left = 0d;
			foreach (var index in GetColumnDisplayOrder())
			{
				columnOffsets[index] = left - defaultLefts[index];
				left += columns[index].LengthIncludingGridSplitterPixels;
			}

			ApplyHeaderOffsets();

			if (FileList?.ItemsPanelRoot is { } panel)
			{
				foreach (var child in panel.Children)
				{
					if (child is SelectorItem container)
						ApplyRowOffsets(container);
				}
			}
		}

		[WinRT.DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void ApplyHeaderOffsets()
		{
			foreach (var child in HeaderGrid.Children)
			{
				if (child is not FrameworkElement element || element == ColumnDropIndicator)
					continue;

				var gridColumn = Grid.GetColumn(element);
				if (gridColumn < FirstMovableHeaderColumn)
					continue;

				var index = (gridColumn - FirstMovableHeaderColumn) / 2;
				if (index >= columnOffsets.Length)
					continue;

				var dragShift = isDraggingColumn && element == pressedColumnHeader ? columnDragDelta : 0;
				SetHorizontalOffset(element, columnOffsets[index] + dragShift);
			}
		}

		[WinRT.DynamicWindowsRuntimeCast(typeof(UserControl))]
		[WinRT.DynamicWindowsRuntimeCast(typeof(Microsoft.UI.Xaml.Controls.Panel))]
		[WinRT.DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void ApplyRowOffsets(SelectorItem container)
		{
			if (container.ContentTemplateRoot is not UserControl { Content: Microsoft.UI.Xaml.Controls.Panel row })
				return;

			foreach (var child in row.Children)
			{
				if (child is FrameworkElement element && MovableCellIndexes.TryGetValue(element.Name, out var index))
					SetHorizontalOffset(element, columnOffsets[index]);
			}
		}

		[WinRT.DynamicWindowsRuntimeCast(typeof(TranslateTransform))]
		private static void SetHorizontalOffset(UIElement element, double offset)
		{
			if (element.RenderTransform is TranslateTransform translate)
				translate.X = offset;
			else if (offset != 0)
				element.RenderTransform = new TranslateTransform { X = offset };
		}

		[WinRT.DynamicWindowsRuntimeCast(typeof(DataGridHeader))]
		[WinRT.DynamicWindowsRuntimeCast(typeof(Files.App.Controls.GridSplitter))]
		private DataGridHeader? FindColumnHeader(DependencyObject? element)
		{
			while (element is not null && element != HeaderGrid)
			{
				if (element is DataGridHeader header)
					return header;

				if (element is Files.App.Controls.GridSplitter)
					return null;

				element = VisualTreeHelper.GetParent(element);
			}

			return null;
		}

		[WinRT.DynamicWindowsRuntimeCast(typeof(DataGridHeader))]
		private void HeaderGrid_ColumnPointerPressed(object sender, PointerRoutedEventArgs e)
		{
			ResetColumnDrag();

			var point = e.GetCurrentPoint(HeaderGrid);
			if (!point.Properties.IsLeftButtonPressed || sender is not DataGridHeader header)
				return;

			var gridColumn = Grid.GetColumn(header);
			pressedColumnHeader = header;
			pressedColumnIndex = (gridColumn - FirstMovableHeaderColumn) / 2;
			pressedColumnPointerId = e.Pointer.PointerId;
			pressedColumnX = point.Position.X;
		}

		private void HeaderGrid_ColumnPointerMoved(object sender, PointerRoutedEventArgs e)
		{
			if (pressedColumnHeader is null || e.Pointer.PointerId != pressedColumnPointerId)
				return;

			var x = e.GetCurrentPoint(HeaderGrid).Position.X;
			if (!isDraggingColumn)
			{
				if (Math.Abs(x - pressedColumnX) < ColumnDragThreshold)
					return;

				isDraggingColumn = true;
				pressedColumnHeader.Opacity = 0.6;
				Canvas.SetZIndex(pressedColumnHeader, 10);

				// Take the pointer from the inner button: moves keep coming outside the header, and the button does not click
				pressedColumnHeader.CapturePointer(e.Pointer);
			}

			columnDragDelta = x - pressedColumnX;
			ApplyHeaderOffsets();
			ShowColumnDropIndicator(GetColumnDropPosition(x));
			e.Handled = true;
		}

		private void HeaderGrid_ColumnPointerReleased(object sender, PointerRoutedEventArgs e)
		{
			if (pressedColumnHeader is null || e.Pointer.PointerId != pressedColumnPointerId)
				return;

			if (!isDraggingColumn)
			{
				ResetColumnDrag();
				return;
			}

			var order = GetColumnDisplayOrder();
			var dropPosition = GetColumnDropPosition(e.GetCurrentPoint(HeaderGrid).Position.X);
			var draggedIndex = pressedColumnIndex;
			var fromPosition = order.IndexOf(draggedIndex);

			pressedColumnHeader.ReleasePointerCapture(e.Pointer);
			ResetColumnDrag();
			e.Handled = true;

			order.RemoveAt(fromPosition);
			order.Insert(dropPosition > fromPosition ? dropPosition - 1 : dropPosition, draggedIndex);
			SetColumnDisplayOrder(order);
		}

		private void HeaderGrid_ColumnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
		{
			// Taking the capture from the inner button raises its capture loss here too; only the header's own loss cancels the drag
			if (pressedColumnHeader is null || e.Pointer.PointerId != pressedColumnPointerId)
				return;

			if (isDraggingColumn && !ReferenceEquals(e.OriginalSource, sender))
				return;

			ResetColumnDrag();
		}

		private void ResetColumnDrag()
		{
			if (isDraggingColumn)
			{
				// The header button raises its click on release; that click must not also change the sorting
				suppressSortAfterColumnDrag = true;
				DispatcherQueue.TryEnqueue(() => suppressSortAfterColumnDrag = false);
			}

			if (pressedColumnHeader is not null)
			{
				pressedColumnHeader.Opacity = 1;
				Canvas.SetZIndex(pressedColumnHeader, 0);
			}

			var wasDragging = isDraggingColumn;
			pressedColumnHeader = null;
			pressedColumnIndex = -1;
			isDraggingColumn = false;
			columnDragDelta = 0;
			ColumnDropIndicator.Visibility = Visibility.Collapsed;

			if (wasDragging)
				ApplyHeaderOffsets();
		}

		private double GetMovableColumnsLeft()
		{
			var left = HeaderGrid.Padding.Left;
			for (var index = 0; index < FirstMovableHeaderColumn && index < HeaderGrid.ColumnDefinitions.Count; index++)
				left += HeaderGrid.ColumnDefinitions[index].ActualWidth;

			return left;
		}

		/// <summary>Position in the display order before which the dragged column would be inserted.</summary>
		private int GetColumnDropPosition(double x)
		{
			var columns = MovableColumns;
			var order = GetColumnDisplayOrder();
			var left = GetMovableColumnsLeft();
			for (var position = 0; position < order.Count; position++)
			{
				var width = columns[order[position]].LengthIncludingGridSplitterPixels;
				if (width > 0 && x < left + width / 2)
					return position;

				left += width;
			}

			return order.Count;
		}

		private void ShowColumnDropIndicator(int position)
		{
			var columns = MovableColumns;
			var order = GetColumnDisplayOrder();
			var left = GetMovableColumnsLeft();
			for (var index = 0; index < position && index < order.Count; index++)
				left += columns[order[index]].LengthIncludingGridSplitterPixels;

			// The indicator spans the grid from its first column, which starts after the padding
			ColumnDropIndicatorTransform.X = left - HeaderGrid.Padding.Left - 1;
			ColumnDropIndicator.Visibility = Visibility.Visible;
		}
	}
}
