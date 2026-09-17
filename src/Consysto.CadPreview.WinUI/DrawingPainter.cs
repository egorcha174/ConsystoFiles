using System.Numerics;
using Consysto.CadPreview.Drawing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace Consysto.CadPreview.WinUI;

/// <summary>
/// Device-bound geometry for one drawing. Polylines and fills are merged into one path per colour, and coordinates are
/// stored relative to the drawing's lower-left corner so float precision survives far-away CAD origins.
/// </summary>
internal sealed class DrawingPainter : IDisposable
{
    private readonly List<(CanvasGeometry Geometry, Rgb Color)> _strokes = [];
    private readonly List<(CanvasGeometry Geometry, Rgb Color)> _fills = [];
    private readonly List<TextPrimitive> _texts;
    private readonly CanvasStrokeStyle _hairline = new()
    {
        TransformBehavior = CanvasStrokeTransformBehavior.Hairline,
        LineJoin = CanvasLineJoin.Round,
    };
    private readonly double _originX;
    private readonly double _originY;

    public DrawingPainter(ICanvasResourceCreator resourceCreator, Drawing2D drawing)
    {
        _originX = drawing.Bounds.IsEmpty ? 0 : drawing.Bounds.MinX;
        _originY = drawing.Bounds.IsEmpty ? 0 : drawing.Bounds.MinY;
        _texts = drawing.Primitives.OfType<TextPrimitive>().ToList();

        foreach (var group in drawing.Primitives.OfType<PolylinePrimitive>().GroupBy(p => p.Color))
        {
            using var builder = new CanvasPathBuilder(resourceCreator);
            foreach (var polyline in group)
                AddFigure(builder, polyline.Points, polyline.Closed);
            _strokes.Add((CanvasGeometry.CreatePath(builder), group.Key));
        }

        foreach (var group in drawing.Primitives.OfType<FillPrimitive>().GroupBy(p => p.Color))
        {
            using var builder = new CanvasPathBuilder(resourceCreator);
            foreach (var fill in group)
                AddFigure(builder, fill.Outline, true);
            _fills.Add((CanvasGeometry.CreatePath(builder), group.Key));
        }
    }

    /// <param name="scale">Device-independent pixels per drawing unit.</param>
    /// <param name="origin">Where the drawing's lower-left corner lands, in device-independent pixels.</param>
    public void Paint(CanvasDrawingSession session, float scale, Vector2 origin, Func<Rgb, Color> ink)
    {
        var previous = session.Transform;
        session.Transform = new Matrix3x2(scale, 0, 0, -scale, origin.X, origin.Y) * previous;

        foreach (var (geometry, color) in _fills)
            session.FillGeometry(geometry, ink(color));
        foreach (var (geometry, color) in _strokes)
            session.DrawGeometry(geometry, ink(color), 1f, _hairline);

        foreach (var text in _texts)
        {
            float size = (float)(text.Height * scale);
            if (size < 3f)
                continue;

            var local = Local(text.Position);
            var anchor = new Vector2(origin.X + local.X * scale, origin.Y - local.Y * scale);
            session.Transform = Matrix3x2.CreateRotation((float)-text.Rotation) * Matrix3x2.CreateTranslation(anchor) * previous;
            using var format = new CanvasTextFormat
            {
                FontFamily = "Segoe UI",
                FontSize = size,
                WordWrapping = CanvasWordWrapping.NoWrap,
                VerticalAlignment = CanvasVerticalAlignment.Bottom,
            };
            session.DrawText(text.Value, 0, 0, ink(text.Color), format);
        }

        session.Transform = previous;
    }

    public void Dispose()
    {
        foreach (var (geometry, _) in _strokes)
            geometry.Dispose();
        foreach (var (geometry, _) in _fills)
            geometry.Dispose();
        _hairline.Dispose();
    }

    private void AddFigure(CanvasPathBuilder builder, Point2[] points, bool closed)
    {
        if (points.Length < 2)
            return;

        builder.BeginFigure(Local(points[0]));
        for (int i = 1; i < points.Length; i++)
            builder.AddLine(Local(points[i]));
        builder.EndFigure(closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
    }

    private Vector2 Local(Point2 point) => new((float)(point.X - _originX), (float)(point.Y - _originY));
}
