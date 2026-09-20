// Consysto fork: one-time configuration of the CAD preview core.

using Consysto.CadPreview;
using Consysto.CadPreview.Step;
using Windows.Storage;

namespace Files.App.Cad
{
	/// <summary>
	/// Every entry point (info pane, thumbnails) asks through here, so the core is configured before its first use.
	/// </summary>
	public static class CadPreviewSetup
	{
		private static readonly Lazy<bool> configured = new(() =>
		{
			// Tessellated STEP/IGES meshes live with the other caches of the package. The mesher itself ships with
			// Consysto.CadPreview.WinUI (Consysto.CadPreview.WinUI\occt in the package), which the core finds on its own.
			StepMeshSource.CacheDirectory = SystemIO.Path.Combine(AppStorage.LocalCacheFolderPath, "cad-meshes");
			return true;
		});

		public static bool IsSupported(string? extension)
		{
			_ = configured.Value;
			return CadPreviewSource.IsSupported(extension);
		}
	}
}
