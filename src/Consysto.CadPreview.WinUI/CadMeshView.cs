using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Consysto.CadPreview.Mesh;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Consysto.CadPreview.WinUI;

/// <summary>
/// Interactive 3D mesh: drag to orbit, right/middle or Shift+drag to pan, wheel to zoom around the cursor,
/// double-click for the home view. A toolbar measures the overall size, distances, radii and angles; clicks pick
/// points while a tool is on, drags still turn the model.
/// </summary>
/// <remarks>
/// Frames come from the software <see cref="MeshRasterizer"/> and are shown through Win2D, the same path the 2D view
/// uses: no Direct3D interop and no third-party XAML types, which Native AOT builds of the host cannot activate.
/// </remarks>
public sealed partial class CadMeshView : UserControl
{
    private const float DegreesPerDip = 0.45f;

    // While the model moves, slower frames drop the resolution; once it rests it is redrawn supersampled.
    private const double InteractiveFrameBudgetMs = 30;
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(160);

    private const float SnapDistance = 8f;
    private const float ClickSlop = 4f;

    // Hover feedback scans every triangle on each pointer move; above this a click still measures, the hover does not.
    private const int MaximumHoverTriangles = 500_000;

    private readonly CanvasControl _canvas = new();
    private readonly MeshRasterizer _rasterizer = new();
    private readonly DispatcherQueueTimer _settleTimer;
    private readonly ToggleButton _boundsButton;
    private readonly ToggleButton[] _toolButtons;
    private readonly Button _clearButton;
    private readonly TextBlock _status = new() { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
    private readonly Border _statusBar;
    private Mesh3D? _mesh;
    private MeshView _view = MeshView.Home;
    private bool _interacting;
    private float _interactiveResolution = 1f;
    private DragMode _drag;
    private Vector2 _lastPointer;
    private Vector2 _pressPointer;
    private bool _pressMoved;

    // The last frame, kept so overlays and hover marks repaint without rasterizing the model again.
    private CanvasBitmap? _frame;
    private (MeshView View, int Width, int Height, bool Interacting) _frameKey;

    private CadMeasureText _text = new();
    private MeasureTool _tool;
    private MeshMeasure? _measure;
    private bool _measurePreparing;
    private readonly List<MeshPick> _pending = [];
    private readonly List<Measurement> _measurements = [];
    private MeshPick? _hover;
    private string? _notice;

    public CadMeshView()
    {
        // A transparent brush keeps the whole area hit-testable.
        Background = new SolidColorBrush(Colors.Transparent);

        _boundsButton = ToolbarToggle("⬚");
        _boundsButton.Click += (_, _) => UpdateOverlay();
        _toolButtons = [ToolbarToggle("↔"), ToolbarToggle("⌀"), ToolbarToggle("∠")];
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
            Background = ThemeBrush("AcrylicInAppFillColorDefaultBrush"),
            Visibility = Visibility.Collapsed,
        };

        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(toolbar);
        root.Children.Add(_statusBar);
        Content = root;
        ApplyText();

        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();
        _canvas.CreateResources += (_, _) => DropFrame();

        _settleTimer = DispatcherQueue.CreateTimer();
        _settleTimer.Interval = SettleDelay;
        _settleTimer.IsRepeating = false;
        _settleTimer.Tick += (_, _) =>
        {
            _interacting = false;
            _canvas.Invalidate();
        };

        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerExited += (_, _) => SetHover(null);
        PointerCaptureLost += (_, _) => _drag = DragMode.None;
        DoubleTapped += OnDoubleTapped;
        ActualThemeChanged += (_, _) => _canvas.Invalidate();

        // Win2D controls must be detached explicitly, otherwise the device and the control keep each other alive.
        Unloaded += (_, _) =>
        {
            _settleTimer.Stop();
            DropFrame();
            _canvas.RemoveFromVisualTree();
        };
    }

    private enum DragMode
    {
        None,
        Orbit,
        Pan,
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

    /// <param name="Label">The short text drawn on the model.</param>
    /// <param name="Detail">The full line for the status bar, which can be selected and copied.</param>
    private sealed record Measurement(MeasurementKind Kind, Vector3[] Points, MeshCircle? Circle, string Label, string Detail);

    public Mesh3D? Mesh
    {
        get => _mesh;
        set
        {
            _mesh = value;
            _measure = null;
            _measurePreparing = false;
            _pending.Clear();
            _measurements.Clear();
            _hover = null;
            _notice = null;
            ResetView();
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

    public void ResetView()
    {
        _view = MeshView.Home;
        _interacting = false;
        _canvas.Invalidate();
    }

    private static ToggleButton ToolbarToggle(string glyph)
        => new() { Content = glyph, Padding = new Thickness(8, 2, 8, 4), MinWidth = 32 };

    private static Brush? ThemeBrush(string key)
        => Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as Brush : null;

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
        if (_mesh is null || sender.ActualWidth < 1 || sender.ActualHeight < 1)
            return;

        // Physical pixels keep the model sharp on high-DPI screens; pan is kept in DIPs and scaled the same way.
        float pixelsPerDip = (float)(sender.XamlRoot?.RasterizationScale ?? 1.0) * (_interacting ? _interactiveResolution : 1f);
        int width = Math.Max(1, (int)(sender.ActualWidth * pixelsPerDip));
        int height = Math.Max(1, (int)(sender.ActualHeight * pixelsPerDip));
        var key = (_view, width, height, _interacting);
        if (_frame is null || key != _frameKey)
        {
            var view = _view with { PanX = _view.PanX * pixelsPerDip, PanY = _view.PanY * pixelsPerDip };
            var watch = Stopwatch.StartNew();
            var image = _rasterizer.RenderFrame(_mesh, width, height, view, MeshFit.Sphere, supersample: _interacting ? 1 : 2);
            if (_interacting)
                AdaptResolution(watch.Elapsed.TotalMilliseconds);

            _frame?.Dispose();
            _frame = CanvasBitmap.CreateFromBytes(sender, image.Pixels, image.Width, image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
            _frameKey = key;
        }

        args.DrawingSession.DrawImage(
            _frame,
            new Rect(0, 0, sender.ActualWidth, sender.ActualHeight),
            new Rect(0, 0, _frame.SizeInPixels.Width, _frame.SizeInPixels.Height),
            1f,
            CanvasImageInterpolation.Linear);

        DrawOverlay(args.DrawingSession, Projection());
    }

    private void DropFrame()
    {
        _frame?.Dispose();
        _frame = null;
    }

    // The mapping the frame was drawn with, in DIPs: overlays and picks line up with the picture.
    private MeshProjection Projection()
        => MeshProjection.Create(_mesh!, (float)_canvas.ActualWidth, (float)_canvas.ActualHeight, _view, MeshFit.Sphere);

    // Frame time grows with the pixel count; step one octave at a time so the picture does not flicker between sizes.
    private void AdaptResolution(double frameMilliseconds)
    {
        if (frameMilliseconds > InteractiveFrameBudgetMs && _interactiveResolution > 0.25f)
            _interactiveResolution /= 2;
        else if (frameMilliseconds < InteractiveFrameBudgetMs / 4 && _interactiveResolution < 1f)
            _interactiveResolution *= 2;
    }

    private void Interact()
    {
        _interacting = true;
        _hover = null;
        _settleTimer.Stop();
        _settleTimer.Start();
        _canvas.Invalidate();
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        float factor = MathF.Pow(1.2f, point.Properties.MouseWheelDelta / 120f);

        // Keep the point under the cursor in place: pan is measured from the middle of the control.
        var cursor = new Vector2((float)(point.Position.X - ActualWidth / 2), (float)(point.Position.Y - ActualHeight / 2));
        var pan = new Vector2(_view.PanX, _view.PanY);
        pan = cursor - (cursor - pan) * factor;
        _view = _view with { Zoom = Math.Clamp(_view.Zoom * factor, 0.05f, 500f), PanX = pan.X, PanY = pan.Y };

        Interact();
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var properties = point.Properties;
        _drag = properties.IsLeftButtonPressed ? DragMode.Orbit
            : properties.IsMiddleButtonPressed || properties.IsRightButtonPressed ? DragMode.Pan
            : DragMode.None;

        // Touchpads have no middle button: Shift turns the left drag into a pan.
        if (_drag == DragMode.Orbit && e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
            _drag = DragMode.Pan;
        if (_drag == DragMode.None)
            return;

        _lastPointer = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _pressPointer = _lastPointer;
        _pressMoved = false;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var position = e.GetCurrentPoint(this).Position;
        var current = new Vector2((float)position.X, (float)position.Y);
        if (_drag == DragMode.None)
        {
            if (_tool != MeasureTool.None && _measure is { } measure && _mesh!.TriangleCount <= MaximumHoverTriangles)
                SetHover(measure.Pick(Projection(), current, SnapDistance));
            return;
        }

        // A click that wobbles a little still measures; only a real drag turns the model.
        if (!_pressMoved && Vector2.Distance(current, _pressPointer) < ClickSlop)
            return;
        _pressMoved = true;

        var delta = current - _lastPointer;
        _lastPointer = current;

        // Turntable orbit: dragging right turns the model's front to the right, dragging down shows more of its top.
        _view = _drag == DragMode.Orbit
            ? _view with
            {
                AzimuthDegrees = _view.AzimuthDegrees - delta.X * DegreesPerDip,
                ElevationDegrees = Math.Clamp(_view.ElevationDegrees + delta.Y * DegreesPerDip, -89f, 89f),
            }
            : _view with { PanX = _view.PanX + delta.X, PanY = _view.PanY + delta.Y };

        Interact();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragMode.None)
            return;

        bool click = _drag == DragMode.Orbit && !_pressMoved;
        _drag = DragMode.None;
        ReleasePointerCapture(e.Pointer);

        if (click && _tool != MeasureTool.None && _measure is { } measure && _mesh is not null)
        {
            var position = e.GetCurrentPoint(this).Position;
            if (measure.Pick(Projection(), new Vector2((float)position.X, (float)position.Y), SnapDistance) is { } pick)
                AddPick(measure, pick);
        }
    }

    // While measuring, a double-click is two picks, not a request for the home view.
    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_tool == MeasureTool.None)
            ResetView();
    }

    private void SetHover(MeshPick? pick)
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

    // Recovering the edges of a large part takes a second or two; it happens once per model, off the UI thread.
    private void PrepareMeasure()
    {
        if (_measure is not null || _measurePreparing || _mesh is not { IsEmpty: false } mesh)
            return;

        _measurePreparing = true;
        _ = Task.Run(() => new MeshMeasure(mesh)).ContinueWith(task => DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(_mesh, mesh))
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

    private void AddPick(MeshMeasure measure, MeshPick pick)
    {
        _notice = null;
        switch (_tool)
        {
            case MeasureTool.Distance:
                _pending.Add(pick);
                if (_pending.Count == 2)
                    AddMeasurement(DistanceBetween(_pending[0].Point, _pending[1].Point));
                break;

            case MeasureTool.Angle:
                _pending.Add(pick);
                if (_pending.Count == 2)
                {
                    float degrees = measure.Angle(_pending[0], _pending[1]);
                    string value = degrees.ToString("0.#", CultureInfo.CurrentCulture);
                    AddMeasurement(new Measurement(
                        MeasurementKind.Angle,
                        [_pending[0].Point, _pending[1].Point],
                        null,
                        value + "°",
                        string.Format(CultureInfo.CurrentCulture, _text.AngleFormat, value)));
                }

                break;

            case MeasureTool.Radius:
                // One click on a round edge or face is enough; elsewhere the circle is taken through three points.
                if (_pending.Count == 0 && measure.FindCircle(pick) is { } found)
                {
                    AddMeasurement(CircleMeasurement(found));
                    break;
                }

                _pending.Add(pick);
                if (_pending.Count == 3)
                {
                    if (MeshMeasure.CircleThrough(_pending[0].Point, _pending[1].Point, _pending[2].Point) is { } circle)
                        AddMeasurement(CircleMeasurement(circle));
                    else
                        _pending.Clear();
                }
                else
                {
                    _notice = string.Format(CultureInfo.CurrentCulture, _text.RadiusPointsPrompt, _pending.Count);
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

    private Measurement DistanceBetween(Vector3 a, Vector3 b)
    {
        var delta = (b - a) * (float)_mesh!.Units.Millimeters;
        string length = FormatLength(delta.Length());
        return new Measurement(
            MeasurementKind.Distance,
            [a, b],
            null,
            length + " " + _text.Millimeters,
            string.Format(
                CultureInfo.CurrentCulture,
                _text.DistanceFormat,
                length,
                _text.Millimeters,
                FormatLength(MathF.Abs(delta.X)),
                FormatLength(MathF.Abs(delta.Y)),
                FormatLength(MathF.Abs(delta.Z))));
    }

    private Measurement CircleMeasurement(MeshCircle circle)
    {
        float radius = circle.Radius * (float)_mesh!.Units.Millimeters;
        string r = FormatLength(radius);
        string d = FormatLength(radius * 2);
        return new Measurement(
            MeasurementKind.Circle,
            [circle.Center],
            circle,
            "R " + r + " · ⌀ " + d,
            string.Format(CultureInfo.CurrentCulture, _text.RadiusFormat, r, d, _text.Millimeters));
    }

    private static string FormatLength(float millimeters)
    {
        string format = millimeters >= 1000 ? "0.#" : millimeters >= 1 ? "0.##" : "0.###";
        return millimeters.ToString(format, CultureInfo.CurrentCulture);
    }

    private void UpdateOverlay()
    {
        var lines = new List<string>();
        if (_mesh is { IsEmpty: false } mesh)
        {
            if (_boundsButton.IsChecked == true)
            {
                var size = (mesh.Max - mesh.Min) * (float)mesh.Units.Millimeters;
                lines.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    _text.BoundsFormat,
                    FormatLength(size.X),
                    FormatLength(size.Y),
                    FormatLength(size.Z),
                    _text.Millimeters));
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
                        MeasureTool.Distance => _text.DistancePrompt,
                        MeasureTool.Radius => _text.RadiusPrompt,
                        _ => _text.AnglePrompt,
                    });
            }

            if (lines.Count > 0 && !mesh.Units.IsStated)
                lines.Add(_text.UnitsAssumed);
        }

        _status.Text = string.Join(Environment.NewLine, lines);
        _statusBar.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _canvas.Invalidate();
    }

    private void DrawOverlay(CanvasDrawingSession session, MeshProjection projection)
    {
        var accent = Application.Current?.Resources.TryGetValue("SystemAccentColor", out var value) == true && value is Color color
            ? color
            : Color.FromArgb(255, 0, 120, 215);
        bool dark = ActualTheme == ElementTheme.Dark;
        var labelBack = dark ? Color.FromArgb(220, 32, 32, 32) : Color.FromArgb(230, 255, 255, 255);
        var labelInk = dark ? Colors.White : Colors.Black;
        using var format = new CanvasTextFormat { FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };

        if (_boundsButton.IsChecked == true && _mesh is { IsEmpty: false } mesh)
            DrawBox(session, projection, mesh.Min, mesh.Max, Color.FromArgb(160, accent.R, accent.G, accent.B));

        foreach (var measurement in _measurements)
        {
            Vector2 anchor;
            switch (measurement.Kind)
            {
                case MeasurementKind.Circle when measurement.Circle is { } circle:
                    DrawCircle(session, projection, circle, accent);
                    anchor = projection.ToScreen(circle.Center);
                    DrawCross(session, anchor, accent);
                    break;
                default:
                    var a = projection.ToScreen(measurement.Points[0]);
                    var b = projection.ToScreen(measurement.Points[1]);
                    if (measurement.Kind == MeasurementKind.Distance)
                        session.DrawLine(a, b, accent, 2f);
                    else
                        session.DrawLine(a, b, accent, 1f, DashedStroke);
                    DrawPoint(session, a, accent);
                    DrawPoint(session, b, accent);
                    anchor = (a + b) / 2;
                    break;
            }

            DrawLabel(session, format, measurement.Label, anchor, labelBack, labelInk);
        }

        foreach (var pick in _pending)
            DrawPoint(session, projection.ToScreen(pick.Point), accent);

        if (_hover is { } hover)
        {
            var at = projection.ToScreen(hover.Point);
            switch (hover.Snap)
            {
                case MeshSnap.Vertex:
                    session.DrawRectangle(at.X - 5, at.Y - 5, 10, 10, accent, 2f);
                    break;
                case MeshSnap.Edge:
                    session.DrawLine(at - new Vector2(6, 0), at + new Vector2(6, 0), accent, 2f);
                    session.DrawLine(at - new Vector2(0, 6), at + new Vector2(0, 6), accent, 2f);
                    break;
                default:
                    session.DrawCircle(at, 4, accent, 1.5f);
                    break;
            }
        }
    }

    private static readonly CanvasStrokeStyle DashedStroke = new() { DashStyle = CanvasDashStyle.Dash };

    private static void DrawPoint(CanvasDrawingSession session, Vector2 at, Color color)
    {
        session.FillCircle(at, 4, color);
        session.DrawCircle(at, 4, Colors.White, 1f);
    }

    private static void DrawCross(CanvasDrawingSession session, Vector2 at, Color color)
    {
        session.DrawLine(at - new Vector2(5, 5), at + new Vector2(5, 5), color, 1.5f);
        session.DrawLine(at - new Vector2(5, -5), at + new Vector2(5, -5), color, 1.5f);
    }

    private static void DrawCircle(CanvasDrawingSession session, MeshProjection projection, MeshCircle circle, Color color)
    {
        var u = Vector3.Normalize(MathF.Abs(circle.Normal.X) < 0.9f ? Vector3.Cross(circle.Normal, Vector3.UnitX) : Vector3.Cross(circle.Normal, Vector3.UnitY));
        var v = Vector3.Cross(circle.Normal, u);
        const int Segments = 72;
        var previous = projection.ToScreen(circle.Center + u * circle.Radius);
        for (int i = 1; i <= Segments; i++)
        {
            float angle = i * 2 * MathF.PI / Segments;
            var next = projection.ToScreen(circle.Center + (u * MathF.Cos(angle) + v * MathF.Sin(angle)) * circle.Radius);
            session.DrawLine(previous, next, color, 2f);
            previous = next;
        }
    }

    private static void DrawBox(CanvasDrawingSession session, MeshProjection projection, Vector3 min, Vector3 max, Color color)
    {
        Vector3 Corner(int i) => new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z);

        // Twelve edges: each corner joined to the corners that differ from it along one axis.
        for (int i = 0; i < 8; i++)
        {
            for (int axis = 1; axis < 8; axis <<= 1)
            {
                if ((i & axis) == 0)
                    session.DrawLine(projection.ToScreen(Corner(i)), projection.ToScreen(Corner(i | axis)), color, 1f, DashedStroke);
            }
        }
    }

    private static void DrawLabel(CanvasDrawingSession session, CanvasTextFormat format, string text, Vector2 anchor, Color back, Color ink)
    {
        using var layout = new CanvasTextLayout(session, text, format, 1000, 100);
        var bounds = layout.LayoutBounds;
        var origin = anchor + new Vector2(8, -(float)bounds.Height - 8);
        session.FillRoundedRectangle(origin.X - 4, origin.Y - 2, (float)bounds.Width + 8, (float)bounds.Height + 4, 3, 3, back);
        session.DrawTextLayout(layout, origin, ink);
    }
}
