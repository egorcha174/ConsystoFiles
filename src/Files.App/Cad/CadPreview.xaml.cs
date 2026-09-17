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
				Host.Children.Add(new CadDrawingView { Drawing = model.Drawing });
			else if (model.Mesh is not null)
				Host.Children.Add(new CadMeshView { Mesh = model.Mesh });

			Unloaded += CadPreview_Unloaded;
		}

		private CadPreviewViewModel ViewModel { get; }

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
			Unloaded -= CadPreview_Unloaded;
		}
	}
}
