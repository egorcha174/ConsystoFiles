using System.Numerics;

namespace Consysto.CadPreview.Mesh;

public enum MeshSnap
{
    Face,
    Edge,
    Vertex,
}

/// <summary>A point picked on the model, snapped to a corner or a sharp edge when the cursor was close to one.</summary>
/// <param name="Normal">The normal of the face that was hit, towards the viewer.</param>
/// <param name="EdgeStart">With <see cref="MeshSnap.Edge"/>: the welded vertices of the edge.</param>
public readonly record struct MeshPick(Vector3 Point, MeshSnap Snap, Vector3 Normal, int Triangle, int EdgeStart, int EdgeEnd);

/// <summary>A circle in model units.</summary>
public readonly record struct MeshCircle(Vector3 Center, Vector3 Normal, float Radius);

/// <summary>
/// Measuring on a triangle soup: picking under the cursor, snapping, and finding circles in the tessellation.
/// The mesh carries no B-rep, so edges are recovered from it: vertices are welded by position and an edge counts as
/// sharp when its faces meet at a crease or it borders a hole. Built once per mesh, which takes a moment on large parts.
/// </summary>
public sealed class MeshMeasure
{
    // Above this the edge map alone would take hundreds of megabytes; picking still works, snapping to edges does not.
    private const int MaximumTopologyTriangles = 2_000_000;

    // Tessellated cylinders and fillets keep their facets under ~20°; real CAD edges are mostly sharper.
    private static readonly float SharpCosine = MathF.Cos(25f * MathF.PI / 180f);

    // Neighbouring segments of a tessellated arc turn by 360/n degrees; a sharper turn is a corner, the arc ends there.
    private static readonly float ArcTurnCosine = MathF.Cos(40f * MathF.PI / 180f);

    private const int MaximumChain = 4096;
    private const int MaximumRegion = 200_000;

    private readonly Mesh3D mesh;
    private readonly Vector3[] normals;
    private readonly int[] corners = [];
    private readonly Vector3[] positions = [];
    private readonly Dictionary<long, Edge> edges = new(EdgeKeyComparer.Instance);
    private readonly Dictionary<int, List<int>> sharpNeighbours = [];

    public MeshMeasure(Mesh3D mesh)
    {
        this.mesh = mesh;
        var vertices = mesh.Vertices;
        normals = new Vector3[mesh.TriangleCount];
        for (int t = 0; t < normals.Length; t++)
        {
            var normal = Vector3.Cross(vertices[t * 3 + 1] - vertices[t * 3], vertices[t * 3 + 2] - vertices[t * 3]);
            float length = normal.Length();
            normals[t] = length > 0 && float.IsFinite(length) ? normal / length : Vector3.Zero;
        }

        if (mesh.TriangleCount > MaximumTopologyTriangles)
            return;

        (corners, positions) = Weld(mesh);
        for (int t = 0; t < normals.Length; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = corners[t * 3 + k], b = corners[t * 3 + (k + 1) % 3];
                if (a == b)
                    continue;

                long key = EdgeKey(a, b);
                edges[key] = edges.TryGetValue(key, out var edge) ? edge with { Second = t, Count = edge.Count + 1 } : new Edge(t, -1, 1);
            }
        }

        foreach (var (key, edge) in edges)
        {
            if (!IsSharp(edge))
                continue;

            int a = (int)(key >> 32), b = (int)key;
            Neighbours(a).Add(b);
            Neighbours(b).Add(a);
        }
    }

    public Mesh3D Mesh => mesh;

    /// <summary>False on meshes too large to recover their edges: then snapping stops at corners of the hit face.</summary>
    public bool HasEdges => positions.Length > 0;

    /// <summary>The nearest face under a screen point, snapped to a vertex or a sharp edge within the tolerance.</summary>
    public MeshPick? Pick(MeshProjection projection, Vector2 screen, float snapDistance)
    {
        var (origin, direction) = projection.Ray(screen);
        var vertices = mesh.Vertices;
        int hit = -1;
        float nearest = float.PositiveInfinity;
        for (int t = 0; t < normals.Length; t++)
        {
            if (normals[t] == Vector3.Zero)
                continue;
            if (Intersect(origin, direction, vertices[t * 3], vertices[t * 3 + 1], vertices[t * 3 + 2], out float distance) && distance < nearest)
            {
                nearest = distance;
                hit = t;
            }
        }

        if (hit < 0)
            return null;

        var point = origin + direction * nearest;
        var normal = Vector3.Dot(normals[hit], projection.Toward) < 0 ? -normals[hit] : normals[hit];

        // Corners first: a click near a corner means the corner, even though it is also near two edges.
        float best = snapDistance;
        Vector3? corner = null;
        for (int k = 0; k < 3; k++)
        {
            var vertex = vertices[hit * 3 + k];
            float distance = Vector2.Distance(projection.ToScreen(vertex), screen);
            if (distance <= best)
            {
                best = distance;
                corner = vertex;
            }
        }

        if (corner is { } snapped)
            return new MeshPick(snapped, MeshSnap.Vertex, normal, hit, -1, -1);

        if (!HasEdges)
            return new MeshPick(point, MeshSnap.Face, normal, hit, -1, -1);

        // The sharp edges of the hit face and those leaving its corners: an edge seen almost end-on borders faces the
        // ray barely touches.
        best = snapDistance;
        (int A, int B, Vector3 Point)? edgeHit = null;
        for (int k = 0; k < 3; k++)
        {
            int a = corners[hit * 3 + k];
            if (!sharpNeighbours.TryGetValue(a, out var neighbours))
                continue;

            foreach (int b in neighbours)
            {
                var start = positions[a];
                var end = positions[b];
                var screenStart = projection.ToScreen(start);
                var screenEnd = projection.ToScreen(end);
                float along = ClosestParameter(screenStart, screenEnd, screen);
                float distance = Vector2.Distance(Vector2.Lerp(screenStart, screenEnd, along), screen);
                if (distance <= best)
                {
                    best = distance;
                    edgeHit = (a, b, Vector3.Lerp(start, end, along));
                }
            }
        }

        return edgeHit is { } e
            ? new MeshPick(e.Point, MeshSnap.Edge, normal, hit, e.A, e.B)
            : new MeshPick(point, MeshSnap.Face, normal, hit, -1, -1);
    }

    /// <summary>The direction of a picked edge, or null when the pick is not on one.</summary>
    public Vector3? EdgeDirection(MeshPick pick)
        => pick.Snap == MeshSnap.Edge ? Vector3.Normalize(positions[pick.EdgeEnd] - positions[pick.EdgeStart]) : null;

    /// <summary>
    /// The circle a pick lies on: the arc of sharp edges through a picked edge, or for a picked face (a hole, a boss,
    /// a cylinder) the circular border of its smooth region nearest the click.
    /// </summary>
    public MeshCircle? FindCircle(MeshPick pick)
    {
        if (!HasEdges)
            return null;

        if (pick.Snap == MeshSnap.Edge)
            return FitCircle(Chain(pick.EdgeStart, pick.EdgeEnd));

        MeshCircle? best = null;
        float bestDistance = float.PositiveInfinity;
        var visited = new HashSet<long>(EdgeKeyComparer.Instance);
        foreach (var (a, b) in RegionBorder(pick.Triangle))
        {
            if (!visited.Add(EdgeKey(a, b)))
                continue;

            var chain = Chain(a, b);
            for (int i = 1; i < chain.Count; i++)
                visited.Add(EdgeKey(chain[i - 1], chain[i]));

            if (FitCircle(chain) is not { } circle)
                continue;

            // Distance of the click from the circle's axis compared with the radius: zero on the cylinder wall.
            var offset = pick.Point - circle.Center;
            var radial = offset - Vector3.Dot(offset, circle.Normal) * circle.Normal;
            float distance = MathF.Abs(radial.Length() - circle.Radius) + MathF.Abs(Vector3.Dot(offset, circle.Normal)) * 0.01f;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = circle;
            }
        }

        return best;
    }

    /// <summary>The circle through three points, or null when they lie on one line.</summary>
    public static MeshCircle? CircleThrough(Vector3 a, Vector3 b, Vector3 c)
    {
        var ab = b - a;
        var ac = c - a;
        var normal = Vector3.Cross(ab, ac);
        float squared = normal.LengthSquared();
        if (squared <= 1e-12f * ab.LengthSquared() * ac.LengthSquared() || squared <= 0)
            return null;

        var center = a + (Vector3.Cross(normal, ab) * ac.LengthSquared() + Vector3.Cross(ac, normal) * ab.LengthSquared()) / (2 * squared);
        return new MeshCircle(center, Vector3.Normalize(normal), Vector3.Distance(center, a));
    }

    /// <summary>
    /// The angle between two picks in degrees: between two edges as lines (0–90), between two faces as the angle their
    /// planes make on the side the viewer sees (90 for a square corner, 180 for faces in one plane), and between an edge
    /// and a face as the line against the plane (0–90).
    /// </summary>
    public float Angle(MeshPick first, MeshPick second)
    {
        var firstEdge = EdgeDirection(first);
        var secondEdge = EdgeDirection(second);
        float cosine = (firstEdge, secondEdge) switch
        {
            ({ } a, { } b) => MathF.Abs(Vector3.Dot(a, b)),
            (null, null) => Vector3.Dot(first.Normal, second.Normal),
            ({ } a, null) => MathF.Sqrt(Math.Max(0, 1 - Square(Vector3.Dot(a, second.Normal)))),
            (null, { } b) => MathF.Sqrt(Math.Max(0, 1 - Square(Vector3.Dot(b, first.Normal)))),
        };

        // Both normals face the viewer, so the faces meet at the supplement of the angle between them.
        if (firstEdge is null && secondEdge is null)
            return 180f - MathF.Acos(Math.Clamp(cosine, -1, 1)) * 180f / MathF.PI;

        return MathF.Acos(Math.Clamp(cosine, -1, 1)) * 180f / MathF.PI;
    }

    private static float Square(float value) => value * value;

    private readonly record struct Edge(int First, int Second, int Count);

    private bool IsSharp(Edge edge)
        => edge.Count != 2
            || normals[edge.First] == Vector3.Zero
            || normals[edge.Second] == Vector3.Zero
            || Vector3.Dot(normals[edge.First], normals[edge.Second]) < SharpCosine;

    private List<int> Neighbours(int vertex)
    {
        if (!sharpNeighbours.TryGetValue(vertex, out var list))
            sharpNeighbours[vertex] = list = new List<int>(2);
        return list;
    }

    private static long EdgeKey(int a, int b) => a < b ? (long)a << 32 | (uint)b : (long)b << 32 | (uint)a;

    // long.GetHashCode folds the halves together with XOR, which collides for nearly every edge of a mesh.
    private sealed class EdgeKeyComparer : IEqualityComparer<long>
    {
        public static readonly EdgeKeyComparer Instance = new();

        public bool Equals(long x, long y) => x == y;

        public int GetHashCode(long key) => (int)((ulong)key * 0x9E3779B97F4A7C15UL >> 32);
    }

    // Exporters write the corners of neighbouring faces as separate copies; equal positions become one vertex.
    private static (int[] Corners, Vector3[] Positions) Weld(Mesh3D mesh)
    {
        var vertices = mesh.Vertices;
        float cell = Math.Max(mesh.Radius * 1e-6f, 1e-12f);
        var index = new Dictionary<(long, long, long), int>(vertices.Length / 2);
        var positions = new List<Vector3>(vertices.Length / 4);
        var corners = new int[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            var key = ((long)MathF.Round(v.X / cell), (long)MathF.Round(v.Y / cell), (long)MathF.Round(v.Z / cell));
            if (!index.TryGetValue(key, out int id))
            {
                id = positions.Count;
                index[key] = id;
                positions.Add(v);
            }

            corners[i] = id;
        }

        return (corners, positions.ToArray());
    }

    // Möller–Trumbore, both sides; the distance may be negative: an orthographic ray starts in the middle of the model.
    private static bool Intersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float distance)
    {
        distance = 0;
        var ab = b - a;
        var ac = c - a;
        var p = Vector3.Cross(direction, ac);
        float determinant = Vector3.Dot(ab, p);
        if (MathF.Abs(determinant) < 1e-20f)
            return false;

        float inverse = 1f / determinant;
        var s = origin - a;
        float u = Vector3.Dot(s, p) * inverse;
        if (u < 0 || u > 1)
            return false;

        var q = Vector3.Cross(s, ab);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0 || u + v > 1)
            return false;

        distance = Vector3.Dot(ac, q) * inverse;
        return float.IsFinite(distance);
    }

    private static float ClosestParameter(Vector2 start, Vector2 end, Vector2 point)
    {
        var segment = end - start;
        float length = segment.LengthSquared();
        return length <= 0 ? 0 : Math.Clamp(Vector2.Dot(point - start, segment) / length, 0, 1);
    }

    /// <summary>Follows sharp edges both ways from one edge while they keep turning gently, as a tessellated arc does.</summary>
    private List<int> Chain(int start, int end)
    {
        var forward = Follow(start, end);
        if (forward.Count > 2 && forward[^1] == start)
            return forward;

        var backward = Follow(end, start);
        backward.Reverse();
        // backward ends with "end, start"; forward starts with "start, end".
        backward.RemoveRange(backward.Count - 2, 2);
        backward.AddRange(forward);
        return backward;
    }

    private List<int> Follow(int from, int to)
    {
        var chain = new List<int> { from, to };
        while (chain.Count < MaximumChain)
        {
            int previous = chain[^2], current = chain[^1];
            if (!sharpNeighbours.TryGetValue(current, out var neighbours) || neighbours.Count != 2)
                break;

            int next = neighbours[0] == previous ? neighbours[1] : neighbours[0];
            var incoming = positions[current] - positions[previous];
            var outgoing = positions[next] - positions[current];
            if (Vector3.Dot(Vector3.Normalize(incoming), Vector3.Normalize(outgoing)) < ArcTurnCosine)
                break;

            chain.Add(next);
            if (next == chain[0])
                break;
        }

        return chain;
    }

    // Sharp edges around the smooth patch of faces the clicked one belongs to.
    private IEnumerable<(int A, int B)> RegionBorder(int triangle)
    {
        var seen = new HashSet<int> { triangle };
        var queue = new Queue<int>();
        queue.Enqueue(triangle);
        while (queue.Count > 0 && seen.Count < MaximumRegion)
        {
            int t = queue.Dequeue();
            for (int k = 0; k < 3; k++)
            {
                int a = corners[t * 3 + k], b = corners[t * 3 + (k + 1) % 3];
                if (a == b || !edges.TryGetValue(EdgeKey(a, b), out var edge))
                    continue;

                if (IsSharp(edge))
                {
                    yield return (a, b);
                    continue;
                }

                int other = edge.First == t ? edge.Second : edge.First;
                if (seen.Add(other))
                    queue.Enqueue(other);
            }
        }
    }

    /// <summary>Least-squares circle through a planar arc of at least four segments that keep turning the same way.</summary>
    private MeshCircle? FitCircle(List<int> chain)
    {
        bool closed = chain.Count > 3 && chain[0] == chain[^1];
        int count = closed ? chain.Count - 1 : chain.Count;
        if (count < 5)
            return null;

        var points = new Vector3[count];
        var centroid = Vector3.Zero;
        for (int i = 0; i < count; i++)
        {
            points[i] = positions[chain[i]];
            centroid += points[i];
        }

        centroid /= count;

        // The turning sense of the arc gives its plane; a straight run of edges has none.
        var normal = Vector3.Zero;
        for (int i = 1; i + 1 < count; i++)
            normal += Vector3.Cross(points[i] - points[i - 1], points[i + 1] - points[i]);
        if (closed)
            normal += Vector3.Cross(points[^1] - points[^2], points[0] - points[^1]) + Vector3.Cross(points[0] - points[^1], points[1] - points[0]);

        float extent = 0;
        foreach (var p in points)
            extent = Math.Max(extent, Vector3.Distance(p, centroid));
        if (extent <= 0 || normal.Length() < 1e-6f * extent * extent)
            return null;

        normal = Vector3.Normalize(normal);
        for (int i = 1; i + 1 < count; i++)
        {
            if (Vector3.Dot(Vector3.Cross(points[i] - points[i - 1], points[i + 1] - points[i]), normal) < 0)
                return null;
        }

        var u = Vector3.Normalize(MathF.Abs(normal.X) < 0.9f ? Vector3.Cross(normal, Vector3.UnitX) : Vector3.Cross(normal, Vector3.UnitY));
        var v = Vector3.Cross(normal, u);

        // Kåsa fit: x² + y² + Dx + Ey + F = 0, solved by the normal equations; coordinates relative to the centroid.
        double sxx = 0, sxy = 0, syy = 0, sx = 0, sy = 0, sxz = 0, syz = 0, sz = 0;
        foreach (var p in points)
        {
            var d = p - centroid;
            if (MathF.Abs(Vector3.Dot(d, normal)) > 0.02f * extent)
                return null;

            double x = Vector3.Dot(d, u), y = Vector3.Dot(d, v), z = -(x * x + y * y);
            sxx += x * x; sxy += x * y; syy += y * y; sx += x; sy += y;
            sxz += x * z; syz += y * z; sz += z;
        }

        if (!Solve3(sxx, sxy, sx, sxy, syy, sy, sx, sy, count, sxz, syz, sz, out double dd, out double ee, out double ff))
            return null;

        double cx = -dd / 2, cy = -ee / 2;
        double radiusSquared = cx * cx + cy * cy - ff;
        if (radiusSquared <= 0)
            return null;

        float radius = (float)Math.Sqrt(radiusSquared);
        var center = centroid + u * (float)cx + v * (float)cy;
        foreach (var p in points)
        {
            if (MathF.Abs(Vector3.Distance(p, center) - radius) > 0.03f * radius)
                return null;
        }

        return new MeshCircle(center, normal, radius);
    }

    private static bool Solve3(
        double a11, double a12, double a13,
        double a21, double a22, double a23,
        double a31, double a32, double a33,
        double b1, double b2, double b3,
        out double x1, out double x2, out double x3)
    {
        double determinant = a11 * (a22 * a33 - a23 * a32) - a12 * (a21 * a33 - a23 * a31) + a13 * (a21 * a32 - a22 * a31);
        x1 = x2 = x3 = 0;
        if (Math.Abs(determinant) < 1e-30)
            return false;

        x1 = (b1 * (a22 * a33 - a23 * a32) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a32 - a22 * b3)) / determinant;
        x2 = (a11 * (b2 * a33 - a23 * b3) - b1 * (a21 * a33 - a23 * a31) + a13 * (a21 * b3 - b2 * a31)) / determinant;
        x3 = (a11 * (a22 * b3 - b2 * a32) - a12 * (a21 * b3 - b2 * a31) + b1 * (a21 * a32 - a22 * a31)) / determinant;
        return true;
    }
}
