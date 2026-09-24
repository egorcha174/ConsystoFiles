using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CSMath;

namespace Consysto.CadPreview.Drawing;

public enum DrawingSpace
{
    /// <summary>Model space if it has geometry, otherwise the first non-empty paper layout (Inventor puts views there).</summary>
    Auto,
    Model,
    Paper,
}

public static class CadDrawingLoader
{
    public static Drawing2D Load(string path, DrawingSpace space = DrawingSpace.Auto)
    {
        var notes = new List<string>();
        NotificationEventHandler collect = (_, e) =>
        {
            if (notes.Count < 200)
                notes.Add($"{e.NotificationType}: {e.Message}");
        };

        CadDocument document = Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase)
            ? DwgReader.Read(path, collect)
            : DxfReader.Read(path, collect);

        var (spaceName, entities) = PickSpace(document, space);
        var drawing = new Drawing2D
        {
            SourcePath = path,
            Version = document.Header.VersionString,
            Space = spaceName,
        };
        drawing.Notes.AddRange(notes);

        var flattener = new EntityFlattener(drawing);
        foreach (var entity in entities)
            flattener.Add(entity, Rgb.Black, 0);

        drawing.RecalculateBounds();
        return drawing;
    }

    private static (string Name, List<Entity> Entities) PickSpace(CadDocument document, DrawingSpace space)
    {
        var model = document.Entities.ToList();
        if (space == DrawingSpace.Model || (space == DrawingSpace.Auto && model.Any(IsDrawable)))
            return ("Model", model);

        var layout = document.Layouts
            .Where(l => l.IsPaperSpace && l.AssociatedBlock is not null)
            .OrderBy(l => l.TabOrder)
            .FirstOrDefault(l => l.AssociatedBlock.Entities.Any(IsDrawable));

        return layout is null ? ("Model", model) : (layout.Name, layout.AssociatedBlock.Entities.ToList());
    }

    private static bool IsDrawable(Entity entity) => entity is not Viewport;
}

internal sealed class EntityFlattener(Drawing2D drawing)
{
    private const int MaxDepth = 16;
    private const int FullCircleSegments = 72;

    public void Add(Entity entity, Rgb blockColor, int depth)
    {
        if (entity.IsInvisible)
            return;

        var layer = entity.Layer;
        if (layer is not null && !layer.IsOn)
            return;

        var color = Resolve(entity.Color, layer, blockColor);
        string layerName = layer?.Name ?? "0";
        drawing.Layers.Add(layerName);

        switch (entity)
        {
            case Line line:
                AddPolyline(color, layerName, [ToPoint(line.StartPoint), ToPoint(line.EndPoint)], false);
                break;

            case Arc arc:
                AddPolyline(color, layerName, ToPoints(arc.PolygonalVertexes(SegmentsFor(arc.Sweep))), false);
                break;

            case Circle circle:
                AddPolyline(color, layerName, ToPoints(circle.PolygonalVertexes(FullCircleSegments)), true);
                break;

            case Ellipse ellipse:
                AddPolyline(color, layerName, ToPoints(ellipse.PolygonalVertexes(FullCircleSegments)), ellipse.IsFullEllipse);
                break;

            case LwPolyline lwPolyline:
                AddPolyline(color, layerName,
                    BulgePoints(lwPolyline.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList(), lwPolyline.IsClosed),
                    lwPolyline.IsClosed);
                break;

            case Polyline2D polyline2D:
                AddPolyline(color, layerName,
                    BulgePoints(polyline2D.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList(), polyline2D.IsClosed),
                    polyline2D.IsClosed);
                break;

            case Polyline3D polyline3D:
                AddPolyline(color, layerName, polyline3D.Vertices.Select(v => ToPoint(v.Location)).ToArray(), polyline3D.IsClosed);
                break;

            case Spline spline:
                if (spline.TryPolygonalVertexes(SplineSegments(spline.ControlPoints.Count), out var splinePoints))
                    AddPolyline(color, layerName, ToPoints(splinePoints), spline.IsClosed);
                else
                    Skip("Spline(invalid)");
                break;

            case AttributeDefinition:
                break;

            case TextEntity text:
                AddText(color, layerName, ToPoint(text.InsertPoint), text.Height, text.Rotation, text.Value);
                break;

            case MText mText:
                AddText(color, layerName, ToPoint(mText.InsertPoint), mText.Height, mText.Rotation, mText.PlainText);
                break;

            case Insert insert:
                if (depth >= MaxDepth)
                {
                    Skip("Insert(too deep)");
                    break;
                }
                foreach (var child in insert.Explode())
                    Add(child, color, depth + 1);
                foreach (var attribute in insert.Attributes)
                    Add(attribute, color, depth + 1);
                break;

            case Dimension dimension:
                if (dimension.Block is null || depth >= MaxDepth)
                {
                    Skip("Dimension(no block)");
                    break;
                }
                foreach (var child in dimension.Block.Entities)
                    Add(child, color, depth + 1);
                break;

            case Solid solid:
                drawing.Primitives.Add(new FillPrimitive(color, layerName,
                    [ToPoint(solid.FirstCorner), ToPoint(solid.SecondCorner), ToPoint(solid.FourthCorner), ToPoint(solid.ThirdCorner)]));
                break;

            case Hatch hatch:
                foreach (var path in hatch.Paths)
                {
                    var outline = HatchBoundary(path);
                    if (outline.Length < 3)
                        continue;
                    if (hatch.IsSolid)
                        drawing.Primitives.Add(new FillPrimitive(color, layerName, outline));
                    else
                        AddPolyline(color, layerName, outline, true);
                }
                break;

            case Viewport:
            case Point:
                break;

            default:
                Skip(entity.GetType().Name);
                break;
        }
    }

    private void AddPolyline(Rgb color, string layer, Point2[] points, bool closed)
    {
        if (points.Length >= 2)
            drawing.Primitives.Add(new PolylinePrimitive(color, layer, points, closed));
    }

    private void AddText(Rgb color, string layer, Point2 position, double height, double rotation, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && height > 0)
            drawing.Primitives.Add(new TextPrimitive(color, layer, position, height, rotation, value));
    }

    private void Skip(string what) => drawing.Skipped[what] = drawing.Skipped.GetValueOrDefault(what) + 1;

    private static Rgb Resolve(Color color, Layer? layer, Rgb blockColor)
    {
        if (color.IsByBlock)
            return blockColor;
        if (color.IsByLayer)
            return layer is null ? Rgb.Black : new Rgb(layer.Color.R, layer.Color.G, layer.Color.B);
        return new Rgb(color.R, color.G, color.B);
    }

    /// <summary>
    /// How finely a spline is divided. Eight points per control point suits ordinary curves, but the cost of each point
    /// grows with the number of control points, so on a heavy curve the two multiply: a drawing from a laser shop had
    /// a spline of 13 024 control points, and dividing that one curve alone took over three minutes.
    ///
    /// Hence the ceiling. Five hundred points describe any curve far beyond what a preview a few hundred pixels across
    /// can show, and the same curve is then divided in under a second.
    /// </summary>
    private static int SplineSegments(int controlPoints) =>
        Math.Clamp(controlPoints * 8, 64, 512);

    private static int SegmentsFor(double sweep) =>
        Math.Max(8, (int)Math.Ceiling(FullCircleSegments * Math.Abs(sweep) / (2 * Math.PI)));

    private static Point2 ToPoint(XYZ point) => new(point.X, point.Y);

    private static Point2[] ToPoints(IEnumerable<XYZ> points) => points.Select(ToPoint).ToArray();

    // DXF bulge = tan(included angle / 4); positive means counter-clockwise from this vertex to the next.
    private static Point2[] BulgePoints(List<(double X, double Y, double Bulge)> vertices, bool closed)
    {
        var result = new List<Point2>(vertices.Count * 2);
        for (int i = 0; i < vertices.Count; i++)
        {
            var from = vertices[i];
            result.Add(new Point2(from.X, from.Y));

            bool hasNext = i < vertices.Count - 1 || closed;
            if (!hasNext || Math.Abs(from.Bulge) < 1e-9)
                continue;

            var to = vertices[(i + 1) % vertices.Count];
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-12)
                continue;

            int sign = Math.Sign(from.Bulge);
            double theta = 4 * Math.Atan(Math.Abs(from.Bulge));
            double radius = chord / (2 * Math.Sin(theta / 2));
            double offset = radius * Math.Cos(theta / 2);
            double centerX = (from.X + to.X) / 2 + sign * (-dy / chord) * offset;
            double centerY = (from.Y + to.Y) / 2 + sign * (dx / chord) * offset;

            double startAngle = Math.Atan2(from.Y - centerY, from.X - centerX);
            double sweep = sign * theta;
            int segments = SegmentsFor(sweep);
            for (int k = 1; k < segments; k++)
            {
                double angle = startAngle + sweep * k / segments;
                result.Add(new Point2(centerX + radius * Math.Cos(angle), centerY + radius * Math.Sin(angle)));
            }
        }
        return result.ToArray();
    }

    private static Point2[] HatchBoundary(Hatch.BoundaryPath path)
    {
        var points = new List<Point2>();
        foreach (var edge in path.Edges)
        {
            switch (edge)
            {
                case Hatch.BoundaryPath.Line line:
                    points.Add(new Point2(line.Start.X, line.Start.Y));
                    points.Add(new Point2(line.End.X, line.End.Y));
                    break;

                case Hatch.BoundaryPath.Arc arc:
                    double sweep = arc.EndAngle - arc.StartAngle;
                    if (sweep <= 0)
                        sweep += 2 * Math.PI;
                    int segments = SegmentsFor(sweep);
                    for (int k = 0; k <= segments; k++)
                    {
                        double angle = arc.StartAngle + sweep * k / segments;
                        if (!arc.CounterClockWise)
                            angle = -angle;
                        points.Add(new Point2(arc.Center.X + arc.Radius * Math.Cos(angle), arc.Center.Y + arc.Radius * Math.Sin(angle)));
                    }
                    break;

                case Hatch.BoundaryPath.Polyline polyline:
                    foreach (var vertex in polyline.Vertices)
                        points.Add(new Point2(vertex.X, vertex.Y));
                    break;
            }
        }
        return points.ToArray();
    }
}
