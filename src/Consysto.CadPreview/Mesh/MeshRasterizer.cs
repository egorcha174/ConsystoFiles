using System.Numerics;

namespace Consysto.CadPreview.Mesh;

/// <summary>Premultiplied BGRA pixels, top row first.</summary>
public sealed class RasterImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Pixels { get; } = pixels;
}

/// <summary>Orbit camera around the model: angles in degrees, zoom relative to the fitted size, pan in output pixels.</summary>
public readonly record struct MeshView(float AzimuthDegrees, float ElevationDegrees, float Zoom, float PanX, float PanY)
{
    /// <summary>Three-quarter view from front-right-above, the way CAD home views show a part.</summary>
    public static MeshView Home => new(35, 28, 1, 0, 0);
}

public enum MeshFit
{
    /// <summary>Frames the projected vertices of this particular view: the fullest picture, for thumbnails.</summary>
    Tight,

    /// <summary>Frames the bounding sphere: the model keeps its size while it rotates, for interactive views.</summary>
    Sphere,
}

/// <summary>
/// Software renderer for meshes: orthographic view, flat shading, z-buffer, optional supersampling, transparent
/// background. Needs no GPU device and no visual tree, so it works on any thread and under Native AOT.
/// An instance keeps its buffers between frames; the static helper is for one-off thumbnails.
/// </summary>
public sealed class MeshRasterizer
{
    private const float MarginFraction = 0.06f;

    // The same neutral steel for thumbnails and the interactive view.
    private static readonly Vector3 BaseColor = new(0.62f, 0.66f, 0.70f);

    private float[] depth = [];
    private byte[] shade = [];
    private byte[] pixels = [];

    public static RasterImage RenderThumbnail(Mesh3D mesh, int width, int height)
    {
        var image = new MeshRasterizer().RenderFrame(mesh, width, height, MeshView.Home, MeshFit.Tight, supersample: 2);
        return new RasterImage(image.Width, image.Height, image.Pixels);
    }

    /// <returns>An image over this instance's buffer: valid until the next frame.</returns>
    public RasterImage RenderFrame(Mesh3D mesh, int width, int height, MeshView view, MeshFit fit, int supersample)
    {
        width = Math.Max(width, 1);
        height = Math.Max(height, 1);
        supersample = Math.Clamp(supersample, 1, 4);
        int sampleWidth = width * supersample;
        int sampleHeight = height * supersample;

        EnsureBuffers(sampleWidth * sampleHeight, width * height * 4);
        Array.Fill(depth, float.NegativeInfinity, 0, sampleWidth * sampleHeight);
        Array.Clear(pixels, 0, width * height * 4);
        var image = new RasterImage(width, height, pixels);
        if (mesh.IsEmpty)
            return image;

        var (right, up, toward) = ViewBasis(mesh.Up, view);
        var (scale, offsetX, offsetY) = Frame(mesh, right, up, view, fit, sampleWidth, sampleHeight, supersample);

        // Lights are fixed to the camera: a key light from the upper left and a weak fill from the right.
        var key = Vector3.Normalize(new Vector3(-0.35f, 0.55f, 0.75f));
        var fill = Vector3.Normalize(new Vector3(0.6f, -0.1f, 0.8f));

        var vertices = mesh.Vertices;
        for (int i = 0; i + 2 < vertices.Length; i += 3)
        {
            var a = vertices[i];
            var b = vertices[i + 1];
            var c = vertices[i + 2];
            var normal = Vector3.Cross(b - a, c - a);
            float length = normal.Length();
            if (length <= 0 || !float.IsFinite(length))
                continue;
            normal /= length;

            // Open meshes show their inside: light both sides of a face alike.
            var viewNormal = new Vector3(Vector3.Dot(normal, right), Vector3.Dot(normal, up), Vector3.Dot(normal, toward));
            if (viewNormal.Z < 0)
                viewNormal = -viewNormal;
            float intensity = 0.28f + 0.62f * Math.Max(0, Vector3.Dot(viewNormal, key)) + 0.18f * Math.Max(0, Vector3.Dot(viewNormal, fill));
            byte faceShade = (byte)Math.Clamp(intensity * 255, 0, 255);

            RasterizeTriangle(Project(a), Project(b), Project(c), faceShade, sampleWidth, sampleHeight);
        }

        Resolve(width, height, sampleWidth, supersample);
        return image;

        Vector3 Project(Vector3 v) => new(offsetX + scale * Vector3.Dot(v, right), offsetY - scale * Vector3.Dot(v, up), Vector3.Dot(v, toward));
    }

    private void EnsureBuffers(int samples, int pixelBytes)
    {
        if (depth.Length < samples)
        {
            depth = new float[samples];
            shade = new byte[samples];
        }

        if (pixels.Length != pixelBytes)
            pixels = new byte[pixelBytes];
    }

    private static (Vector3 Right, Vector3 Up, Vector3 Toward) ViewBasis(MeshUp meshUp, MeshView view)
    {
        var worldUp = meshUp == MeshUp.Z ? Vector3.UnitZ : Vector3.UnitY;
        var front = meshUp == MeshUp.Z ? -Vector3.UnitY : Vector3.UnitZ;
        // Stop just short of the poles, where "right" would flip.
        float azimuth = view.AzimuthDegrees * MathF.PI / 180f;
        float elevation = Math.Clamp(view.ElevationDegrees, -89.5f, 89.5f) * MathF.PI / 180f;

        var toward = Vector3.Normalize(
            front * MathF.Cos(azimuth) * MathF.Cos(elevation)
            + Vector3.UnitX * MathF.Sin(azimuth) * MathF.Cos(elevation)
            + worldUp * MathF.Sin(elevation));
        var right = Vector3.Normalize(Vector3.Cross(worldUp, toward));
        var up = Vector3.Cross(toward, right);
        return (right, up, toward);
    }

    private static (float Scale, float OffsetX, float OffsetY) Frame(Mesh3D mesh, Vector3 right, Vector3 up, MeshView view, MeshFit fit, int sampleWidth, int sampleHeight, int supersample)
    {
        float margin = MarginFraction * Math.Min(sampleWidth, sampleHeight);
        float centerX, centerY, scale;

        if (fit == MeshFit.Sphere)
        {
            float radius = Math.Max(mesh.Radius, 1e-6f);
            scale = (Math.Min(sampleWidth, sampleHeight) / 2f - margin) / radius;
            centerX = Vector3.Dot(mesh.Center, right);
            centerY = Vector3.Dot(mesh.Center, up);
        }
        else
        {
            // The projected vertices, not the bounding box corners: a box seen at an angle wastes a lot of space.
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            foreach (var vertex in mesh.Vertices)
            {
                float x = Vector3.Dot(vertex, right);
                float y = Vector3.Dot(vertex, up);
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }

            float extentX = Math.Max(maxX - minX, 1e-6f);
            float extentY = Math.Max(maxY - minY, 1e-6f);
            scale = Math.Min((sampleWidth - 2 * margin) / extentX, (sampleHeight - 2 * margin) / extentY);
            centerX = (minX + maxX) / 2;
            centerY = (minY + maxY) / 2;
        }

        scale *= Math.Max(view.Zoom, 1e-3f);
        float offsetX = sampleWidth / 2f - scale * centerX + view.PanX * supersample;
        float offsetY = sampleHeight / 2f + scale * centerY + view.PanY * supersample;
        return (scale, offsetX, offsetY);
    }

    // Screen coordinates in samples; Z grows towards the viewer.
    private void RasterizeTriangle(Vector3 p0, Vector3 p1, Vector3 p2, byte faceShade, int width, int height)
    {
        float area = Edge(p0, p1, p2.X, p2.Y);
        if (MathF.Abs(area) < 1e-9f)
            return;

        int minX = Math.Max(0, (int)MathF.Floor(Math.Min(p0.X, Math.Min(p1.X, p2.X))));
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling(Math.Max(p0.X, Math.Max(p1.X, p2.X))));
        int minY = Math.Max(0, (int)MathF.Floor(Math.Min(p0.Y, Math.Min(p1.Y, p2.Y))));
        int maxY = Math.Min(height - 1, (int)MathF.Ceiling(Math.Max(p0.Y, Math.Max(p1.Y, p2.Y))));
        if (minX > maxX || minY > maxY)
            return;

        float inverseArea = 1f / area;
        var depth = this.depth;
        var shade = this.shade;

        for (int y = minY; y <= maxY; y++)
        {
            float sampleY = y + 0.5f;
            int row = y * width;

            // Long thin triangles (big flat faces of large parts) cover a sliver of their bounding box:
            // narrow each row to the span where all three edge functions keep the area's sign.
            float spanStart = minX, spanEnd = maxX + 1;
            if (!ClipSpan(p1, p2, sampleY, inverseArea, ref spanStart, ref spanEnd)
                || !ClipSpan(p2, p0, sampleY, inverseArea, ref spanStart, ref spanEnd)
                || !ClipSpan(p0, p1, sampleY, inverseArea, ref spanStart, ref spanEnd))
                continue;

            int firstX = Math.Max(minX, (int)MathF.Floor(spanStart - 0.5f));
            int lastX = Math.Min(maxX, (int)MathF.Ceiling(spanEnd - 0.5f));
            for (int x = firstX; x <= lastX; x++)
            {
                float sampleX = x + 0.5f;
                // Barycentric weights; all share the sign of the area when the sample is inside, whatever the winding.
                float w0 = Edge(p1, p2, sampleX, sampleY) * inverseArea;
                float w1 = Edge(p2, p0, sampleX, sampleY) * inverseArea;
                float w2 = Edge(p0, p1, sampleX, sampleY) * inverseArea;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                    continue;

                float z = w0 * p0.Z + w1 * p1.Z + w2 * p2.Z;
                int index = row + x;
                if (z <= depth[index])
                    continue;

                depth[index] = z;
                shade[index] = faceShade;
            }
        }
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);

    // Edge(a, b, x, y) * inverseArea is linear in x: slope * x + intercept. Keeps [start, end] where it is not negative.
    // The bounds are only a shortcut with a sample of slack; the per-sample weight test still decides.
    private static bool ClipSpan(Vector3 a, Vector3 b, float y, float inverseArea, ref float start, ref float end)
    {
        float slope = (a.Y - b.Y) * inverseArea;
        float intercept = ((b.X - a.X) * (y - a.Y) + (b.Y - a.Y) * a.X) * inverseArea;
        if (MathF.Abs(slope) < 1e-12f)
            return intercept >= -1e-6f;

        float crossing = -intercept / slope;
        if (slope > 0)
            start = Math.Max(start, crossing - 1);
        else
            end = Math.Min(end, crossing + 1);
        return start <= end;
    }

    // Averages each supersample block; uncovered samples count as transparent, which anti-aliases the silhouette.
    private void Resolve(int width, int height, int sampleWidth, int supersample)
    {
        int samples = supersample * supersample;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int covered = 0;
                int shadeSum = 0;
                for (int sy = 0; sy < supersample; sy++)
                {
                    int row = (y * supersample + sy) * sampleWidth;
                    for (int sx = 0; sx < supersample; sx++)
                    {
                        int index = row + x * supersample + sx;
                        if (float.IsNegativeInfinity(depth[index]))
                            continue;
                        covered++;
                        shadeSum += shade[index];
                    }
                }

                if (covered == 0)
                    continue;

                int pixel = (y * width + x) * 4;
                float light = shadeSum / (float)samples;
                pixels[pixel] = (byte)(BaseColor.Z * light);
                pixels[pixel + 1] = (byte)(BaseColor.Y * light);
                pixels[pixel + 2] = (byte)(BaseColor.X * light);
                pixels[pixel + 3] = (byte)(covered * 255 / samples);
            }
        }
    }
}
