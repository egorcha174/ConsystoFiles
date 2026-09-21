using Consysto.CadPreview.Drawing;
using Consysto.CadPreview.Inventor;
using Consysto.CadPreview.Kompas;
using Consysto.CadPreview.Mesh;
using Consysto.CadPreview.SolidWorks;
using Consysto.CadPreview.Step;

namespace Consysto.CadPreview;

/// <summary>What a file can be shown as. At most one of the three is set; none when the file has nothing to show.</summary>
public sealed class CadPreviewContent
{
    /// <summary>2D geometry to draw: DXF, and DWG with 2D entities.</summary>
    public Drawing2D? Drawing { get; init; }

    /// <summary>3D geometry to orbit: STL, OBJ, and 3MF that carries a mesh.</summary>
    public Mesh3D? Mesh { get; init; }

    /// <summary>
    /// A PNG or BMP stored in the file, for files whose geometry we cannot draw ourselves:
    /// DWG with only 3D solids, Inventor documents, sliced 3MF projects.
    /// </summary>
    public byte[]? Image { get; init; }

    public bool IsEmpty => Drawing is null && Mesh is null && Image is null;
}

public static class CadPreviewSource
{
    private static readonly string[] DrawingExtensions = [".dxf", ".dwg"];
    private static readonly string[] MeshExtensions = [".stl", ".obj", ".3mf"];

    public static bool IsSupported(string? extension)
        => extension is not null
            && (DrawingExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                || MeshExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
                || InventorPreviewReader.IsSupported(extension)
                || SolidWorksPreviewReader.IsSupported(extension)
                || CadMeshSource.IsSupported(extension)
                || KompasPreviewReader.IsSupported(extension)
                || StepMeshSource.IsSupported(extension));

    public static CadPreviewContent Load(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".dxf":
            case ".dwg":
                var drawing = CadDrawingLoader.Load(path);
                if (drawing.Primitives.Count > 0)
                    return new CadPreviewContent { Drawing = drawing };
                return new CadPreviewContent { Image = extension == ".dwg" ? DwgPreviewReader.TryRead(path) : null };

            case ".stl":
                return FromMesh(StlReader.Read(path));

            case ".obj":
                return FromMesh(ObjReader.Read(path));

            case ".step":
            case ".stp":
            case ".iges":
            case ".igs":
                return FromMesh(StepMeshSource.Load(path));

            case ".3mf":
                var package = ThreeMfReader.Read(path);
                return package.Mesh.IsEmpty
                    ? new CadPreviewContent { Image = package.Thumbnail }
                    : new CadPreviewContent { Mesh = package.Mesh };

            default:
                // Documents of other CAD systems: what they saved as their own picture is what we show
                // Geometry first, so a document can be turned in the hand; what cannot be read falls back to its own picture
                if (CadMeshSource.IsSupported(extension))
                {
                    var shape = CadMeshSource.Load(path);
                    if (!shape.IsEmpty)
                        return new CadPreviewContent { Mesh = shape };
                }

                if (SolidWorksPreviewReader.IsSupported(extension))
                    return new CadPreviewContent { Image = SolidWorksPreviewReader.TryRead(path) };

                if (KompasPreviewReader.IsSupported(extension))
                    return new CadPreviewContent { Image = KompasPreviewReader.TryRead(path) };

                return InventorPreviewReader.IsSupported(extension)
                    ? new CadPreviewContent { Image = InventorPreviewReader.TryRead(path) }
                    : new CadPreviewContent();
        }
    }

    private static CadPreviewContent FromMesh(Mesh3D mesh)
        => mesh.IsEmpty ? new CadPreviewContent() : new CadPreviewContent { Mesh = mesh };
}
