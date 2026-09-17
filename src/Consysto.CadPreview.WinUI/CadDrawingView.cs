using System.Numerics;
using Consysto.CadPreview.Drawing;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Consysto.CadPreview.WinUI;

/// <summary>Interactive 2D drawing: drag to pan, wheel to zoom around the cursor, double-click to fit.</summary>
/// <remarks>Built without XAML so the library ships no resources the host package would have to merge.</remarks>
public sealed partial class CadDrawingView : UserControl
{
    private const float FitMargin = 16f;

    private readonly CanvasControl _canvas = new();
    private Drawing2D? _drawing;
    private DrawingPainter? _painter;
    private float _scale = 1f;
    private Vector2 _origin;
    private bool _fitPending = true;
    private bool _dragging;
    private Vector2 _lastPointer;

    public CadDrawingView()
    {
        // A transparent brush keeps the whole area hit-testable for panning and zooming.
        Background = new SolidColorBrush(Colors.Transparent);
        Content = _canvas;

        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (_, _) => ResetPainter();
        _canvas.SizeChanged += (_, _) => _canvas.Invalidate();

        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        DoubleTapped += (_, _) => Fit();
        ActualThemeChanged += (_, _) => _canvas.Invalidate();

        // Win2D controls must be detached explicitly, otherwise the device and the control keep each other alive.
        Unloaded += (_, _) =>
        {
            ResetPainter();
            _canvas.RemoveFromVisualTree();
        };
    }

    public Drawing2D? Drawing
    {
        get => _drawing;
        set
        {
            _drawing = value;
            ResetPainter();
            _fitPending = true;
            _canvas.Invalidate();
        }
    }

    public void Fit()
    {
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
    }

    private void ApplyFit(float width, float height)
    {
        var bounds = _drawing!.Bounds;
        float drawingWidth = (float)Math.Max(bounds.Width, 1e-6);
        float drawingHeight = (float)Math.Max(bounds.Height, 1e-6);
        _scale = Math.Max(1e-6f, Math.Min((width - 2 * FitMargin) / drawingWidth, (height - 2 * FitMargin) / drawingHeight));
        _origin = new Vector2((width - drawingWidth * _scale) / 2, (height + drawingHeight * _scale) / 2);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        float factor = MathF.Pow(1.2f, point.Properties.MouseWheelDelta / 120f);
        var cursor = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _origin = cursor - (cursor - _origin) * factor;
        _scale *= factor;
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
        _lastPointer = new Vector2((float)point.Position.X, (float)point.Position.Y);
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        var position = e.GetCurrentPoint(this).Position;
        var current = new Vector2((float)position.X, (float)position.Y);
        _origin += current - _lastPointer;
        _lastPointer = current;
        _fitPending = false;
        _canvas.Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        ReleasePointerCapture(e.Pointer);
    }

    private void ResetPainter()
    {
        _painter?.Dispose();
        _painter = null;
    }
}
