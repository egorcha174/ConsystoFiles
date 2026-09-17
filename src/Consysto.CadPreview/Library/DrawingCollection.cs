using Consysto.CadPreview.Inventor;
using Consysto.Collections;

namespace Consysto.CadPreview.Library;

public static class DrawingFields
{
    /// <summary>Part, assembly, drawing, presentation or an exchange model.</summary>
    public const string Kind = "kind";

    /// <summary>DWG, DXF, IPT… — what the file is, as the user names it.</summary>
    public const string Format = "format";

    /// <summary>The folder the drawing lies in, so an order or a product can be found as a whole.</summary>
    public const string Folder = "folder";

    /// <summary>How many documents an assembly is built from; absent for everything else.</summary>
    public const string Parts = "parts";

    /// <summary>iProperties of Inventor documents; absent for other formats and for empty properties.</summary>
    public const string PartNumber = "partNumber";
    public const string Description = "description";
    public const string Material = "material";
    public const string Designer = "designer";
    public const string Project = "project";

    /// <summary>Mass in kilograms, invariant culture.</summary>
    public const string Mass = "mass";
}

/// <summary>
/// A catalogue of drawings: Inventor documents, DWG/DXF and the exchange formats around them. Nothing here needs Inventor
/// installed — an assembly is read straight out of the file (see <see cref="InventorReferenceReader"/>).
/// </summary>
public sealed class DrawingCollection : CollectionKind
{
    private static readonly string[] Extensions =
        [".ipt", ".iam", ".idw", ".ipn", ".dwg", ".dxf", ".step", ".stp", ".iges", ".igs", ".stl", ".obj", ".3mf"];

    public override string Id => "drawings";

    /// <summary>2: iProperties are read, so an index made before them is read again.</summary>
    public override int Version => 2;

    public override bool Accepts(string fileName)
        => Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Inventor's OldVersions folders hold backup copies of every save; they would double the catalogue.</summary>
    public override bool AcceptsPath(string path)
        => !path.Contains(@"\OldVersions\", StringComparison.OrdinalIgnoreCase);

    public override IReadOnlyList<CollectionFacet> Facets =>
    [
        new(DrawingFields.Kind, DrawingFields.Kind),
        new(DrawingFields.Format, DrawingFields.Format),
        new(DrawingFields.Folder, DrawingFields.Folder),
        new(DrawingFields.Material, DrawingFields.Material),
        new(DrawingFields.Designer, DrawingFields.Designer),
        new(DrawingFields.Project, DrawingFields.Project),
    ];

    public override IReadOnlyList<string> SearchFields =>
        [DrawingFields.Folder, DrawingFields.Format, DrawingFields.PartNumber, DrawingFields.Description, DrawingFields.Material];

    public override ItemMetadata Read(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var metadata = new ItemMetadata
        {
            Title = TitleFromFileName(Path.GetFileName(path)),
            HasCover = CadPreviewSource.IsSupported(extension),
        };

        metadata
            .Set(DrawingFields.Kind, KindOf(extension))
            .Set(DrawingFields.Format, extension.TrimStart('.').ToUpperInvariant())
            .Set(DrawingFields.Folder, Path.GetFileName(Path.GetDirectoryName(path)));

        if (InventorPropertyReader.IsSupported(extension))
        {
            var properties = InventorPropertyReader.Read(path);
            metadata
                .Set(DrawingFields.PartNumber, properties.PartNumber)
                .Set(DrawingFields.Description, properties.Description ?? properties.Title)
                .Set(DrawingFields.Material, properties.Material)
                .Set(DrawingFields.Designer, properties.Designer ?? properties.Author)
                .Set(DrawingFields.Project, properties.Project)
                .Set(DrawingFields.Mass, properties.MassKilograms?.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        }

        // Only an assembly is opened a second time: for everything else the list of documents is empty or trivial
        if (extension == ".iam")
        {
            var references = InventorReferenceReader.Read(path);
            if (references.Count > 0)
                metadata.Set(DrawingFields.Parts, references.Count.ToString());
        }

        return metadata;
    }

    /// <summary>Drawings of one product share a name and differ by extension, so only identical files are copies.</summary>
    public override string? SameItemKey(CollectionItem item)
        => null;

    private static string KindOf(string extension)
        => extension switch
        {
            ".ipt" => "Деталь",
            ".iam" => "Сборка",
            ".idw" => "Чертёж",
            ".ipn" => "Презентация",
            ".dwg" or ".dxf" => "Чертёж",
            ".step" or ".stp" or ".iges" or ".igs" => "Обменная модель",
            _ => "Модель",
        };
}
