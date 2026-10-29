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

		private Mesh3D? mesh;
		/// <summary>Set late for a document shown first by its saved picture, once its shape has been built.</summary>
		public Mesh3D? Mesh
		{
			get => mesh;
			private set => SetProperty(ref mesh, value);
		}

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
			// A document whose shape takes seconds shows its saved picture at once; the shape replaces it when ready
			byte[]? quickPicture = null;
			try
			{
				quickPicture = await Task.Run(() => CadPreviewSource.LoadQuickPicture(Item.ItemPath!), LoadCancelledTokenSource.Token);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogWarning(ex, "CAD preview could not read the saved picture");
			}

			if (quickPicture is not null)
			{
				await ShowImageAsync(quickPicture);
				_ = LoadShapeLaterAsync();
				await LoadReferencesAsync();
				return await LoadPropertiesAsync();
			}

			CadPreviewContent content;
			try
			{
				content = await Task.Run(() => CadPreviewSource.Load(Item.ItemPath!, LoadCancelledTokenSource.Token), LoadCancelledTokenSource.Token);
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
			if (Drawing is { } drawing)
				details.AddRange(await Task.Run(() => CuttingDetails(drawing), LoadCancelledTokenSource.Token));
			// Files the system draws itself — a page of an Illustrator document, a vector picture — are drawn here and
			// then shown like any other picture
			if (content.Drawn is { } drawn)
			{
				try
				{
					var picture = drawn.Kind switch
					{
						HostDrawnKind.PdfPage => await CadThumbnailRenderer.RenderPdfPageAsync(drawn.Path, PageSize),
						HostDrawnKind.Svg => await CadThumbnailRenderer.RenderSvgAsync(drawn.Path, PageSize),
						_ => null,
					};
					content = new CadPreviewContent { Image = picture };
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "The file could not be drawn by the engine of the system");
				}
			}

			if (content.Image is { } image)
				await ShowImageAsync(image);

			return details;
		}

		private Task ShowImageAsync(byte[] image)
			=> MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(async () =>
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

		private async Task LoadShapeLaterAsync()
		{
			var token = LoadCancelledTokenSource.Token;
			try
			{
				// Not stopped when the user moves on: a build cut short leaves nothing in the cache and starts over next time
				var content = await Task.Run(() => CadPreviewSource.Load(Item.ItemPath!), CancellationToken.None);
				if (content.Mesh is { IsEmpty: false } shape && !token.IsCancellationRequested)
					await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(() => Mesh = shape);
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				// The saved picture stays; nothing is lost
				App.Logger.LogWarning(ex, "CAD preview could not build the shape");
			}
		}

		/// <summary>The iProperties of an Inventor document, shown above the file's own details.</summary>
		private async Task<List<FileProperty>> LoadPropertiesAsync()
		{
			if (Consysto.CadPreview.Print.GcodeReader.IsPrintFile(Item.ItemPath))
				return await LoadPrintPropertiesAsync();

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

		/// <summary>For a drawing sent to a laser: how long the cut is and how many times the beam pierces the sheet.</summary>
		private static List<FileProperty> CuttingDetails(Drawing2D drawing)
		{
			if (drawing.Curves.Count == 0)
				return [];

			double perUnit = drawing.MillimetersPerUnit ?? 1;

			// Ends closer than a hundredth of a millimetre belong to one outline
			var cutting = DrawingMeasure.Cutting(drawing, 0.01 / perUnit);
			var culture = System.Globalization.CultureInfo.CurrentCulture;
			string length = drawing.MillimetersPerUnit is null
				? $"{cutting.Length.ToString("#,0.#", culture)} {Strings.ConsystoCadMeasureDrawingUnits.GetLocalizedResource()}"
				: $"{(cutting.Length * perUnit).ToString("#,0.#", culture)} {Strings.ConsystoCadMeasureMillimeters.GetLocalizedResource()}";

			return
			[
				new FileProperty { NameResource = "ConsystoCadCutLength", SectionResource = "ConsystoCadCuttingSection", Value = length },
				new FileProperty { NameResource = "ConsystoCadPierces", SectionResource = "ConsystoCadCuttingSection", Value = cutting.Pierces.ToString(culture) },
			];
		}

		/// <summary>What the slicer wrote about a print job: time, plastic, layer, nozzle, printer.</summary>
		private async Task<List<FileProperty>> LoadPrintPropertiesAsync()
		{
			Consysto.CadPreview.Print.PrintInfo info;
			try
			{
				info = await Task.Run(() => Consysto.CadPreview.Print.GcodeReader.Read(Item.ItemPath!), LoadCancelledTokenSource.Token);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogWarning(ex, "The print job could not be read");
				return [];
			}

			var details = new List<FileProperty>();
			void Add(string nameResource, string? value)
			{
				if (!string.IsNullOrEmpty(value))
					details.Add(new FileProperty { NameResource = nameResource, SectionResource = "ConsystoPrintSection", Value = value });
			}

			Add("ConsystoPrintTime", info.PrintTime is { } time ? PrintFormat.Time(time) : null);
			Add("ConsystoPrintFilament", PrintFormat.Filament(info));
			Add("ConsystoPrintLayerHeight", info.LayerHeight is { } layer ? PrintFormat.Millimeters(layer) : null);
			Add("ConsystoPrintLayers", info.Layers?.ToString(System.Globalization.CultureInfo.CurrentCulture));
			Add("ConsystoPrintNozzle", info.NozzleDiameter is { } nozzle ? PrintFormat.Millimeters(nozzle) : null);
			Add("ConsystoPrintPrinter", info.Printer);
			Add("ConsystoPrintSlicer", info.Slicer);

			Properties = new ObservableCollection<CadPropertyRow>(details.Select(detail => new CadPropertyRow(detail.Name, detail.Value?.ToString() ?? string.Empty)));
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
