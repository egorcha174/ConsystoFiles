using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Consysto.CadPreview.WinUI;

/// <summary>Draws a Segoe Fluent Icons glyph into a PNG, for places that only accept bitmaps (Files sidebar items).</summary>
public static class GlyphRenderer
{
    public static async Task<byte[]> RenderAsync(string glyph, int size, Color color)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, size, size, 96);
        using (var session = target.CreateDrawingSession())
        using (var format = new CanvasTextFormat
        {
            FontFamily = "Segoe Fluent Icons",
            FontSize = size * 0.75f,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center,
        })
        {
            session.Clear(Microsoft.UI.Colors.Transparent);
            session.DrawText(glyph, new Rect(0, 0, size, size), color, format);
        }

        using var stream = new InMemoryRandomAccessStream();
        await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
