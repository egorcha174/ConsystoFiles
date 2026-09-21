using System.Numerics;
using System.Runtime.InteropServices;

namespace Consysto.CadPreview.Mesh;

/// <summary>
/// Meshes kept on disk between runs. Tessellating a part takes seconds, and the pane, the thumbnail and the gallery all
/// ask for the same file, so the triangles are written once in the plainest form there is: nine floats per triangle.
/// </summary>
internal static class MeshCache
{
	private const int HeaderBytes = 16;
	private const int TriangleBytes = 36;

	private static ReadOnlySpan<byte> Signature => "CSMESH1\0"u8;

	public static void Write(string path, Mesh3D mesh)
	{
		var triangles = mesh.Vertices.Length / 3;
		var bytes = new byte[HeaderBytes + triangles * TriangleBytes];

		Signature.CopyTo(bytes);
		BitConverter.TryWriteBytes(bytes.AsSpan(8), (uint)triangles);
		MemoryMarshal.AsBytes(mesh.Vertices.AsSpan(0, triangles * 3)).CopyTo(bytes.AsSpan(HeaderBytes));

		File.WriteAllBytes(path, bytes);
	}

	/// <exception cref="InvalidDataException">The file is not a mesh of this shape, so the caller re-reads the original.</exception>
	public static Mesh3D Read(string path, MeshUp up = MeshUp.Z)
	{
		var bytes = File.ReadAllBytes(path);
		if (bytes.Length < HeaderBytes || !bytes.AsSpan(0, 8).SequenceEqual(Signature))
			throw new InvalidDataException("The cached mesh is damaged.");

		var triangles = BitConverter.ToUInt32(bytes, 8);
		if (triangles > MeshBuilder.MaximumTriangles || HeaderBytes + (long)triangles * TriangleBytes != bytes.Length)
			throw new InvalidDataException("The cached mesh is damaged.");

		var vertices = new Vector3[triangles * 3];
		MemoryMarshal.Cast<byte, Vector3>(bytes.AsSpan(HeaderBytes)).CopyTo(vertices);

		return new Mesh3D(vertices, up);
	}
}
