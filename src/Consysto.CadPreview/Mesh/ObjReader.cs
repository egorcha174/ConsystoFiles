using System.Globalization;
using System.Numerics;
using System.Text;

namespace Consysto.CadPreview.Mesh;

/// <summary>Geometry of Wavefront OBJ: vertices and polygon faces. Materials, texture coordinates and normals are ignored.</summary>
public static class ObjReader
{
    public static Mesh3D Read(string path) => Read(File.ReadAllBytes(path));

    internal static Mesh3D Read(byte[] bytes)
    {
        ReadOnlySpan<byte> text = bytes;
        var positions = new List<Vector3>();
        // Resolved to absolute indices while reading: negative indices count back from the vertices seen so far.
        var triangles = new List<int>();
        var face = new List<int>(8);

        foreach (var range in text.Split((byte)'\n'))
        {
            var rest = AsciiTokens.TrimLine(text[range]);
            var keyword = AsciiTokens.Next(ref rest);

            if (keyword.SequenceEqual("v"u8))
            {
                positions.Add(new Vector3(AsciiTokens.NextFloat(ref rest), AsciiTokens.NextFloat(ref rest), AsciiTokens.NextFloat(ref rest)));
            }
            else if (keyword.SequenceEqual("f"u8))
            {
                face.Clear();
                for (var token = AsciiTokens.Next(ref rest); !token.IsEmpty; token = AsciiTokens.Next(ref rest))
                {
                    // "7", "7/1", "7//3", "7/1/3": only the vertex index matters here.
                    int slash = token.IndexOf((byte)'/');
                    if (!int.TryParse(slash < 0 ? token : token[..slash], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int index) || index == 0)
                        continue;
                    face.Add(index > 0 ? index - 1 : positions.Count + index);
                }

                // Polygons are fanned from their first corner; CAD exporters write convex faces.
                for (int k = 1; k + 1 < face.Count; k++)
                {
                    triangles.Add(face[0]);
                    triangles.Add(face[k]);
                    triangles.Add(face[k + 1]);
                }
            }
        }

        var builder = new MeshBuilder();
        for (int i = 0; i + 2 < triangles.Count; i += 3)
        {
            int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
            if ((uint)a < (uint)positions.Count && (uint)b < (uint)positions.Count && (uint)c < (uint)positions.Count)
                builder.AddTriangle(positions[a], positions[b], positions[c]);
        }

        return builder.ToMesh(MeshUp.Y);
    }
}
