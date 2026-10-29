using System.Globalization;
using System.Numerics;
using Consysto.CadPreview.Drawing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Consysto.CadPreview.WinUI;

/// <summary>
/// Interactive 2D drawing: drag to pan, wheel to zoom around the cursor, double-click to fit. A toolbar measures the
/// overall size, distances, radii and angles in the units the file states; clicks pick points while a tool is on.
/// </summary>
/// <remarks>Built without XAML so the library ships no resources the host package would have to merge.</remarks>
public sealed partial class CadDrawingView : UserControl
{
    private const float FitMargin = 16f;
    private const float SnapDistance = 8f;
    private const float ClickSlop = 4f;

    private readonly CanvasControl _canvas = new();
    private readonly ToggleButton _boundsButton;
    private readonly ToggleButton[] _toolButtons;
    private readonly Button _clearButton;
    private readonly TextBlock _status = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
    private readonly Border _statusBar;
    private Drawing2D? _drawing;
    private DrawingPainter? _painter;
    private float _scale = 1f;
    private Vector2 _origin;
    private bool _fitPending = true;
    // Consysto fork: the quick-preview overlay grows the card open, and the canvas' first Draw can land mid-animation,
    // fitting to a width narrower than the settled one — the drawing then sits off-centre for good, because SizeChanged
    // only repaints. Re-fit on every size change until the person actually zooms or pans by hand.
    private bool _autoFit = true;
    private bool _dragging;
    private bool _pressMoved;
    private Vector2 _lastPointer;
    private Vector2 _pressPointer;

    private CadMeasureText _text = new();
    private MeasureTool _tool;
    private DrawingMeasure? _measure;
    private bool _measurePreparing;
    private readonly List<DrawingPick> _pending = [];
    private readonly List<Measurement> _measurements = [];
    private DrawingPick? _hover;
    private string? _notice;

    public CadDrawingView()
    {
        // A transparent brush keeps the whole area hit-testable for panning and zooming.
        Background = new SolidColorBrush(Colors.Transparent);

        _boundsButton = CadMeshView.ToolbarToggle("⬚");
        _boundsButton.Click += (_, _) => UpdateOverlay();
        _toolButtons = [CadMeshView.ToolbarToggle("↔"), CadMeshView.ToolbarToggle("⌀"), CadMeshView.ToolbarToggle("∠")];
        for (int i = 0; i < _toolButtons.Length; i++)
        {
            var tool = (MeasureTool)(i + 1);
            _toolButtons[i].Click += (_, _) => SelectTool(_tool == tool ? MeasureTool.None : tool);
        }

        _clearButton = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 4), MinWidth = 32 };
        _clearButton.Click += (_, _) => ClearMeasurements();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        toolbar.Children.Add(_boundsButton);
        foreach (var button in _toolButtons)
            toolbar.Children.Add(button);
        toolbar.Children.Add(_clearButton);

        _statusBar = new Border
        {
            Child = _status,
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = CadMeshView.ThemeBrush("AcrylicInAppFillColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
        };

        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(toolbar);
        root.Children.Add(_statusBar);
        Content = root;
        ApplyText();

        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (_, _) => ResetPainter();
        _canvas.SizeChanged += (_, _) =>
        {
            if (_autoFit)
                _fitPending = true;
            _canvas.Invalidate();
        };

        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerExited += (_, _) => SetHover(null);
        PointerCaptureLost += (_, _) => _dragging = false;
        DoubleTapped += (_, _) =>
        {
            // While measuring, a double-click is two picks, not a request to fit
            if (_tool == MeasureTool.None)
                Fit();
        };
        ActualThemeChanged += (_, _) => _canvas.Invalidate();

        // Win2D controls must be detached explicitly, otherwise the device and the control keep each other alive.
        Unloaded += (_, _) =>
        {
            ResetPainter();
            _canvas.RemoveFromVisualTree();
        };
    }

    private enum MeasureTool
    {
        None,
        Distance,
        Radius,
        Angle,
    }

    private enum MeasurementKind
    {
        Distance,
        Circle,
        Angle,
    }

    private sealed record Measurement(MeasurementKind Kind, Point2[] Points, DrawingCurve? Circle, string Label, string Detail, DrawingCurve[]? Holes = null);

    public Drawing2D? Drawing
    {
        get => _drawing;
        set
        {
            _drawing = value;
            ResetPainter();
            _autoFit = true;
            _fitPending = true;
            _measure = null;
            _measurePreparing = false;
            _pending.Clear();
            _measurements.Clear();
            _hover = null;
            _notice = null;
            if (_tool != MeasureTool.None)
                PrepareMeasure();
            UpdateOverlay();
        }
    }

    /// <summary>The words of the measuring tools, in the host's language.</summary>
    public CadMeasureText MeasureText
    {
        get => _text;
        set
        {
            _text = value ?? new CadMeasureText();
            ApplyText();
            UpdateOverlay();
        }
    }

    public void Fit()
    {
        _autoFit = true;
        _fitPending = true;
        _canvas.Invalidate();
    }

    internal static Color Ink(Rgb rgb, bool darkTheme)
    {
        double luminance = rgb.Luminance;
        if (darkTheme)
            return luminance < 0.25 ? Color.FromArgb(255, 0xE6, 0xE6, 0xE6) : Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
        return luminance > 0.8 ? Color.FromArgb(255, 0x1F, 0x1F, 0x1F) : Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
    }

    private void ApplyText()
    {
        ToolTipService.SetToolTip(_boundsButton, _text.BoundsTool);
        ToolTipService.SetToolTip(_toolButtons[0], _text.DistanceTool);
        ToolTipService.SetToolTip(_toolButtons[1], _text.RadiusTool);
        ToolTipService.SetToolTip(_toolButtons[2], _text.AngleTool);
        ToolTipService.SetToolTip(_clearButton, _text.Clear);
        AutomationProperties.SetName(_boundsButton, _text.BoundsTool);
        AutomationProperties.SetName(_toolButtons[0], _text.DistanceTool);
        AutomationProperties.SetName(_toolButtons[1], _text.RadiusTool);
        AutomationProperties.SetName(_toolButtons[2], _text.AngleTool);
        AutomationProperties.SetName(_clearButton, _text.Clear);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_drawing is null || _drawing.Bounds.IsEmpty)
            return;

        if (_fitPending && sender.ActualWidth > 0 && sender.ActualHeight > 0)
        {
            ApplyFit((float)sender.ActualWidth, (float)sender.ActualHeight);
            _fitPending = false;
        }

        _painter ??= new DrawingPainter(sender, _drawing);
        bool dark = ActualTheme == ElementTheme.Dark;
        _painter.Paint(args.DrawingSession, _scale, _origin, rgb => Ink(rgb, dark));
        DrawOverlay(args.DrawingSession);
    }

    private void ApplyFit(float width, float height)
    {
        var bounds = _drawing!.Bounds;
        float drawingWidth = (float)Math.Max(bounds.Width, 1e-6);
        float drawingHeight = (float)Math.Max(bounds.Height, 1e-6);
        _scale = Math.Max(1e-6f, Math.Min((width - 2 * FitMargin) / drawingWidth, (height - 2 * FitMargin) / drawingHeight));
        _origin = new Vector2((width - drawingWidth * _scale) / 2, (height + drawingHeight * _scale) / 2);
    }

    // The painter places the lower-left corner of the drawing at the origin, Y up
    private Vector2 ToScreen(Point2 point)
    {
        var bounds = _drawing!.Bounds;
        return new Vector2(
            _origin.X + (float)((point.X - bounds.MinX) * _scale),
            _origin.Y - (float)((point.Y - bounds.MinY) * _scale));
    }

    private Point2 ToDrawing(Vector2 screen)
    {
        var bounds = _drawing!.Bounds;
        return new Point2(bounds.MinX + (screen.X - _origin.X) / _scale, bounds.MinY - (screen.Y - _origin.Y) / _scale);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        float factor = MathF.Pow(1.2f, point.Properties.MouseWheelDelta / 120f);
        var cursor = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _origin = cursor - (cursor - _origin) * factor;
        _scale *= factor;
        _autoFit = false;
        _fitPending = false;
        _canvas.Invalidate();
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed)
            return;

        _dragging = true;
        _pressMoved = false;
        _lastPointer = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _pressPointer = _lastPointer;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        var current = new Vector2((float)position.X, (float)position.Y);
        if (!_dragging)
        {
            if (_tool != MeasureTool.None && _measure is { } measure && _drawing is not null)
                SetHover(measure.Pick(ToDrawing(current), SnapDistance / _scale));
            return;
        }

        // A click that wobbles a little still measures; only a real drag pans.
        if (!_pressMoved && Vector2.Distance(current, _pressPointer) < ClickSlop)
            return;
        _pressMoved = true;

        _origin += current - _lastPointer;
        _lastPointer = current;
        _autoFit = false;
        _fitPending = false;
        _canvas.Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        ReleasePointerCapture(e.Pointer);

        if (!_pressMoved && _tool != MeasureTool.None && _measure is { } measure && _drawing is not null)
        {
            var position = e.GetCurrentPoint(this).Position;
            var pick = measure.Pick(ToDrawing(new Vector2((float)position.X, (float)position.Y)), SnapDistance / _scale);
            AddPick(pick, exact: e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control));
        }
    }

    private void SetHover(DrawingPick? pick)
    {
        if (pick == _hover)
            return;
        _hover = pick;
        _canvas.Invalidate();
    }

    private void SelectTool(MeasureTool tool)
    {
        _tool = tool;
        for (int i = 0; i < _toolButtons.Length; i++)
            _toolButtons[i].IsChecked = (int)tool == i + 1;

        _pending.Clear();
        _hover = null;
        _notice = null;
        if (tool != MeasureTool.None)
            PrepareMeasure();
        UpdateOverlay();
    }

    // A laser nest holds tens of thousands of pieces; they are put in order once per drawing, off the UI thread.
    private void PrepareMeasure()
    {
        if (_measure is not null || _measurePreparing || _drawing is not { } drawing)
            return;

        _measurePreparing = true;
        _ = Task.Run(() => new DrawingMeasure(drawing)).ContinueWith(task => DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_drawing, drawing))
                return;
            _measurePreparing = false;
            _measure = task.IsCompletedSuccessfully ? task.Result : null;
            UpdateOverlay();
        }), TaskScheduler.Default);
    }

    private void ClearMeasurements()
    {
        _pending.Clear();
        _measurements.Clear();
        _notice = null;
        UpdateOverlay();
    }

    /// <param name="exact">Ctrl was held: the point itself, even on a hole.</param>
    private void AddPick(DrawingPick pick, bool exact)
    {
        _notice = null;
        switch (_tool)
        {
            case MeasureTool.Distance:
                // A hole is measured from its centre, the way drawings dimension holes
                if (!exact && DrawingMeasure.HoleOf(pick) is { } hole)
                    pick = pick with { Point = hole.Center, Snap = DrawingSnap.Center };
                _pending.Add(pick);
                if (_pending.Count == 2)
                    AddMeasurement(DistanceBetween(_pending[0], _pending[1], exact));
                break;

            case MeasureTool.Radius:
                if (pick.Curve is { Shape: CurveShape.Arc } arc)
                    AddMeasurement(CircleMeasurement(arc));
                else
                    _notice = _text.DrawingNoArc;
                break;

            case MeasureTool.Angle:
                if (DrawingMeasure.Direction(pick) is null)
                {
                    _notice = _text.DrawingAnglePrompt;
                    break;
                }

                _pending.Add(pick);
                if (_pending.Count == 2)
                {
                    var (ax, ay) = DrawingMeasure.Direction(_pending[0])!.Value;
                    var (bx, by) = DrawingMeasure.Direction(_pending[1])!.Value;
                    double degrees = Math.Acos(Math.Clamp(Math.Abs(ax * bx + ay * by), 0, 1)) * 180 / Math.PI;
                    string value = degrees.ToString("0.#", CultureInfo.CurrentCulture);
                    AddMeasurement(new Measurement(
                        MeasurementKind.Angle,
                        [_pending[0].Point, _pending[1].Point],
                        null,
                        value + "°",
                        string.Format(CultureInfo.CurrentCulture, _text.AngleFormat, value)));
                }

                break;
        }

        UpdateOverlay();
    }

    private void AddMeasurement(Measurement measurement)
    {
        _pending.Clear();
        _measurements.Add(measurement);
    }

    private double UnitScale => _drawing?.MillimetersPerUnit ?? 1;

    private string UnitName => _drawing?.MillimetersPerUnit is null ? _text.DrawingUnits : _text.Millimeters;

    private Measurement DistanceBetween(DrawingPick first, DrawingPick second, bool exact)
    {
        var a = first.Point;
        var b = second.Point;
        double dx = Math.Abs(b.X - a.X) * UnitScale, dy = Math.Abs(b.Y - a.Y) * UnitScale;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        string length = Format(distance);

        var p = exact ? null : DrawingMeasure.HoleOf(first);
        var q = exact ? null : DrawingMeasure.HoleOf(second);
        DrawingCurve[]? holes = (p, q) switch
        {
            ({ } x, { } y) => [x, y],
            ({ } x, null) => [x],
            (null, { } y) => [y],
            _ => null,
        };

        string detail = p is not null && q is not null
            ? string.Format(
                CultureInfo.CurrentCulture,
                _text.DrawingCentresFormat,
                length,
                UnitName,
                Format(Math.Max(0, distance - (p.Radius + q.Radius) * UnitScale)),
                Format(dx),
                Format(dy))
            : string.Format(CultureInfo.CurrentCulture, _text.DrawingDistanceFormat, length, UnitName, Format(dx), Format(dy));

        return new Measurement(MeasurementKind.Distance, [a, b], null, length + " " + UnitName, detail, holes);
    }

    private Measurement CircleMeasurement(DrawingCurve arc)
    {
        double radius = arc.Radius * UnitScale;
        string r = Format(radius);
        string d = Format(radius * 2);
        return new Measurement(
            MeasurementKind.Circle,
            [arc.Center],
            arc,
            "R " + r + " · ⌀ " + d,
            string.Format(CultureInfo.CurrentCulture, _text.RadiusFormat, r, d, UnitName));
    }

    private static string Format(double value) => CadMeshView.FormatLength((float)value);

    private void UpdateOverlay()
    {
        var lines = new List<string>();
        if (_drawing is { Bounds.IsEmpty: false } drawing)
        {
            if (_boundsButton.IsChecked == true)
            {
                lines.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    _text.DrawingBoundsFormat,
                    Format(drawing.Bounds.Width * UnitScale),
                    Format(drawing.Bounds.Height * UnitScale),
                    UnitName));
            }

            if (_measurements.Count > 0)
                lines.Add(_measurements[^1].Detail);

            if (_tool != MeasureTool.None)
            {
                if (_measurePreparing)
                    lines.Add(_text.Preparing);
                else if (_notice is not null)
                    lines.Add(_notice);
                else if (_measurements.Count == 0 || _pending.Count > 0)
                    lines.Add(_tool switch
                    {
                        MeasureTool.Distance => _text.DrawingDistancePrompt,
                        MeasureTool.Radius => _text.DrawingRadiusPrompt,
                        _ => _text.DrawingAnglePrompt,
                    });
            }

            if (lines.Count > 0 && drawing.MillimetersPerUnit is null)
                lines.Add(_text.DrawingUnitsUnknown);
        }

        _status.Text = string.Join(Environment.NewLine, lines);
        _statusBar.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _canvas.Invalidate();
    }

    private void DrawOverlay(CanvasDrawingSession session)
    {
        var accent = Application.Current?.Resources.TryGetValue("SystemAccentColor", out var value) == true && value is Color color
            ? color
            : Color.FromArgb(255, 0, 120, 215);
        bool dark = ActualTheme == ElementTheme.Dark;
        var labelBack = dark ? Color.FromArgb(220, 32, 32, 32) : Color.FromArgb(230, 255, 255, 255);
        var labelInk = dark ? Colors.White : Colors.Black;
        using var format = new CanvasTextFormat { FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };

        if (_boundsButton.IsChecked == true && _drawing is { Bounds.IsEmpty: false } drawing)
        {
            var corner = ToScreen(new Point2(drawing.Bounds.MinX, drawing.Bounds.MaxY));
            var opposite = ToScreen(new Point2(drawing.Bounds.MaxX, drawing.Bounds.MinY));
            session.DrawRectangle(corner.X, corner.Y, opposite.X - corner.X, opposite.Y - corner.Y,
                Color.FromArgb(160, accent.R, accent.G, accent.B), 1f, CadMeshView.DashedStroke);
        }

        foreach (var measurement in _measurements)
        {
            Vector2 anchor;
            if (measurement.Kind == MeasurementKind.Circle && measurement.Circle is { } circle)
            {
                DrawArc(session, circle, accent);
                anchor = ToScreen(circle.Center);
                CadMeshView.DrawCross(session, anchor, accent);
            }
            else
            {
                var a = ToScreen(measurement.Points[0]);
                var b = ToScreen(measurement.Points[1]);
                if (measurement.Kind == MeasurementKind.Distance)
                    session.DrawLine(a, b, accent, 2f);
                else
                    session.DrawLine(a, b, accent, 1f, CadMeshView.DashedStroke);
                CadMeshView.DrawPoint(session, a, accent);
                CadMeshView.DrawPoint(session, b, accent);
                anchor = (a + b) / 2;
            }

            foreach (var hole in measurement.Holes ?? [])
                DrawArc(session, hole, accent);

            CadMeshView.DrawLabel(session, format, measurement.Label, anchor, labelBack, labelInk);
        }

        foreach (var pick in _pending)
            CadMeshView.DrawPoint(session, ToScreen(pick.Point), accent);

        if (_hover is { } hover)
        {
            if (_tool != MeasureTool.Angle && hover.Curve is { Shape: CurveShape.Arc } arc
                && (_tool == MeasureTool.Radius || DrawingMeasure.HoleOf(hover) is not null))
                DrawArc(session, arc, accent);

            var at = ToScreen(hover.Point);
            switch (hover.Snap)
            {
                case DrawingSnap.End:
                    session.DrawRectangle(at.X - 5, at.Y - 5, 10, 10, accent, 2f);
                    break;
                case DrawingSnap.Middle:
                    session.DrawLine(at + new Vector2(-6, 5), at + new Vector2(6, 5), accent, 2f);
                    session.DrawLine(at + new Vector2(-6, 5), at + new Vector2(0, -6), accent, 2f);
                    session.DrawLine(at + new Vector2(6, 5), at + new Vector2(0, -6), accent, 2f);
                    break;
                case DrawingSnap.Center:
                    CadMeshView.DrawCross(session, at, accent);
                    session.DrawCircle(at, 6, accent, 1.5f);
                    break;
                case DrawingSnap.Near:
                    session.DrawLine(at - new Vector2(6, 0), at + new Vector2(6, 0), accent, 2f);
                    session.DrawLine(at - new Vector2(0, 6), at + new Vector2(0, 6), accent, 2f);
                    break;
                default:
                    session.DrawCircle(at, 4, accent, 1.5f);
                    break;
            }
        }
    }

    private void DrawArc(CanvasDrawingSession session, DrawingCurve arc, Color color)
    {
        var points = arc.Points;
        var previous = ToScreen(points[0]);
        int count = arc.Closed ? points.Length : points.Length - 1;
        for (int i = 1; i <= count; i++)
        {
            var next = ToScreen(points[i % points.Length]);
            session.DrawLine(previous, next, color, 2f);
            previous = next;
        }
    }

    private void ResetPainter()
    {
        _painter?.Dispose();
        _painter = null;
    }
}
