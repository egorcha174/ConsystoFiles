// Consysto fork: CAD preview for the info pane (DWG/DXF, STL/OBJ/3MF, Inventor).

using Consysto.CadPreview;
using Consysto.CadPreview.Drawing;
using Consysto.CadPreview.Inventor;
using Consysto.CadPreview.Mesh;
using Consysto.CadPreview.WinUI;
using Files.App.ViewModels.Previews;
using Files.App.ViewModels.Properties;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Files.App.Cad
{
	public sealed partial class CadPreviewViewModel : BasePreviewModel
	{
		public CadPreviewViewModel(ListedItem item)
			: base(item)
		{
		}

		public static bool IsSupported(string? extension)
			=> CadPreviewSetup.IsSupported(extension);

		public Drawing2D? Drawing { get; private set; }

		public Mesh3D? Mesh { get; private set; }

		private BitmapImage? embeddedImage;
		public BitmapImage? EmbeddedImage
		{
			get => embeddedImage;
			private set => SetProperty(ref embeddedImage, value);
		}

		/// <summary>False when the file could not be read; the pane then falls back to the regular previewers.</summary>
		public bool HasPreview
			=> Drawing is not null || Mesh is not null || EmbeddedImage is not null;

		private ObservableCollection<CadReferenceRow> references = [];
		/// <summary>
		/// The documents an assembly is built from, or the part a derived part came from.
		/// </summary>
		/// <remarks>An observable collection, not a read-only list: a list reaches WinRT as a vector view, which ItemsSource refuses.</remarks>
		public ObservableCollection<CadReferenceRow> References
		{
			get => references;
			private set
			{
				if (SetProperty(ref references, value))
					OnPropertyChanged(nameof(ReferencesVisibility));
			}
		}

		private ObservableCollection<CadPropertyRow> properties = [];
		/// <summary>The iProperties shown under the preview, the same ones the Details tab lists.</summary>
		public ObservableCollection<CadPropertyRow> Properties
		{
			get => properties;
			private set
			{
				if (SetProperty(ref properties, value))
					OnPropertyChanged(nameof(PropertiesVisibility));
			}
		}

		public Visibility PropertiesVisibility
			=> Properties.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

		public Visibility ReferencesVisibility
			=> References.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

		/// <summary>How large the page of a PDF-shaped document is drawn: enough to read in the pane without waste.</summary>
		private const int PageSize = 1024;

		public override async Task<List<FileProperty>> LoadPreviewAndDetailsAsync()
		{
			CadPreviewContent content;
			try
			{
				content = await Task.Run(() => CadPreviewSource.Load(Item.ItemPath!), LoadCancelledTokenSource.Token);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogWarning(ex, "CAD preview could not read the file");
				return [];
			}

			Drawing = content.Drawing;
			Mesh = content.Mesh;
			await LoadReferencesAsync();
			var details = await LoadPropertiesAsync();
			// An Illustrator document is a PDF inside: its first page is drawn here and then shown like any other picture
			if (content.PdfPath is { } pdf)
			{
				try
				{
					content = new CadPreviewContent { Image = await CadThumbnailRenderer.RenderPdfPageAsync(pdf, PageSize) };
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "The first page of the PDF-shaped document could not be drawn");
				}
			}

			if (content.Image is { } image)
			{
				await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () =>
				{
					try
					{
						EmbeddedImage = await ToBitmapAsync(image);
					}
					catch (Exception ex)
					{
						App.Logger.LogWarning(ex, "CAD preview could not decode the embedded image");
					}
				});
			}

			return details;
		}

		/// <summary>The iProperties of an Inventor document, shown above the file's own details.</summary>
		private async Task<List<FileProperty>> LoadPropertiesAsync()
		{
			if (!InventorPropertyReader.IsSupported(SystemIO.Path.GetExtension(Item.ItemPath)))
				return [];

			InventorProperties properties;
			try
			{
				properties = await Task.Run(() => InventorPropertyReader.Read(Item.ItemPath!), LoadCancelledTokenSource.Token);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogWarning(ex, "The iProperties of the Inventor file could not be read");
				return [];
			}

			var details = new List<FileProperty>();
			void Add(string nameResource, string? value, string section = "ConsystoIPropertiesSection")
			{
				if (!string.IsNullOrEmpty(value))
					details.Add(new FileProperty { NameResource = nameResource, SectionResource = section, Value = value });
			}

			Add("ConsystoIPropertyPartNumber", properties.PartNumber);
			Add("ConsystoIPropertyDescription", properties.Description);
			Add("ConsystoIPropertyTitle", properties.Title);
			Add("ConsystoIPropertyMaterial", properties.Material);
			Add("ConsystoIPropertyMass", properties.MassKilograms is { } mass
				? string.Format(Strings.ConsystoIPropertyMassValue.GetLocalizedResource(), FormatMass(mass))
				: null);
			Add("ConsystoIPropertySheetMetalRule", properties.SheetMetalRule);
			Add("ConsystoIPropertyProject", properties.Project);
			Add("ConsystoIPropertyDesigner", properties.Designer ?? properties.Author);
			Add("ConsystoIPropertyVendor", properties.Vendor);
			Add("ConsystoIPropertyComments", properties.Comments);

			foreach (var (name, value) in properties.Custom)
				details.Add(new FileProperty { LocalizedName = name, SectionResource = "ConsystoIPropertiesCustomSection", Value = value });

			// The Details tab has no list control of its own, so the parts of an assembly go there one per line
			foreach (var reference in References)
				details.Add(new FileProperty { LocalizedName = reference.Name, SectionResource = "ConsystoIPropertiesPartsSection", Value = reference.IsFound ? reference.Path : Strings.ConsystoIPropertyPartMissing.GetLocalizedResource() });

			Properties = new ObservableCollection<CadPropertyRow>(details
				.Where(detail => detail.SectionResource != "ConsystoIPropertiesPartsSection")
				.Select(detail => new CadPropertyRow(detail.Name, detail.Value?.ToString() ?? string.Empty)));
			return details;
		}

		/// <summary>An assembly lists its parts; reading them means opening the file again, so it happens off the UI thread.</summary>
		private async Task LoadReferencesAsync()
		{
			if (!InventorReferenceReader.IsSupported(SystemIO.Path.GetExtension(Item.ItemPath)))
				return;

			try
			{
				var found = await Task.Run(() => InventorReferenceReader.Read(Item.ItemPath!), LoadCancelledTokenSource.Token);
				References = new ObservableCollection<CadReferenceRow>(found.Select(reference => new CadReferenceRow(reference)));
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogWarning(ex, "The documents of the Inventor file could not be read");
			}
		}

		/// <summary>Three significant digits, so a small part reads 0,012 rather than 0.</summary>
		internal static string FormatMass(double kilograms)
		{
			var decimals = kilograms >= 100 ? 1 : Math.Clamp(2 - (int)Math.Floor(Math.Log10(kilograms)), 0, 6);
			return Math.Round(kilograms, decimals).ToString("0." + new string('#', Math.Max(decimals, 1)));
		}

		private static async Task<BitmapImage> ToBitmapAsync(byte[] image)
		{
			using var stream = new InMemoryRandomAccessStream();
			using (var writer = new DataWriter(stream))
			{
				writer.WriteBytes(image);
				await writer.StoreAsync();
				writer.DetachStream();
			}
			stream.Seek(0);

			var bitmap = new BitmapImage();
			await bitmap.SetSourceAsync(stream);
			return bitmap;
		}
	}

	/// <summary>One iProperty for the list under the preview.</summary>
	public sealed partial class CadPropertyRow(string name, string value)
	{
		public string Name { get; } = name;

		public string Value { get; } = value;
	}

	/// <summary>One document of an assembly, ready for the list: a file that moved away is shown dimmed by its old path.</summary>
	public sealed partial class CadReferenceRow(InventorReference reference)
	{
		public string Name { get; } = reference.Name;

		public string Path { get; } = reference.ResolvedPath ?? reference.RecordedPath;

		/// <summary>A part that moved away has no folder to show, so it is not clickable.</summary>
		public bool IsFound { get; } = reference.IsFound;

		public double Opacity { get; } = reference.IsFound ? 1 : 0.5;
	}
}
