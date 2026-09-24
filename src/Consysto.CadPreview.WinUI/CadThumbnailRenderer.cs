using System.Numerics;
using Consysto.CadPreview.Drawing;
using Consysto.CadPreview.Mesh;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Data.Pdf;
using Windows.Graphics.DirectX;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Consysto.CadPreview.WinUI;

/// <summary>
/// Off-screen PNG thumbnails: a drawing on a white sheet, a shaded mesh, an image embedded in the file,
/// or the first page of a document that is a PDF inside.
/// </summary>
public static class CadThumbnailRenderer
{
    public static async Task<byte[]?> RenderDrawingAsync(Drawing2D drawing, int size)
    {
        if (drawing.Bounds.IsEmpty || size < 8)
            return null;

        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, size, size, 96);
        using (var painter = new DrawingPainter(device, drawing))
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Colors.White);
            float margin = Math.Max(2f, size * 0.06f);
            var bounds = drawing.Bounds;
            float width = (float)Math.Max(bounds.Width, 1e-6);
            float height = (float)Math.Max(bounds.Height, 1e-6);
            float scale = Math.Min((size - 2 * margin) / width, (size - 2 * margin) / height);
            var origin = new Vector2((size - width * scale) / 2, (size + height * scale) / 2);
            painter.Paint(session, scale, origin, rgb => CadDrawingView.Ink(rgb, darkTheme: false));
        }

        return await EncodePngAsync(target);
    }

    public static async Task<byte[]?> RenderMeshAsync(Mesh3D mesh, int size)
    {
        if (mesh.IsEmpty || size < 8)
            return null;

        // The rasterizer is pure CPU work; keep it off the caller's thread.
        var image = await Task.Run(() => MeshRasterizer.RenderThumbnail(mesh, size, size));
        using var bitmap = CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), image.Pixels, image.Width, image.Height, DirectXPixelFormat.B8G8R8A8UIntNormalized);
        return await EncodePngAsync(bitmap);
    }

    public static async Task<byte[]?> RenderImageAsync(byte[] imageFile, int size)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input))
        {
            writer.WriteBytes(imageFile);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        input.Seek(0);

        using var bitmap = await CanvasBitmap.LoadAsync(device, input);
        float scale = Math.Min((float)size / bitmap.SizeInPixels.Width, (float)size / bitmap.SizeInPixels.Height);
        float width = bitmap.SizeInPixels.Width * scale;
        float height = bitmap.SizeInPixels.Height * scale;

        using var target = new CanvasRenderTarget(device, size, size, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);
            session.DrawImage(bitmap, new Rect((size - width) / 2, (size - height) / 2, width, height), bitmap.Bounds, 1f, CanvasImageInterpolation.HighQualityCubic);
        }

        return await EncodePngAsync(target);
    }

    /// <summary>
    /// A vector picture, drawn at the asked size. The engine of the system understands the common part of SVG —
    /// shapes, paths, fills, strokes — which is what a drawing or a logo is made of. Effects and text laid out by
    /// style sheets may come out plainer than in a browser; a picture that cannot be read at all returns nothing.
    /// </summary>
    public static async Task<byte[]?> RenderSvgAsync(string path, int size)
    {
        if (size < 8)
            return null;

        var device = CanvasDevice.GetSharedDevice();
        if (!CanvasSvgDocument.IsSupported(device))
            return null;

        var markup = await File.ReadAllTextAsync(path);
        using var document = CanvasSvgDocument.LoadFromXml(device, markup);

        using var target = new CanvasRenderTarget(device, size, size, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);
            // A picture without a size of its own is fitted to the square it is asked for
            session.DrawSvg(document, new Size(size, size));
        }

        return await EncodePngAsync(target);
    }

    /// <summary>
    /// The first page of a PDF-shaped document, drawn on white. Used for Adobe Illustrator files, which are PDFs
    /// inside: the artwork is their first page.
    /// </summary>
    public static async Task<byte[]?> RenderPdfPageAsync(string path, int size)
    {
        if (size < 8)
            return null;

        var file = await StorageFile.GetFileFromPathAsync(path);
        var document = await PdfDocument.LoadFromFileAsync(file);
        if (document.PageCount == 0)
            return null;

        using var page = document.GetPage(0);
        var options = new PdfPageRenderOptions { BackgroundColor = Colors.White };
        // The longer side is asked for, so a page of any shape comes back fitting the square
        if (page.Size.Width >= page.Size.Height)
            options.DestinationWidth = (uint)size;
        else
            options.DestinationHeight = (uint)size;

        using var drawn = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(drawn, options);
        drawn.Seek(0);

        var device = CanvasDevice.GetSharedDevice();
        using var bitmap = await CanvasBitmap.LoadAsync(device, drawn);

        using var target = new CanvasRenderTarget(device, size, size, 96);
        using (var session = target.CreateDrawingSession())
        {
            session.Clear(Colors.Transparent);
            session.DrawImage(bitmap, new Rect(
                (size - bitmap.SizeInPixels.Width) / 2.0,
                (size - bitmap.SizeInPixels.Height) / 2.0,
                bitmap.SizeInPixels.Width,
                bitmap.SizeInPixels.Height));
        }

        return await EncodePngAsync(target);
    }

    private static async Task<byte[]> EncodePngAsync(CanvasBitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();
        await bitmap.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
