using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using ACadSharp.Types.Units;
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
            MillimetersPerUnit = MillimetersPer(document.Header.InsUnits),
        };
        drawing.Notes.AddRange(notes);

        var flattener = new EntityFlattener(drawing);
        foreach (var entity in entities)
            flattener.Add(entity, Rgb.Black, 0);

        // Drawings of the old kind come back parsed but empty — every vertex at the origin. Nothing was drawn, so the
        // text of the file is read directly rather than showing the user a blank pane. A file the main reader handled
        // never reaches here.
        if (drawing.Primitives.Count == 0 && Path.GetExtension(path).Equals(".dxf", StringComparison.OrdinalIgnoreCase))
            LegacyDxfReader.TryRead(path, drawing);

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

    /// <summary>The $INSUNITS of the header in millimetres; null for a drawing that states no units.</summary>
    private static double? MillimetersPer(UnitsType units) => units switch
    {
        UnitsType.Millimeters => 1,
        UnitsType.Centimeters => 10,
        UnitsType.Decimeters => 100,
        UnitsType.Meters => 1000,
        UnitsType.Kilometers => 1_000_000,
        UnitsType.Microns => 0.001,
        UnitsType.Inches or UnitsType.USSurveyInches => 25.4,
        UnitsType.Feet or UnitsType.USSurveyFeet => 304.8,
        UnitsType.Yards or UnitsType.USSurveyYards => 914.4,
        UnitsType.Mils => 0.0254,
        _ => null,
    };
}

internal sealed class EntityFlattener(Drawing2D drawing)
{
    private const int MaxDepth = 16;
    private const int FullCircleSegments = 72;

    // Inside a dimension: drawn, but not part of the shape, so neither measured nor cut
    private bool _annotation;

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
            {
                Point2[] ends = [ToPoint(line.StartPoint), ToPoint(line.EndPoint)];
                AddPolyline(color, layerName, ends, false);
                AddCurve(CurveShape.Line, layerName, ends, false);
                break;
            }

            case Arc arc:
            {
                var points = ToPoints(arc.PolygonalVertexes(SegmentsFor(arc.Sweep)));
                AddPolyline(color, layerName, points, false);
                AddArc(layerName, points, arc.Radius, arc.Radius * Math.Abs(arc.Sweep), false);
                break;
            }

            case Circle circle:
            {
                var points = ToPoints(circle.PolygonalVertexes(FullCircleSegments));
                AddPolyline(color, layerName, points, true);
                AddArc(layerName, points, circle.Radius, 2 * Math.PI * circle.Radius, true);
                break;
            }

            case Ellipse ellipse:
            {
                var points = ToPoints(ellipse.PolygonalVertexes(FullCircleSegments));
                AddPolyline(color, layerName, points, ellipse.IsFullEllipse);

                // A circle from a block comes back from Explode as an ellipse with equal axes; it is still a hole
                if (Math.Abs(ellipse.RadiusRatio - 1) < 1e-6)
                    AddArc(layerName, points, ellipse.MajorAxis / 2, PathLength(points, ellipse.IsFullEllipse), ellipse.IsFullEllipse);
                else
                    AddCurve(CurveShape.Curve, layerName, points, ellipse.IsFullEllipse);
                break;
            }

            case LwPolyline lwPolyline:
            {
                var vertices = lwPolyline.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList();
                AddPolyline(color, layerName, BulgePoints(vertices, lwPolyline.IsClosed), lwPolyline.IsClosed);
                AddSegments(layerName, vertices, lwPolyline.IsClosed);
                break;
            }

            case Polyline2D polyline2D:
            {
                var vertices = polyline2D.Vertices.Select(v => (v.Location.X, v.Location.Y, v.Bulge)).ToList();
                AddPolyline(color, layerName, BulgePoints(vertices, polyline2D.IsClosed), polyline2D.IsClosed);
                AddSegments(layerName, vertices, polyline2D.IsClosed);
                break;
            }

            case Polyline3D polyline3D:
            {
                var points = polyline3D.Vertices.Select(v => ToPoint(v.Location)).ToArray();
                AddPolyline(color, layerName, points, polyline3D.IsClosed);
                AddCurve(CurveShape.Curve, layerName, points, polyline3D.IsClosed);
                break;
            }

            case Spline spline:
                // ACadSharp cannot evaluate several periodic splines written by Illustrator/laser CAM exports:
                // TryPolygonalVertexes may return an origin point or coordinates many orders larger than the source.
                // The control polygon is a safe, bounded approximation for a preview and preserves the real extents.
                // A closed periodic spline (laser CAM, CorelDRAW) comes back from ACadSharp with a first vertex at the
                // origin: two rays to (0,0) per curve, inside the extents check when the part lies near the origin
                // (08.10.2026, «Мини печь буржуйка.dxf»: 10 splines, 20 rays). Its knot vector is stored in full, so the
                // same curve evaluates correctly as an ordinary one: that is tried first.
                Point2[]? curve = null;
                if (spline.Flags.HasFlag(SplineFlags.Periodic) && TryEvaluateAsNonPeriodic(spline, out var plainPoints))
                    curve = ToPoints(plainPoints);
                else if (spline.TryPolygonalVertexes(SplineSegments(spline.ControlPoints.Count), out var splinePoints)
                    && IsSane(splinePoints, spline.ControlPoints))
                    curve = ToPoints(splinePoints);
                else if (spline.ControlPoints.Count >= 2
                    && spline.ControlPoints.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)))
                    curve = ToPoints(spline.ControlPoints);

                if (curve is null)
                {
                    Skip("Spline(invalid)");
                    break;
                }

                AddPolyline(color, layerName, curve, spline.IsClosed);
                AddCurve(CurveShape.Curve, layerName, curve, spline.IsClosed);
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
                bool outer = _annotation;
                _annotation = true;
                foreach (var child in dimension.Block.Entities)
                    Add(child, color, depth + 1);
                _annotation = outer;
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

    private void AddCurve(CurveShape shape, string layer, Point2[] points, bool closed)
    {
        if (_annotation || points.Length < 2)
            return;

        double length = PathLength(points, closed);
        if (length > 0 && double.IsFinite(length))
            drawing.Curves.Add(new DrawingCurve { Shape = shape, Points = points, Closed = closed, Length = length, Layer = layer });
    }

    /// <summary>
    /// The centre is found from the flattened points rather than taken from the entity: those are already where the
    /// picture draws them, in world coordinates, whatever the extrusion direction or the block it came from.
    /// </summary>
    private void AddArc(string layer, Point2[] points, double radius, double length, bool closed)
    {
        if (_annotation || points.Length < 3 || !(radius > 0) || !double.IsFinite(length))
            return;

        int third = points.Length / 3;
        if (CircleCenter(points[0], points[third], points[Math.Min(points.Length - 1, 2 * third)]) is not { } center)
        {
            AddCurve(CurveShape.Curve, layer, points, closed);
            return;
        }

        drawing.Curves.Add(new DrawingCurve
        {
            Shape = CurveShape.Arc,
            Points = points,
            Closed = closed,
            Center = center,
            Radius = Distance(center, points[0]),
            Length = length,
            Layer = layer,
        });
    }

    /// <summary>A polyline is measured piece by piece, so each of its corners can be picked.</summary>
    private void AddSegments(string layer, List<(double X, double Y, double Bulge)> vertices, bool closed)
    {
        int count = closed ? vertices.Count : vertices.Count - 1;
        for (int i = 0; i < count; i++)
        {
            var from = vertices[i];
            var to = vertices[(i + 1) % vertices.Count];
            if (Math.Abs(from.Bulge) < 1e-9)
            {
                AddCurve(CurveShape.Line, layer, [new Point2(from.X, from.Y), new Point2(to.X, to.Y)], false);
                continue;
            }

            var points = BulgePoints([from, (to.X, to.Y, 0)], false);
            double chord = Distance(new Point2(from.X, from.Y), new Point2(to.X, to.Y));
            double theta = 4 * Math.Atan(Math.Abs(from.Bulge));
            double radius = chord / (2 * Math.Sin(theta / 2));
            AddArc(layer, points, radius, radius * theta, false);
        }
    }

    internal static Point2? CircleCenter(Point2 a, Point2 b, Point2 c)
    {
        double d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        if (Math.Abs(d) < 1e-12)
            return null;

        double a2 = a.X * a.X + a.Y * a.Y, b2 = b.X * b.X + b.Y * b.Y, c2 = c.X * c.X + c.Y * c.Y;
        var center = new Point2(
            (a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d,
            (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
        return double.IsFinite(center.X) && double.IsFinite(center.Y) ? center : null;
    }

    private static double PathLength(Point2[] points, bool closed)
    {
        double length = 0;
        for (int i = 1; i < points.Length; i++)
            length += Distance(points[i - 1], points[i]);
        return closed && points.Length > 2 ? length + Distance(points[^1], points[0]) : length;
    }

    private static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private void AddText(Rgb color, string layer, Point2 position, double height, double rotation, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && height > 0)
            drawing.Primitives.Add(new TextPrimitive(color, layer, position, height, rotation, value));
    }

    private void Skip(string what) => drawing.Skipped[what] = drawing.Skipped.GetValueOrDefault(what) + 1;

    /// <summary>
    /// The curve of a spline by de Boor's algorithm over its working domain, knots[degree]..knots[count]. ACadSharp
    /// walks the whole knot vector instead: with the periodic flag cleared the curve came out right but with a short
    /// hook at the seam of every closed outline, the part of the knot vector outside that domain (08.10.2026).
    /// </summary>
    private static bool TryEvaluateAsNonPeriodic(Spline spline, out List<XYZ> points)
    {
        points = [];
        int degree = spline.Degree;
        var controls = spline.ControlPoints;
        var knots = spline.Knots;
        int count = controls.Count;
        if (degree < 1 || count <= degree || knots.Count != count + degree + 1)
            return false;

        double start = knots[degree], end = knots[count];
        if (!(end > start) || !double.IsFinite(start) || !double.IsFinite(end))
            return false;

        bool rational = spline.Weights.Count == count;
        int segments = SplineSegments(count);
        var homogeneous = new (double X, double Y, double Z, double W)[degree + 1];
        for (int step = 0; step <= segments; step++)
        {
            double t = step == segments ? end : start + (end - start) * step / segments;

            // The knot span holding t, kept inside the working domain so the end of the curve is reached exactly
            int span = degree;
            while (span < count - 1 && knots[span + 1] <= t)
                span++;

            for (int j = 0; j <= degree; j++)
            {
                var point = controls[span - degree + j];
                double weight = rational ? spline.Weights[span - degree + j] : 1;
                homogeneous[j] = (point.X * weight, point.Y * weight, point.Z * weight, weight);
            }
            for (int r = 1; r <= degree; r++)
                for (int j = degree; j >= r; j--)
                {
                    double left = knots[span - degree + j], right = knots[span + 1 + j - r];
                    double alpha = right > left ? (t - left) / (right - left) : 0;
                    var a = homogeneous[j - 1];
                    var b = homogeneous[j];
                    homogeneous[j] = (a.X + alpha * (b.X - a.X), a.Y + alpha * (b.Y - a.Y), a.Z + alpha * (b.Z - a.Z), a.W + alpha * (b.W - a.W));
                }

            var result = homogeneous[degree];
            if (!(Math.Abs(result.W) > 1e-12))
                return false;
            points.Add(new XYZ(result.X / result.W, result.Y / result.W, result.Z / result.W));
        }

        return IsSane(points, controls);
    }

    private static bool IsSane(IReadOnlyList<XYZ> evaluated, IReadOnlyList<XYZ> controls)
    {
        if (evaluated.Count < 2 || evaluated.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            return false;

        var source = controls.Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y)).ToList();
        if (source.Count == 0)
            return false;

        var minX = source.Min(p => p.X); var maxX = source.Max(p => p.X);
        var minY = source.Min(p => p.Y); var maxY = source.Max(p => p.Y);
        var width = Math.Max(maxX - minX, 1e-6);
        var height = Math.Max(maxY - minY, 1e-6);

        // A valid spline can overshoot a little, but an evaluator that emits (0, 0) for a
        // part whose control points are hundreds of units away is corrupt. Two extents
        // leave room for a real curve while rejecting that jump.
        return evaluated.All(p => p.X >= minX - width * 2 && p.X <= maxX + width * 2
            && p.Y >= minY - height * 2 && p.Y <= maxY + height * 2);
    }

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
    // Shared with the reader of old drawings, which meets the same curvature under the same code.
    internal static Point2[] BulgePoints(List<(double X, double Y, double Bulge)> vertices, bool closed)
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
