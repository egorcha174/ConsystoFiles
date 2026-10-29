namespace Consysto.CadPreview.Drawing;

public enum DrawingSnap
{
    /// <summary>Nothing near: the point clicked.</summary>
    None,
    /// <summary>The end of a line or an arc, a corner of a polyline.</summary>
    End,
    Middle,
    /// <summary>The centre of an arc or circle.</summary>
    Center,
    /// <summary>The nearest point on a line or curve.</summary>
    Near,
}

/// <param name="Curve">The piece the point lies on or belongs to; null for a free point.</param>
/// <param name="Segment">With <see cref="DrawingSnap.Near"/>: the index of the flattened segment hit, for its direction.</param>
public readonly record struct DrawingPick(Point2 Point, DrawingSnap Snap, DrawingCurve? Curve, int Segment = 0);

/// <summary>How a part is cut from this drawing: the length of all the outlines and how many times the beam starts.</summary>
/// <param name="Length">In drawing units.</param>
/// <param name="Pierces">The outlines, each joined from the pieces whose ends meet; each one is a pierce.</param>
public readonly record struct CuttingFigures(double Length, int Pierces);

/// <summary>
/// Picking and measuring on a 2D drawing, in drawing units. Built once per drawing; the pieces are put into a grid so a
/// pointer move looks at the few cells under the cursor, not at every piece of a laser nest.
/// </summary>
public sealed class DrawingMeasure
{
    private const int TargetPerCell = 16;

    private readonly List<DrawingCurve> _curves;
    private readonly List<(Point2 Point, DrawingSnap Snap, DrawingCurve Curve)> _snaps = [];
    private readonly Dictionary<long, List<int>> _snapCells = [];
    private readonly Dictionary<long, List<(int Curve, int Segment)>> _segmentCells = [];
    private readonly double _cell;
    private readonly double _minX;
    private readonly double _minY;

    public DrawingMeasure(Drawing2D drawing)
    {
        _curves = drawing.Curves.Count > 0 ? drawing.Curves : FromPicture(drawing);

        var bounds = drawing.Bounds;
        _minX = bounds.IsEmpty ? 0 : bounds.MinX;
        _minY = bounds.IsEmpty ? 0 : bounds.MinY;

        int segments = _curves.Sum(c => c.Points.Length);
        double area = Math.Max(bounds.Width * bounds.Height, 1e-12);
        _cell = Math.Max(Math.Sqrt(area * TargetPerCell / Math.Max(segments, 1)), Math.Max(bounds.Width, bounds.Height) / 4096);
        if (!(_cell > 0) || !double.IsFinite(_cell))
            _cell = 1;

        for (int c = 0; c < _curves.Count; c++)
        {
            var curve = _curves[c];
            var points = curve.Points;
            if (curve.Shape == CurveShape.Arc)
                AddSnap(curve.Center, DrawingSnap.Center, curve);
            if (!curve.Closed)
            {
                AddSnap(points[0], DrawingSnap.End, curve);
                AddSnap(points[^1], DrawingSnap.End, curve);
            }
            if (curve.Shape == CurveShape.Line)
                AddSnap(Lerp(points[0], points[^1], 0.5), DrawingSnap.Middle, curve);
            else if (curve.Shape == CurveShape.Arc && !curve.Closed)
                AddSnap(PointAlong(curve, curve.Length / 2), DrawingSnap.Middle, curve);

            int count = curve.Closed ? points.Length : points.Length - 1;
            for (int s = 0; s < count; s++)
            {
                var a = points[s];
                var b = points[(s + 1) % points.Length];
                foreach (var key in Cells(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)))
                {
                    if (!_segmentCells.TryGetValue(key, out var list))
                        _segmentCells[key] = list = [];
                    list.Add((c, s));
                }
            }
        }
    }

    public bool IsEmpty => _curves.Count == 0;

    /// <summary>The point under the cursor: a characteristic point within reach first, then a line, else the point itself.</summary>
    /// <param name="reach">How far a snap may pull, in drawing units.</param>
    public DrawingPick Pick(Point2 at, double reach)
    {
        DrawingPick? best = null;
        double bestDistance = reach;
        foreach (var key in Cells(at.X - reach, at.Y - reach, at.X + reach, at.Y + reach))
        {
            if (!_snapCells.TryGetValue(key, out var list))
                continue;
            foreach (int index in list)
            {
                var (point, snap, curve) = _snaps[index];
                double distance = Distance(point, at);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = new DrawingPick(point, snap, curve);
                }
            }
        }

        if (best is { } characteristic)
            return characteristic;

        bestDistance = reach;
        foreach (var key in Cells(at.X - reach, at.Y - reach, at.X + reach, at.Y + reach))
        {
            if (!_segmentCells.TryGetValue(key, out var list))
                continue;
            foreach (var (c, s) in list)
            {
                var points = _curves[c].Points;
                var near = Nearest(points[s], points[(s + 1) % points.Length], at);
                double distance = Distance(near, at);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = new DrawingPick(near, DrawingSnap.Near, _curves[c], s);
                }
            }
        }

        return best ?? new DrawingPick(at, DrawingSnap.None, null);
    }

    /// <summary>The circle a pick was taken on or at the centre of: a hole is measured from there.</summary>
    public static DrawingCurve? HoleOf(DrawingPick pick)
        => pick.Curve is { Shape: CurveShape.Arc, Closed: true } circle ? circle : null;

    /// <summary>The straight piece a pick lies on, for angles; null at a free point or the centre of an arc.</summary>
    public static (Point2 A, Point2 B)? SegmentOf(DrawingPick pick)
    {
        if (pick.Curve is not { } curve || pick.Snap is DrawingSnap.None or DrawingSnap.Center)
            return null;

        var points = curve.Points;
        if (curve.Shape == CurveShape.Line)
            return (points[0], points[^1]);
        if (pick.Snap == DrawingSnap.Near)
            return (points[pick.Segment], points[(pick.Segment + 1) % points.Length]);
        return Distance(pick.Point, points[0]) <= Distance(pick.Point, points[^1])
            ? (points[0], points[1])
            : (points[^2], points[^1]);
    }

    /// <summary>
    /// The angle between two picked lines, as a drawing dimensions it: from the point where the lines, or their
    /// extensions, cross, between the rays towards the two picks. Null for parallel lines.
    /// </summary>
    public static (Point2 Vertex, double Degrees)? Angle(DrawingPick first, DrawingPick second)
    {
        if (SegmentOf(first) is not { } one || SegmentOf(second) is not { } other)
            return null;

        var (a1, b1) = one;
        var (a2, b2) = other;

        double d1x = b1.X - a1.X, d1y = b1.Y - a1.Y, d2x = b2.X - a2.X, d2y = b2.Y - a2.Y;
        double cross = d1x * d2y - d1y * d2x;
        if (Math.Abs(cross) < 1e-12 * Math.Max(1, (d1x * d1x + d1y * d1y) * (d2x * d2x + d2y * d2y)))
            return null;

        double t = ((a2.X - a1.X) * d2y - (a2.Y - a1.Y) * d2x) / cross;
        var vertex = new Point2(a1.X + d1x * t, a1.Y + d1y * t);

        var (r1x, r1y) = Ray(vertex, first.Point, d1x, d1y);
        var (r2x, r2y) = Ray(vertex, second.Point, d2x, d2y);
        double degrees = Math.Acos(Math.Clamp(r1x * r2x + r1y * r2y, -1, 1)) * 180 / Math.PI;
        return (vertex, degrees);
    }

    /// <summary>The unit direction from the vertex towards a pick; along the line when the pick is the vertex itself.</summary>
    public static (double X, double Y) Ray(Point2 vertex, Point2 towards, double alongX, double alongY)
    {
        double x = towards.X - vertex.X, y = towards.Y - vertex.Y;
        double length = Math.Sqrt(x * x + y * y);
        if (length > 1e-9)
            return (x / length, y / length);

        length = Math.Sqrt(alongX * alongX + alongY * alongY);
        return length > 0 ? (alongX / length, alongY / length) : (1, 0);
    }

    /// <summary>
    /// The pieces of the drawing joined end to end into outlines. A circle or a closed polyline is an outline by itself;
    /// lines and arcs exported one by one are joined where their ends meet within <paramref name="tolerance"/>.
    /// </summary>
    public static CuttingFigures Cutting(Drawing2D drawing, double tolerance)
    {
        if (drawing.Curves.Count == 0)
            return default;

        var parent = new int[drawing.Curves.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        int Find(int i)
        {
            while (parent[i] != i)
                i = parent[i] = parent[parent[i]];
            return i;
        }

        // Ends are put into cells the size of the tolerance; ends that meet are then in the same or a neighbouring cell
        var ends = new Dictionary<(long, long), List<(int Curve, Point2 Point)>>();
        double length = 0;
        for (int c = 0; c < drawing.Curves.Count; c++)
        {
            var curve = drawing.Curves[c];
            length += curve.Length;
            if (curve.Closed)
                continue;

            foreach (var end in (Point2[])[curve.Points[0], curve.Points[^1]])
            {
                long cx = (long)Math.Floor(end.X / tolerance), cy = (long)Math.Floor(end.Y / tolerance);
                for (long dx = -1; dx <= 1; dx++)
                {
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        if (!ends.TryGetValue((cx + dx, cy + dy), out var near))
                            continue;
                        foreach (var (other, point) in near)
                        {
                            if (Distance(point, end) <= tolerance)
                                parent[Find(c)] = Find(other);
                        }
                    }
                }

                if (!ends.TryGetValue((cx, cy), out var cell))
                    ends[(cx, cy)] = cell = [];
                cell.Add((c, end));
            }
        }

        int outlines = 0;
        for (int i = 0; i < parent.Length; i++)
        {
            if (Find(i) == i)
                outlines++;
        }

        return new CuttingFigures(length, outlines);
    }

    private static List<DrawingCurve> FromPicture(Drawing2D drawing)
        => drawing.Primitives
            .OfType<PolylinePrimitive>()
            .Select(p => new DrawingCurve { Shape = CurveShape.Curve, Points = p.Points, Closed = p.Closed, Length = 0, Layer = p.Layer })
            .ToList();

    private void AddSnap(Point2 point, DrawingSnap snap, DrawingCurve curve)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            return;

        _snaps.Add((point, snap, curve));
        long key = Key(CellX(point.X), CellY(point.Y));
        if (!_snapCells.TryGetValue(key, out var list))
            _snapCells[key] = list = [];
        list.Add(_snaps.Count - 1);
    }

    private IEnumerable<long> Cells(double minX, double minY, double maxX, double maxY)
    {
        long x0 = CellX(minX), x1 = CellX(maxX), y0 = CellY(minY), y1 = CellY(maxY);

        // A segment spanning a large part of the drawing is rare; past this it is simply not indexed beyond its corners
        if ((x1 - x0 + 1) * (y1 - y0 + 1) > 65_536)
        {
            yield return Key(x0, y0);
            yield return Key(x1, y1);
            yield break;
        }

        for (long x = x0; x <= x1; x++)
        {
            for (long y = y0; y <= y1; y++)
                yield return Key(x, y);
        }
    }

    private long CellX(double x) => (long)Math.Floor((x - _minX) / _cell);

    private long CellY(double y) => (long)Math.Floor((y - _minY) / _cell);

    private static long Key(long x, long y) => (x << 32) ^ (y & 0xFFFFFFFF);

    private static Point2 PointAlong(DrawingCurve curve, double distance)
    {
        var points = curve.Points;
        double walked = 0;
        for (int i = 1; i < points.Length; i++)
        {
            double step = Distance(points[i - 1], points[i]);
            if (walked + step >= distance && step > 0)
                return Lerp(points[i - 1], points[i], (distance - walked) / step);
            walked += step;
        }

        return points[^1];
    }

    private static Point2 Nearest(Point2 a, Point2 b, Point2 p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 0)
            return a;

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return new Point2(a.X + dx * t, a.Y + dy * t);
    }

    private static Point2 Lerp(Point2 a, Point2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    internal static double Distance(Point2 a, Point2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
