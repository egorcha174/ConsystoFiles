using Consysto.CadPreview.Artwork;
using Consysto.CadPreview.Drawing;
using Consysto.CadPreview.Fusion;
using Consysto.CadPreview.Inventor;
using Consysto.CadPreview.Kompas;
using Consysto.CadPreview.Mesh;
using Consysto.CadPreview.Print;
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

    /// <summary>
    /// A file the host has to draw itself, because drawing it needs an engine of the operating system: the first page
    /// of a PDF-shaped document, or a vector picture. The path is passed on rather than a picture.
    /// </summary>
    public HostDrawn? Drawn { get; init; }

    public bool IsEmpty => Drawing is null && Mesh is null && Image is null && Drawn is null;
}

/// <summary>What the host is asked to draw, and from which file.</summary>
public sealed record HostDrawn(HostDrawnKind Kind, string Path);

public enum HostDrawnKind
{
    /// <summary>The first page of a document that is a PDF inside — an Adobe Illustrator file saved the usual way.</summary>
    PdfPage,

    /// <summary>A vector picture in SVG.</summary>
    Svg,
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
                || ArtworkPreviewReader.IsSupported(extension)
                || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
                || FusionPreviewReader.IsSupported(extension)
                || StepMeshSource.IsSupported(extension)
                || GcodeReader.IsSupported(extension));

    /// <summary>
    /// For a thumbnail in a folder: a print job shows the picture its slicer stored, which is read from the head of the file,
    /// and is drawn from its moves only when there is none and the file is not too large to go through.
    /// </summary>
    public static CadPreviewContent LoadThumbnail(string path, long maxDrawnBytes)
    {
        if (!GcodeReader.IsSupported(Path.GetExtension(path)))
            return Load(path);

        if (GcodeReader.Read(path).Thumbnail is { } picture)
            return new CadPreviewContent { Image = picture };

        return new FileInfo(path).Length <= maxDrawnBytes && GcodeToolpath.Load(path) is { } toolpath
            ? new CadPreviewContent { Drawing = toolpath }
            : new CadPreviewContent();
    }

    public static CadPreviewContent Load(string path, CancellationToken cancellation = default)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            // In the preview pane a print job is drawn from its moves, which can be zoomed into: the picture slicers
            // store is small, often 140×110. The picture stays for jobs that cannot be drawn: binary G-code
            case ".gcode":
            case ".gco":
            case ".g":
            case ".gx":
            case ".bgcode":
                if (extension != ".bgcode" && GcodeToolpath.Load(path, cancellation) is { } toolpath)
                    return new CadPreviewContent { Drawing = toolpath };
                return new CadPreviewContent { Image = GcodeReader.Read(path).Thumbnail };

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

                if (FusionPreviewReader.IsSupported(extension))
                    return new CadPreviewContent { Image = FusionPreviewReader.TryRead(path) };

                // Illustrator and CorelDRAW: an .ai is a PDF to be drawn, a .cdr carries a finished picture
                if (extension == ".svg")
                    return new CadPreviewContent { Drawn = new HostDrawn(HostDrawnKind.Svg, path) };

                if (ArtworkPreviewReader.IsSupported(extension))
                    return ArtworkPreviewReader.IsPdfInside(path)
                        ? new CadPreviewContent { Drawn = new HostDrawn(HostDrawnKind.PdfPage, path) }
                        : new CadPreviewContent { Image = ArtworkPreviewReader.TryRead(path) };

                return InventorPreviewReader.IsSupported(extension)
                    ? new CadPreviewContent { Image = InventorPreviewReader.TryRead(path) }
                    : new CadPreviewContent();
        }
    }

    private static CadPreviewContent FromMesh(Mesh3D mesh)
        => mesh.IsEmpty ? new CadPreviewContent() : new CadPreviewContent { Mesh = mesh };
}
