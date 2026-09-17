namespace Consysto.CadPreview.Drawing;

public readonly record struct Point2(double X, double Y);

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);

    public double Luminance => (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255.0;
}

public struct Bounds2
{
    public double MinX;
    public double MinY;
    public double MaxX;
    public double MaxY;

    public static Bounds2 Empty => new()
    {
        MinX = double.PositiveInfinity,
        MinY = double.PositiveInfinity,
        MaxX = double.NegativeInfinity,
        MaxY = double.NegativeInfinity,
    };

    public readonly bool IsEmpty => MinX > MaxX || MinY > MaxY;

    public readonly double Width => IsEmpty ? 0 : MaxX - MinX;

    public readonly double Height => IsEmpty ? 0 : MaxY - MinY;

    public void Include(Point2 point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            return;
        MinX = Math.Min(MinX, point.X);
        MinY = Math.Min(MinY, point.Y);
        MaxX = Math.Max(MaxX, point.X);
        MaxY = Math.Max(MaxY, point.Y);
    }
}

public enum PrimitiveKind
{
    Polyline,
    Fill,
    Text,
}

public abstract class Primitive(Rgb color, string layer)
{
    public Rgb Color { get; } = color;

    public string Layer { get; } = layer;

    public abstract PrimitiveKind Kind { get; }
}

public sealed class PolylinePrimitive(Rgb color, string layer, Point2[] points, bool closed) : Primitive(color, layer)
{
    public Point2[] Points { get; } = points;

    public bool Closed { get; } = closed;

    public override PrimitiveKind Kind => PrimitiveKind.Polyline;
}

public sealed class FillPrimitive(Rgb color, string layer, Point2[] outline) : Primitive(color, layer)
{
    public Point2[] Outline { get; } = outline;

    public override PrimitiveKind Kind => PrimitiveKind.Fill;
}

public sealed class TextPrimitive(Rgb color, string layer, Point2 position, double height, double rotation, string value) : Primitive(color, layer)
{
    public Point2 Position { get; } = position;

    public double Height { get; } = height;

    /// <summary>Radians, counter-clockwise.</summary>
    public double Rotation { get; } = rotation;

    public string Value { get; } = value;

    public override PrimitiveKind Kind => PrimitiveKind.Text;
}

/// <summary>A flattened, renderer-neutral 2D drawing: everything is polylines, fills and text in world units.</summary>
public sealed class Drawing2D
{
    public required string SourcePath { get; init; }

    public required string Version { get; init; }

    public required string Space { get; init; }

    public List<Primitive> Primitives { get; } = [];

    public HashSet<string> Layers { get; } = [];

    /// <summary>Entity type name → how many were not drawn.</summary>
    public Dictionary<string, int> Skipped { get; } = [];

    public List<string> Notes { get; } = [];

    public Bounds2 Bounds { get; private set; } = Bounds2.Empty;

    public void RecalculateBounds()
    {
        var bounds = Bounds2.Empty;
        foreach (var primitive in Primitives)
        {
            switch (primitive)
            {
                case PolylinePrimitive polyline:
                    foreach (var point in polyline.Points)
                        bounds.Include(point);
                    break;
                case FillPrimitive fill:
                    foreach (var point in fill.Outline)
                        bounds.Include(point);
                    break;
                case TextPrimitive text:
                    bounds.Include(text.Position);
                    break;
            }
        }
        Bounds = bounds;
    }
}
