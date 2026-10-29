// Consysto fork: CAD preview for the info pane.

using Consysto.CadPreview.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Files.App.Cad
{
	public sealed partial class CadPreview : UserControl
	{
		public CadPreview(CadPreviewViewModel model)
		{
			ViewModel = model;
			InitializeComponent();

			// Geometry gets an interactive view; files we cannot draw (DWG solids, Inventor, sliced 3MF) show their embedded image.
			if (model.Drawing is not null)
				Host.Children.Add(new CadDrawingView { Drawing = model.Drawing, MeasureText = MeasureText() });
			else if (model.Mesh is not null)
				ShowMesh(model.Mesh);

			model.PropertyChanged += ViewModel_PropertyChanged;
			Unloaded += CadPreview_Unloaded;
		}

		/// <summary>Where the same details are listed beside the preview, as in the info pane, they are not repeated under it.</summary>
		public void HideProperties()
		{
			PropertiesList.Visibility = Visibility.Collapsed;
		}

		private void ShowMesh(Consysto.CadPreview.Mesh.Mesh3D mesh)
		{
			StoredPicture.Visibility = Visibility.Collapsed;
			Host.Children.Add(new CadMeshView { Mesh = mesh, MeasureText = MeasureText() });
		}

		// The saved picture was shown while the shape was built; now the shape takes its place
		private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName == nameof(CadPreviewViewModel.Mesh) && ViewModel.Mesh is { } mesh)
				ShowMesh(mesh);
		}

		private CadPreviewViewModel ViewModel { get; }

		private static CadMeasureText MeasureText() => new()
		{
			BoundsTool = Strings.ConsystoCadMeasureBounds.GetLocalizedResource(),
			DistanceTool = Strings.ConsystoCadMeasureDistance.GetLocalizedResource(),
			RadiusTool = Strings.ConsystoCadMeasureRadius.GetLocalizedResource(),
			AngleTool = Strings.ConsystoCadMeasureAngle.GetLocalizedResource(),
			Clear = Strings.ConsystoCadMeasureClear.GetLocalizedResource(),
			Millimeters = Strings.ConsystoCadMeasureMillimeters.GetLocalizedResource(),
			BoundsFormat = Strings.ConsystoCadMeasureBoundsFormat.GetLocalizedResource(),
			DistanceFormat = Strings.ConsystoCadMeasureDistanceFormat.GetLocalizedResource(),
			CentresFormat = Strings.ConsystoCadMeasureCentresFormat.GetLocalizedResource(),
			RadiusFormat = Strings.ConsystoCadMeasureRadiusFormat.GetLocalizedResource(),
			AngleFormat = Strings.ConsystoCadMeasureAngleFormat.GetLocalizedResource(),
			DistancePrompt = Strings.ConsystoCadMeasureDistancePrompt.GetLocalizedResource(),
			RadiusPrompt = Strings.ConsystoCadMeasureRadiusPrompt.GetLocalizedResource(),
			RadiusPointsPrompt = Strings.ConsystoCadMeasureRadiusPointsPrompt.GetLocalizedResource(),
			AnglePrompt = Strings.ConsystoCadMeasureAnglePrompt.GetLocalizedResource(),
			Preparing = Strings.ConsystoCadMeasurePreparing.GetLocalizedResource(),
			UnitsAssumed = Strings.ConsystoCadMeasureUnitsAssumed.GetLocalizedResource(),
			DrawingUnits = Strings.ConsystoCadMeasureDrawingUnits.GetLocalizedResource(),
			DrawingBoundsFormat = Strings.ConsystoCadMeasureDrawingBoundsFormat.GetLocalizedResource(),
			DrawingDistanceFormat = Strings.ConsystoCadMeasureDrawingDistanceFormat.GetLocalizedResource(),
			DrawingCentresFormat = Strings.ConsystoCadMeasureDrawingCentresFormat.GetLocalizedResource(),
			DrawingDistancePrompt = Strings.ConsystoCadMeasureDrawingDistancePrompt.GetLocalizedResource(),
			DrawingRadiusPrompt = Strings.ConsystoCadMeasureDrawingRadiusPrompt.GetLocalizedResource(),
			DrawingNoArc = Strings.ConsystoCadMeasureDrawingNoArc.GetLocalizedResource(),
			DrawingAnglePrompt = Strings.ConsystoCadMeasureDrawingAnglePrompt.GetLocalizedResource(),
			DrawingUnitsUnknown = Strings.ConsystoCadMeasureDrawingUnitsUnknown.GetLocalizedResource(),
		};

		/// <summary>A part of the assembly is shown where it lies: its own folder, with the file selected.</summary>
		private void References_ItemClick(object sender, ItemClickEventArgs e)
		{
			if (e.ClickedItem is not CadReferenceRow { IsFound: true } row ||
				SystemIO.Path.GetDirectoryName(row.Path) is not { } folder)
			{
				return;
			}

			Ioc.Default.GetService<IContentPageContext>()?.ShellPage?
				.NavigateToPath(folder, new NavigationArguments() { SelectItems = [SystemIO.Path.GetFileName(row.Path)] });
		}

		private void CadPreview_Unloaded(object sender, RoutedEventArgs e)
		{
			ViewModel.PreviewControlBase_Unloaded(sender, e);
			ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
			Unloaded -= CadPreview_Unloaded;
		}
	}
}
