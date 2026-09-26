using System.Globalization;
using Consysto.CadPreview.Drawing;

namespace Consysto.CadPreview.Print;

/// <summary>
/// Draws a print job from its moves, for G-code that carries no picture of its own: the extruding moves seen from above at
/// an angle, coloured from blue at the bed to orange at the top. Travel moves are left out. A job with a great many moves
/// is thinned by layers, keeping the top one whole, so the drawing stays light enough for a preview.
/// </summary>
public static class GcodeToolpath
{
    private const int MaxPoints = 400_000;

    /// <summary>What is kept while reading; beyond it every other point of a layer is dropped, so memory stays bounded.</summary>
    private const int MaxReadPoints = 4_000_000;
    private const long MaxFileBytes = 512L * 1024 * 1024;
    private const double Cos30 = 0.8660254037844386;

    private sealed class Layer(double z)
    {
        public double Z { get; } = z;

        public List<Point2[]> Paths { get; } = [];

        public int Points { get; set; }
    }

    public static Drawing2D? Load(string path, CancellationToken cancellation = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > MaxFileBytes)
            return null;

        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16));
        var layers = new List<Layer>();
        var byHeight = new Dictionary<long, Layer>();
        var kept = 0;
        var keepEvery = 1;
        var pointIndex = 0;
        var path2 = new List<Point2>();
        Layer? layer = null;

        double x = 0, y = 0, z = 0, e = 0;
        var relative = false;
        var relativeExtrusion = false;
        string? line;
        var count = 0;

        // Slicers mark their layers; what is extruded before the first mark is the purge line along the bed edge
        var marked = false;

        void Flush()
        {
            if (path2.Count >= 2 && layer is not null)
            {
                layer.Paths.Add([.. path2]);
                layer.Points += path2.Count;
                kept += path2.Count;

                // Too much read already: keep one point in two from here on, and thin what was kept the same way
                if (kept > MaxReadPoints)
                {
                    keepEvery *= 2;
                    kept = 0;
                    foreach (var candidate in layers)
                    {
                        for (var i = 0; i < candidate.Paths.Count; i++)
                        {
                            var points = candidate.Paths[i];
                            if (points.Length > 2)
                                candidate.Paths[i] = [.. points.Where((_, index) => index % 2 == 0 || index == points.Length - 1)];
                        }

                        candidate.Points = candidate.Paths.Sum(points => points.Length);
                        kept += candidate.Points;
                    }
                }
            }

            path2.Clear();
        }

        while ((line = reader.ReadLine()) is not null)
        {
            if ((++count & 0xFFFF) == 0)
                cancellation.ThrowIfCancellationRequested();

            if (!marked && IsLayerMark(line))
            {
                Flush();
                marked = true;
                layers.Clear();
                byHeight.Clear();
                kept = 0;
                layer = null;
            }

            var comment = line.IndexOf(';');
            var code = (comment >= 0 ? line.AsSpan(0, comment) : line.AsSpan()).Trim();
            if (code.Length < 2)
                continue;

            var command = code[0];
            if (command is 'G' or 'g')
            {
                var number = Number(code[1..], out _);
                switch (number)
                {
                    case 0 or 1 or 2 or 3:
                        double nx = x, ny = y, nz = z, ne = e, ci = 0, cj = 0, radius = 0;
                        var hasE = false;
                        foreach (var word in Words(code))
                        {
                            var value = Number(word.AsSpan(1), out var ok);
                            if (!ok)
                                continue;
                            switch (char.ToUpperInvariant(word[0]))
                            {
                                case 'X': nx = relative ? x + value : value; break;
                                case 'Y': ny = relative ? y + value : value; break;
                                case 'Z': nz = relative ? z + value : value; break;
                                case 'E': ne = relativeExtrusion || relative ? e + value : value; hasE = true; break;
                                case 'I': ci = value; break;
                                case 'J': cj = value; break;
                                case 'R': radius = value; break;
                            }
                        }

                        var isArc = number is 2 or 3 && (ci != 0 || cj != 0 || radius != 0);
                        var moved = nx != x || ny != y || isArc;
                        var extrudes = hasE && ne > e + 1e-6 && moved;
                        if (nz != z)
                        {
                            Flush();
                            z = nz;
                        }

                        if (extrudes)
                        {
                            if (layer is null || Math.Abs(layer.Z - z) > 1e-4)
                            {
                                Flush();
                                var height = (long)Math.Round(z * 10_000);
                                if (!byHeight.TryGetValue(height, out layer))
                                {
                                    layer = new Layer(z);
                                    layers.Add(layer);
                                    byHeight[height] = layer;
                                }
                            }

                            if (path2.Count == 0)
                                path2.Add(Project(x, y, z));

                            if (isArc)
                            {
                                foreach (var (ax, ay) in Arc(x, y, nx, ny, ci, cj, radius, clockwise: number == 2))
                                {
                                    if (++pointIndex % keepEvery == 0)
                                        path2.Add(Project(ax, ay, z));
                                }
                            }
                            else if (++pointIndex % keepEvery == 0)
                            {
                                path2.Add(Project(nx, ny, z));
                            }
                        }
                        else if (moved)
                        {
                            Flush();
                        }

                        x = nx; y = ny; e = ne;
                        break;
                    case 90: relative = false; break;
                    case 91: relative = true; break;
                    case 92:
                        foreach (var word in Words(code))
                        {
                            var value = Number(word.AsSpan(1), out var ok);
                            if (ok && char.ToUpperInvariant(word[0]) == 'E')
                                e = value;
                        }
                        break;
                }
            }
            else if (command is 'M' or 'm')
            {
                var number = Number(code[1..], out _);
                if (number == 82)
                    relativeExtrusion = false;
                else if (number == 83)
                    relativeExtrusion = true;
            }
        }

        Flush();
        if (layers.Count == 0)
            return null;

        layers.Sort((a, b) => a.Z.CompareTo(b.Z));
        var total = layers.Sum(candidate => (long)candidate.Points);
        var stride = (int)Math.Max(1, Math.Ceiling(total / (double)MaxPoints));

        var drawing = new Drawing2D { SourcePath = path, Version = "G-code", Space = "Toolpath" };
        double bottom = layers[0].Z, top = layers[^1].Z;
        var drawn = layers.Where((_, i) => i % stride == 0 || i == layers.Count - 1).ToList();

        // One layer can hold everything, as in a vase printed as a spiral: then its paths are thinned too
        var pathStride = (int)Math.Max(1, Math.Ceiling(drawn.Sum(candidate => (long)candidate.Points) / (double)MaxPoints));
        foreach (var current in drawn)
        {
            var color = Shade(top > bottom ? (current.Z - bottom) / (top - bottom) : 1);
            var name = current.Z.ToString("0.##", CultureInfo.InvariantCulture);
            for (var p = 0; p < current.Paths.Count; p += pathStride)
                drawing.Primitives.Add(new PolylinePrimitive(color, name, current.Paths[p], closed: false));
            drawing.Layers.Add(name);
        }

        drawing.RecalculateBounds();
        return drawing;
    }

    /// <summary>Points along G2 (clockwise) or G3, by centre offset I/J or radius R; a full circle when it ends where it starts.</summary>
    private static IEnumerable<(double X, double Y)> Arc(double x0, double y0, double x1, double y1, double i, double j, double r, bool clockwise)
    {
        double cx, cy;
        if (i != 0 || j != 0)
        {
            cx = x0 + i;
            cy = y0 + j;
        }
        else
        {
            // Radius form: the centre on the side the direction asks for; a negative R takes the longer arc
            var dx = x1 - x0;
            var dy = y1 - y0;
            var chord = Math.Sqrt(dx * dx + dy * dy);
            if (chord < 1e-9 || Math.Abs(r) < chord / 2)
            {
                yield return (x1, y1);
                yield break;
            }

            var h = Math.Sqrt(r * r - chord * chord / 4) * ((clockwise ^ r < 0) ? -1 : 1);
            cx = x0 + dx / 2 - h * dy / chord;
            cy = y0 + dy / 2 + h * dx / chord;
        }

        var radius = Math.Sqrt((x0 - cx) * (x0 - cx) + (y0 - cy) * (y0 - cy));
        var start = Math.Atan2(y0 - cy, x0 - cx);
        var end = Math.Atan2(y1 - cy, x1 - cx);
        var sweep = clockwise ? start - end : end - start;
        if (sweep <= 1e-9)
            sweep += 2 * Math.PI;

        var steps = (int)Math.Clamp(Math.Ceiling(sweep * radius / 0.5), 4, 360);
        for (var k = 1; k <= steps; k++)
        {
            var angle = start + (clockwise ? -1 : 1) * sweep * k / steps;
            yield return k == steps ? (x1, y1) : (cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
        }
    }

    private static bool IsLayerMark(string line)
        => line.StartsWith(";LAYER_CHANGE", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith(";LAYER:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("; layer num", StringComparison.OrdinalIgnoreCase);

    /// <summary>An isometric view: the bed seen from its front corner, height going up.</summary>
    private static Point2 Project(double x, double y, double z)
        => new((x - y) * Cos30, (x + y) * 0.5 + z);

    /// <summary>From a calm blue at the bed to the orange of Consysto at the top.</summary>
    private static Rgb Shade(double t)
    {
        t = Math.Clamp(t, 0, 1);
        static byte Mix(byte from, byte to, double t) => (byte)Math.Round(from + (to - from) * t);
        return new Rgb(Mix(60, 245, t), Mix(110, 130, t), Mix(200, 40, t));
    }

    private static IEnumerable<string> Words(ReadOnlySpan<char> code)
    {
        var words = new List<string>();
        foreach (var range in code.Split(' '))
        {
            var word = code[range].Trim();
            if (word.Length >= 2)
                words.Add(word.ToString());
        }

        return words;
    }

    private static double Number(ReadOnlySpan<char> text, out bool ok)
    {
        var end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is '.' or '-' or '+'))
            end++;
        ok = double.TryParse(text[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
        return ok ? value : double.NaN;
    }
}
