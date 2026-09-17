using System.Diagnostics;
using System.Numerics;
using Consysto.CadPreview.Mesh;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace Consysto.CadPreview.WinUI;

/// <summary>
/// Interactive 3D mesh: drag to orbit, right/middle or Shift+drag to pan, wheel to zoom around the cursor,
/// double-click for the home view.
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

    private readonly CanvasControl _canvas = new();
    private readonly MeshRasterizer _rasterizer = new();
    private readonly DispatcherQueueTimer _settleTimer;
    private Mesh3D? _mesh;
    private MeshView _view = MeshView.Home;
    private bool _interacting;
    private float _interactiveResolution = 1f;
    private DragMode _drag;
    private Vector2 _lastPointer;

    public CadMeshView()
    {
        // A transparent brush keeps the whole area hit-testable.
        Background = new SolidColorBrush(Colors.Transparent);
        Content = _canvas;

        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();

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
        PointerCaptureLost += (_, _) => _drag = DragMode.None;
        DoubleTapped += (_, _) => ResetView();

        // Win2D controls must be detached explicitly, otherwise the device and the control keep each other alive.
        Unloaded += (_, _) =>
        {
            _settleTimer.Stop();
            _canvas.RemoveFromVisualTree();
        };
    }

    private enum DragMode
    {
        None,
        Orbit,
        Pan,
    }

    public Mesh3D? Mesh
    {
        get => _mesh;
        set
        {
            _mesh = value;
            ResetView();
        }
    }

    public void ResetView()
    {
        _view = MeshView.Home;
        _interacting = false;
        _canvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_mesh is null || sender.ActualWidth < 1 || sender.ActualHeight < 1)
            return;

        // Physical pixels keep the model sharp on high-DPI screens; pan is kept in DIPs and scaled the same way.
        float pixelsPerDip = (float)(sender.XamlRoot?.RasterizationScale ?? 1.0) * (_interacting ? _interactiveResolution : 1f);
        int width = Math.Max(1, (int)(sender.ActualWidth * pixelsPerDip));
        int height = Math.Max(1, (int)(sender.ActualHeight * pixelsPerDip));
        var view = _view with { PanX = _view.PanX * pixelsPerDip, PanY = _view.PanY * pixelsPerDip };

        var watch = Stopwatch.StartNew();
        var image = _rasterizer.RenderFrame(_mesh, width, height, view, MeshFit.Sphere, supersample: _interacting ? 1 : 2);
        if (_interacting)
            AdaptResolution(watch.Elapsed.TotalMilliseconds);

        using var bitmap = CanvasBitmap.CreateFromBytes(sender, image.Pixels, image.Width, image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        args.DrawingSession.DrawImage(
            bitmap,
            new Rect(0, 0, sender.ActualWidth, sender.ActualHeight),
            new Rect(0, 0, image.Width, image.Height),
            1f,
            CanvasImageInterpolation.Linear);
    }

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
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_drag == DragMode.None)
            return;

        var position = e.GetCurrentPoint(this).Position;
        var current = new Vector2((float)position.X, (float)position.Y);
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

        _drag = DragMode.None;
        ReleasePointerCapture(e.Pointer);
    }
}
