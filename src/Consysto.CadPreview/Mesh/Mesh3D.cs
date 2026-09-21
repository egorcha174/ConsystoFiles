using System.Numerics;

namespace Consysto.CadPreview.Mesh;

/// <summary>Which model axis points up: Inventor and OBJ exports are Y-up, 3MF build plates are Z-up.</summary>
public enum MeshUp
{
    Y,
    Z,
}

/// <summary>
/// Triangle soup in model units: every three vertices form one triangle. CAD meshes are full of sharp edges,
/// so vertices are never shared between faces and the viewer shades each face flat.
/// </summary>
public sealed class Mesh3D
{
    public Mesh3D(Vector3[] vertices, MeshUp up)
    {
        Vertices = vertices;
        Up = up;
        if (vertices.Length == 0)
            return;

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in vertices)
        {
            min = Vector3.Min(min, vertex);
            max = Vector3.Max(max, vertex);
        }

        Min = min;
        Max = max;
    }

    public Vector3[] Vertices { get; }

    public MeshUp Up { get; }

    public int TriangleCount => Vertices.Length / 3;

    /// <summary>Nothing to show: a file whose geometry this build could not read.</summary>
    public static Mesh3D Empty { get; } = new([], MeshUp.Z);

    public bool IsEmpty => Vertices.Length == 0;

    public Vector3 Min { get; }

    public Vector3 Max { get; }

    public Vector3 Center => (Min + Max) / 2;

    /// <summary>Radius of the sphere around the bounding box, what a camera needs to frame the model.</summary>
    public float Radius => Vector3.Distance(Min, Max) / 2;
}

internal sealed class MeshBuilder
{
    // A corrupt or hostile file must not make a preview allocate gigabytes: 5 M triangles is already ~180 MB.
    public const int MaximumTriangles = 5_000_000;

    private readonly List<Vector3> vertices = [];

    public int TriangleCount => vertices.Count / 3;

    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        if (!IsFinite(a) || !IsFinite(b) || !IsFinite(c))
            return;
        if (TriangleCount >= MaximumTriangles)
            throw new InvalidDataException($"The mesh has more than {MaximumTriangles} triangles.");

        vertices.Add(a);
        vertices.Add(b);
        vertices.Add(c);
    }

    public Mesh3D ToMesh(MeshUp up) => new(vertices.ToArray(), up);

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
